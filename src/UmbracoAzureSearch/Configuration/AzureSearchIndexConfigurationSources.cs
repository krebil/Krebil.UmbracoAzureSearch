using Microsoft.Extensions.Configuration;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// The appsettings and code sources of the per-index options, and which aliases each one configures.
/// </summary>
internal sealed class AzureSearchIndexConfigurationSources(
    IConfiguration configuration,
    IEnumerable<AzureSearchIndexCodeConfiguration> codeConfigurations)
{
    public const string IndexesKey = "Indexes";
    public const string SuggestionsKey = "Suggestions";

    private readonly AzureSearchIndexCodeConfiguration[] _codeConfigurations = codeConfigurations.ToArray();

    private IConfigurationSection Indexes
        => configuration.GetSection($"{UmbracoAzureSearchOptions.Name}:{IndexesKey}");

    public IConfigurationSection IndexSection(string indexAlias) => Indexes.GetSection(indexAlias);

    public IEnumerable<string> AppSettingsAliases => Indexes.GetChildren().Select(section => section.Key);

    public IEnumerable<string> CodeAliases => _codeConfigurations.Select(c => c.IndexAlias).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AzureSearchIndexCodeConfiguration> CodeConfigurations(string indexAlias)
        => _codeConfigurations.Where(c => string.Equals(c.IndexAlias, indexAlias, StringComparison.OrdinalIgnoreCase));

    public bool HasSuggestions(string indexAlias) => IndexSection(indexAlias).GetSection(SuggestionsKey).Exists();

    public static string LegacySuggestionsKey(string indexAlias)
        => $"{UmbracoAzureSearchOptions.Name}:{SuggestionsKey}:{indexAlias}";

    public static string SuggestionsKeyFor(string indexAlias)
        => $"{UmbracoAzureSearchOptions.Name}:{IndexesKey}:{indexAlias}:{SuggestionsKey}";
}
