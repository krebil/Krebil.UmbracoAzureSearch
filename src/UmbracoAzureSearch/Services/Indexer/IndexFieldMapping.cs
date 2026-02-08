using Azure.Search.Documents.Indexes.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>
/// Represents a mapping between an index field and its values with metadata for field creation.
/// </summary>
public class IndexFieldMapping
{
    public required string FieldName { get; init; }

    public required object[] Values { get; init; }

    public required SearchFieldDataType FieldType { get; init; }

    public required bool IsCollection { get; init; }

    public required bool IsSortable { get; init; }

    public required bool IsSearchable { get; init; }

    public required bool IsFacetable { get; init; }

    /// <summary>
    /// Reference to the original IndexField for additional metadata if needed
    /// </summary>
    public IndexField? SourceField { get; init; }
}