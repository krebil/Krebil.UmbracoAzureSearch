namespace UmbracoAzureSearch.Models;

/// <summary>How an index analyzes text for its cultures.</summary>
public enum AnalyzerMode
{
    /// <summary>Shared text fields with <c>standard.lucene</c> for every culture, as before.</summary>
    None,

    /// <summary>One copy of the aggregate text fields and the suggestion field per mapped culture, plus an invariant copy.</summary>
    FieldPerCulture,

    /// <summary>
    /// One physical index per mapped culture, plus a fallback index with <c>standard.lucene</c> when Umbraco has a
    /// language outside the map. Invariant documents go into every index.
    /// </summary>
    IndexPerCulture,
}

/// <summary>
/// The analyzer settings of one index, under <c>Indexes:&lt;alias&gt;:Analyzers</c>.
/// </summary>
public class AnalyzerOptions
{
    /// <summary>The value of <see cref="SuggestionAnalyzer"/> that gives suggestion copies per culture.</summary>
    public const string PerCulture = "PerCulture";

    /// <summary>The analyzer layout. <see cref="AnalyzerMode.None"/> by default.</summary>
    public AnalyzerMode Mode { get; set; } = AnalyzerMode.None;

    /// <summary>
    /// The analyzer of each culture, keyed by culture. A key matches a culture exactly first, then by its language part,
    /// case-insensitive: <c>pt</c> covers <c>pt-BR</c> and <c>pt-PT</c>, and a <c>pt-BR</c> key wins over <c>pt</c>.
    /// </summary>
    public Dictionary<string, string> Cultures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The analyzer of the invariant copy. <c>standard.lucene</c> by default.</summary>
    public string Invariant { get; set; } = "standard.lucene";

    /// <summary>
    /// <see cref="PerCulture"/> (default) gives suggestion copies that follow the culture analyzers. A fixed analyzer name
    /// gives one shared suggestion field with that analyzer.
    /// </summary>
    public string SuggestionAnalyzer { get; set; } = PerCulture;
}
