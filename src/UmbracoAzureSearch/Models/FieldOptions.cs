namespace UmbracoAzureSearch.Models;

/// <summary>
/// The attributes of one opted-in field, under <c>Indexes:&lt;alias&gt;:Fields:&lt;field&gt;</c>.
/// An unset attribute keeps the automatic value while <see cref="AzureSearchIndexOptions.AutomaticFields"/> is on, and is off otherwise.
/// </summary>
public class FieldOptions
{
    /// <summary>The texts of the field are searchable, and feed the free-text query on all fields.</summary>
    public bool? Searchable { get; set; }

    /// <summary>The values of the field can be used in filters.</summary>
    public bool? Filterable { get; set; }

    /// <summary>The values of the field can be used in facets.</summary>
    public bool? Facetable { get; set; }

    /// <summary>The field can be used to sort results.</summary>
    public bool? Sortable { get; set; }

    /// <summary>The texts of the field feed suggestions. Needs <see cref="Searchable"/>.</summary>
    public bool? Suggestable { get; set; }

    /// <summary>The Azure analyzer of the searchable texts, e.g. <c>en.microsoft</c>. Unset uses <c>standard.lucene</c>.</summary>
    public string? Analyzer { get; set; }

    /// <summary>The relevance boost of the field. Stored for scoring profiles; it has no effect yet.</summary>
    public double? Boost { get; set; }
}
