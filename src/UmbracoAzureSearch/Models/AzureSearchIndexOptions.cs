using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Umbraco.Cms.Core.DeliveryApi;
using UmbracoAzureSearch.Services.Indexer;

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
    /// Rebuilds into a second Azure index behind an Azure alias and swaps when the rebuild succeeds, so searches keep
    /// working during a rebuild. Off by default. Has no effect on serverless services.
    /// </summary>
    public bool ZeroDowntimeRebuild { get; set; }

    /// <summary>
    /// With <see cref="ZeroDowntimeRebuild"/>, the swap is skipped when the new index has fewer documents than this
    /// percentage of the old index. 50 by default; 0 turns the check off.
    /// </summary>
    public int RebuildMinimumDocumentPercent { get; set; } = 50;

    /// <summary>
    /// The documents a rebuild sends to Azure in one request, 1 to 1000. 1 (the default) sends each item at once. Only the
    /// writes of the rebuild are batched; other writes during a rebuild are sent at once.
    /// </summary>
    public int RebuildBatchSize { get; set; } = 1;

    /// <summary>With <see cref="ZeroDowntimeRebuild"/>, the seconds between the swap and the delete of the old index. 10 by default.</summary>
    public int RebuildDeleteDelaySeconds { get; set; } = 10;

    /// <summary>
    /// A rebuild clears the cached <c>umbracoIndexDocument</c> rows, so every item is read again from content. The cache is
    /// shared by all indexes. Off by default.
    /// </summary>
    public bool ClearDocumentCacheOnRebuild { get; set; }

    /// <summary>
    /// The scoring profile a query uses when it names none. Unset: <c>umbraco-boosts</c> when a field has a boost,
    /// otherwise the package's <c>relevanceBoost</c> profile.
    /// </summary>
    public string? DefaultScoringProfile { get; set; }

    /// <summary>Spell check settings, under <c>Indexes:&lt;alias&gt;:SpellCheck</c>. Off by default.</summary>
    public SpellCheckOptions SpellCheck { get; set; } = new();

    /// <summary>Analyzer settings, under <c>Indexes:&lt;alias&gt;:Analyzers</c>.</summary>
    public AnalyzerOptions Analyzers { get; set; } = new();

    /// <summary>
    /// Writes every field the Umbraco Search indexers produce. Off writes only the opted-in <see cref="Fields"/>,
    /// the <see cref="IndexHandlerFields"/> and the Umbraco system fields (<c>Umb_*</c>). On by default.
    /// </summary>
    public bool AutomaticFields { get; set; } = true;

    /// <summary>
    /// The fields of the Delivery API index handlers (<c>IContentIndexHandler</c>), by field name. They count as opted in,
    /// with the automatic attributes, so Delivery API filters and sorts keep working when <see cref="AutomaticFields"/> is
    /// off. An entry in <see cref="Fields"/> still changes their attributes. Filled from the registered handlers.
    /// </summary>
    public IReadOnlyDictionary<string, FieldType> IndexHandlerFields { get; internal set; }
        = new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Changes to the Azure index definition, run after the package builds it, for every Azure index of the alias.
    /// The package does not validate them. Code only.
    /// </summary>
    public IReadOnlyList<Action<SearchIndex, AzureSearchIndexContext>> IndexConfigurations { get; internal set; } = [];

    /// <summary>
    /// Changes to each Azure query, run after the package sets its own search options. The package does not validate them.
    /// Code only.
    /// </summary>
    public IReadOnlyList<Action<SearchOptions, AzureSearchRequest>> SearchConfigurations { get; internal set; } = [];

    /// <summary>
    /// The attributes a field gets in the index, with the rule the indexer uses, or null when the index does not keep the
    /// field. Use the options from <c>IOptionsMonitor&lt;AzureSearchIndexOptions&gt;.Get(alias)</c>. Only those options
    /// include the <see cref="IndexHandlerFields"/>.
    /// </summary>
    /// <param name="fieldName">The Umbraco field name (case-insensitive).</param>
    public AzureSearchFieldAttributes? ResolveField(string fieldName)
        => new IndexDefinition(this).Resolve(fieldName) is { } field
            ? new AzureSearchFieldAttributes(field.Searchable, field.Filterable, field.Facetable, field.Sortable, field.Suggestable)
            : null;
}
