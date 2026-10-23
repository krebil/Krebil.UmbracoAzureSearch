namespace UmbracoAzureSearch.Models;

/// <summary>
/// Settings for one index, as named options keyed by the Umbraco index alias. Filled from
/// <c>UmbracoAzureSearch:Indexes:&lt;alias&gt;</c> first, then from <c>ConfigureAzureSearchIndex</c> in code.
/// An alias without an entry uses the defaults.
/// </summary>
public class AzureSearchIndexOptions
{
    /// <summary>Suggestion settings, under <c>Indexes:&lt;alias&gt;:Suggestions</c>.</summary>
    public SuggestionOptions Suggestions { get; set; } = new();
}
