using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// Fills the options of one index in a fixed order, whatever the order of registration: the old
/// <c>Suggestions:&lt;alias&gt;</c> location, then <c>Indexes:&lt;alias&gt;</c>, then code.
/// </summary>
internal sealed class ConfigureAzureSearchIndexOptions(
    AzureSearchIndexConfigurationSources sources,
    IOptions<UmbracoAzureSearchOptions> azureSearchOptions)
    : IConfigureNamedOptions<AzureSearchIndexOptions>
{
    public void Configure(AzureSearchIndexOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, AzureSearchIndexOptions options)
    {
        if (string.IsNullOrEmpty(name))
            return;

        // The old location applies only while the new one is unset. AzureSearchIndexStartupCheck logs which one is used.
#pragma warning disable CS0618
        if (!sources.HasSuggestions(name) && azureSearchOptions.Value.Suggestions.TryGetValue(name, out var legacySuggestions))
#pragma warning restore CS0618
        {
            options.Suggestions = new SuggestionOptions { Enabled = legacySuggestions.Enabled, Fields = legacySuggestions.Fields };
        }

        sources.IndexSection(name).Bind(options);

        var builder = new AzureSearchIndexBuilder(options);
        foreach (var codeConfiguration in sources.CodeConfigurations(name))
        {
            codeConfiguration.Configure(builder);
        }
    }
}
