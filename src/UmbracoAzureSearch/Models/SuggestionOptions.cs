namespace UmbracoAzureSearch.Models;

/// <summary>
/// Suggestion settings for one index alias, under <c>UmbracoAzureSearch:Indexes:&lt;alias&gt;:Suggestions</c>.
/// </summary>
public class SuggestionOptions
{
    /// <summary>
    /// Gives the index a suggestion field and suggester, and answers <c>maxSuggestions</c>. On by default.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The Umbraco fields whose texts feed suggestions. Null or empty uses all highest-relevance (R1) texts.
    /// </summary>
    public string[]? Fields { get; set; }
}
