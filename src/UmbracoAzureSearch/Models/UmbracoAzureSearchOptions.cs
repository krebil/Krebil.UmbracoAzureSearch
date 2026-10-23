namespace UmbracoAzureSearch.Models;

public class UmbracoAzureSearchOptions
{
    public const string Name = "UmbracoAzureSearch";
    
    public string? Key { get; set; }
    public string? Endpoint { get; set; }
    
    public bool EnableDebugMode { get; set; }

    public string? Environment { get; set; }

    public bool IsServerless { get; set; }

    // Keyed by Umbraco index alias (before the environment suffix); an alias without an entry uses the defaults.
    [Obsolete("Use UmbracoAzureSearch:Indexes:<alias>:Suggestions or ConfigureAzureSearchIndex. This location is read while the new one is unset.")]
    public Dictionary<string, SuggestionOptions> Suggestions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Obsolete("Use IOptionsMonitor<AzureSearchIndexOptions>.Get(indexAlias).Suggestions.")]
    public SuggestionOptions GetSuggestionOptions(string indexAlias)
        => Suggestions.TryGetValue(indexAlias, out var options) ? options : new SuggestionOptions();
}