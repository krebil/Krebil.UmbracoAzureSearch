namespace UmbracoAzureSearch.Models;

/// <summary>The attributes a field gets in an index. See <see cref="AzureSearchIndexOptions.ResolveField"/>.</summary>
/// <param name="Searchable">The texts are searchable and feed the free-text query on all fields.</param>
/// <param name="Filterable">The values can be used in filters.</param>
/// <param name="Facetable">The values can be used in facets.</param>
/// <param name="Sortable">The field can be used to sort.</param>
/// <param name="Suggestable">The texts feed suggestions.</param>
public sealed record AzureSearchFieldAttributes(
    bool Searchable,
    bool Filterable,
    bool Facetable,
    bool Sortable,
    bool Suggestable);
