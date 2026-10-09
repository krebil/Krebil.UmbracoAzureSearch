namespace UmbracoAzureSearch.Models;

/// <summary>
/// Suggestion settings for one index alias, under <c>UmbracoAzureSearch:Suggestions:&lt;alias&gt;</c>.
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

    /// <summary>
    /// Matches the last term with an edit distance of 1. Slower; off by default.
    /// </summary>
    public bool Fuzzy { get; set; }
}
