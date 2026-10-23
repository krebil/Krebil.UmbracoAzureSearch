using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Search.Core.Configuration;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// Checks the per-index options when the host starts. An error stops startup; every error is listed in one exception.
/// Validators of <see cref="AzureSearchIndexOptions"/> (<see cref="IValidateOptions{TOptions}"/>) run for every configured alias.
/// </summary>
internal sealed class AzureSearchIndexStartupCheck(
    AzureSearchIndexConfigurationSources sources,
    IOptions<IndexOptions> indexOptions,
    IOptions<UmbracoAzureSearchOptions> azureSearchOptions,
    IOptionsMonitor<AzureSearchIndexOptions> azureSearchIndexOptions,
    ILogger<AzureSearchIndexStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogLegacySuggestions();

        var errors = new List<string>();
        foreach (var alias in sources.AppSettingsAliases)
        {
            CheckIndex(alias, $"{UmbracoAzureSearchOptions.Name}:{AzureSearchIndexConfigurationSources.IndexesKey}:{alias}", errors);
        }

        foreach (var alias in sources.CodeAliases)
        {
            CheckIndex(alias, $"ConfigureAzureSearchIndex(\"{alias}\")", errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Azure AI Search index configuration is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors.Distinct())}");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void CheckIndex(string alias, string source, List<string> errors)
    {
        var registeredAlias = RegisteredAlias(alias);
        if (registeredAlias is null)
        {
            var registered = indexOptions.Value.GetContentIndexRegistrations().Select(r => r.IndexAlias).Order();
            errors.Add($"{source} configures the index \"{alias}\", but Umbraco Search does not register an index with that alias. "
                       + $"Registered content indexes: {string.Join(", ", registered)}.");
            return;
        }

        try
        {
            _ = azureSearchIndexOptions.Get(registeredAlias);
        }
        catch (OptionsValidationException exception)
        {
            errors.AddRange(exception.Failures.Select(failure => $"{source}: {failure}"));
        }
    }

    // Configuration keys are case-insensitive, so an alias matches its registration in any case.
    private string? RegisteredAlias(string alias)
        => indexOptions.Value.GetIndexRegistration(alias)?.IndexAlias
           ?? indexOptions.Value.GetContentIndexRegistrations()
               .FirstOrDefault(r => string.Equals(r.IndexAlias, alias, StringComparison.OrdinalIgnoreCase))?.IndexAlias;

    private void LogLegacySuggestions()
    {
#pragma warning disable CS0618
        foreach (var alias in azureSearchOptions.Value.Suggestions.Keys)
#pragma warning restore CS0618
        {
            var oldKey = AzureSearchIndexConfigurationSources.LegacySuggestionsKey(alias);
            var newKey = AzureSearchIndexConfigurationSources.SuggestionsKeyFor(alias);
            if (sources.HasSuggestions(alias))
            {
                logger.LogWarning(
                    "{OldKey} is ignored because {NewKey} is also set. Remove the old key.",
                    oldKey, newKey);
            }
            else
            {
                logger.LogWarning(
                    "{OldKey} is deprecated and will be removed. Move the settings to {NewKey}.",
                    oldKey, newKey);
            }
        }
    }
}
