using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Search.Core.Configuration;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.Scoring;
using UmbracoAzureSearch.Services.IndexManager;

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
    PhysicalIndexes physicalIndexes,
    ScoringProfileSources scoringProfileSources,
    ILogger<AzureSearchIndexStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogLegacySuggestions();
        LogServerlessRebuild();

        var errors = new List<string>();
        foreach (var alias in sources.AppSettingsAliases)
        {
            CheckIndex(alias, $"{UmbracoAzureSearchOptions.Name}:{AzureSearchIndexConfigurationSources.IndexesKey}:{alias}", errors);
        }

        foreach (var alias in sources.CodeAliases)
        {
            CheckIndex(alias, $"ConfigureAzureSearchIndex(\"{alias}\")", errors);
        }

        if (errors.Count == 0)
        {
            await CheckIndexCountAsync(errors);
            await CheckScoringProfilesAsync(errors, cancellationToken);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Azure AI Search index configuration is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors.Distinct())}");
        }
    }

    // Azure allows 100 scoring profiles per index.
    private const int MaxScoringProfiles = 100;

    // The profiles of every index this package serves must fit the index built from code. The providers can return
    // profiles for an index without a configuration entry, so every registered index of this package is checked.
    private async Task CheckScoringProfilesAsync(List<string> errors, CancellationToken cancellationToken)
    {
        var aliases = indexOptions.Value.GetContentIndexRegistrations()
            .Where(r => typeof(IAzureSearchIndexer).IsAssignableFrom(r.Indexer) || typeof(AzureSearchIndexer).IsAssignableFrom(r.Indexer))
            .Select(r => r.IndexAlias);
        foreach (var alias in aliases)
        {
            var profiles = await scoringProfileSources.GetAsync(alias, cancellationToken);
            var definition = new IndexDefinition(azureSearchIndexOptions.Get(alias)).WithProviderProfiles(profiles);
            // ConfigureIndex is not run here, because it would see a scratch index. It can add fields and profiles, so with a
            // callback a missing field or default profile is left to Azure.
            var hasCallbacks = definition.Options.IndexConfigurations.Count > 0;
            var index = IndexSchema.Build(new PhysicalIndex(alias.ToLowerInvariant(), null), alias, definition, runCallbacks: false);
            var source = $"Scoring profiles of \"{alias}\"";

            errors.AddRange(index.ScoringProfiles
                .SelectMany(p => IndexSchema.ProfileProblems(p, index))
                .Where(problem => !hasCallbacks || !problem.EndsWith("which the index does not have", StringComparison.Ordinal))
                .Select(problem => $"{source}: {problem}"));
            if (!hasCallbacks && index.ScoringProfiles.All(p => p.Name != definition.DefaultScoringProfile))
            {
                errors.Add($"{source}: the default scoring profile \"{definition.DefaultScoringProfile}\" is not a profile of the index.");
            }

            if (index.ScoringProfiles.Count > MaxScoringProfiles)
            {
                errors.Add($"{source}: the index has {index.ScoringProfiles.Count} scoring profiles, and Azure allows {MaxScoringProfiles}.");
            }
        }
    }

    // IndexPerCulture multiplies the Azure indexes of an alias, so the total is checked against the tier once an index uses it.
    private async Task CheckIndexCountAsync(List<string> errors)
    {
        var aliases = indexOptions.Value.GetContentIndexRegistrations().Select(r => r.IndexAlias)
            .Concat(sources.AppSettingsAliases.Concat(sources.CodeAliases).Select(RegisteredAlias).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var definitions = aliases.Select(alias => (Alias: alias, Definition: new IndexDefinition(azureSearchIndexOptions.Get(alias)))).ToArray();
        if (!definitions.Any(d => d.Definition.IndexPerCulture))
            return;

        var counts = new List<string>();
        var total = 0;
        foreach (var (alias, definition) in definitions)
        {
            var count = 1;
            if (definition.IndexPerCulture)
            {
                // Unknown languages count the fallback index, so the check errs on the safe side.
                count = definition.Options.Analyzers.Cultures.Count + (await physicalIndexes.FallbackNeededAsync(definition) is false ? 0 : 1);
            }

            total += count;
            counts.Add($"{alias}: {count}");
        }

        var maxIndexes = azureSearchOptions.Value.MaxIndexes;
        if (total > maxIndexes)
        {
            errors.Add($"The Umbraco indexes need {total} Azure indexes ({string.Join(", ", counts)}), but the service allows {maxIndexes} "
                       + $"({UmbracoAzureSearchOptions.Name}:MaxIndexes). Map fewer cultures, or use FieldPerCulture.");
        }
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

    // Serverless services have no aliases, so the option is ignored there.
    private void LogServerlessRebuild()
    {
        if (!azureSearchOptions.Value.IsServerless)
            return;

        foreach (var alias in sources.AppSettingsAliases.Concat(sources.CodeAliases).Select(RegisteredAlias).OfType<string>().Distinct())
        {
            if (azureSearchIndexOptions.Get(alias).ZeroDowntimeRebuild)
            {
                logger.LogWarning(
                    "ZeroDowntimeRebuild has no effect for {IndexAlias}: serverless services do not support index aliases, so rebuilds work as without the option",
                    alias);
            }
        }
    }

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
