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

    /// <summary>
    /// Writes every field the Umbraco Search indexers produce. Off writes only the opted-in <see cref="Fields"/>
    /// and the Umbraco system fields (<c>Umb_*</c>). On by default.
    /// </summary>
    public bool AutomaticFields { get; set; } = true;

    /// <summary>Opted-in fields and their attributes, keyed by Umbraco field name (case-insensitive).</summary>
    public Dictionary<string, FieldOptions> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The content type aliases the index holds. Null holds every content type. Code only.</summary>
    public IReadOnlyList<string>? ContentTypes { get; internal set; }

    /// <summary>
    /// The cultures the index holds. A key matches the culture or its language part, so <c>pt</c> holds <c>pt-BR</c>.
    /// Invariant documents are always held. Null holds every culture. Code only.
    /// </summary>
    public IReadOnlyList<string>? Cultures { get; internal set; }

    /// <summary>The <c>IContentIndexer</c> types that run for this index only. Code only.</summary>
    public IReadOnlyList<Type> Indexers { get; internal set; } = [];
}
