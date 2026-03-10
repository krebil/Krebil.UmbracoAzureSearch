using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Extensions;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Extensions;

namespace UmbracoAzureSearch.Services.Indexer;

public class DocumentMapper
{
    public record MappingResult
    {
        public required List<SearchDocument> Documents { get; init; }
        public required List<IndexFieldMapping> FieldMappings { get; init; }
    }

    public MappingResult MapToDocuments(
        Guid id,
        UmbracoObjectTypes objectType,
        IEnumerable<Variation> variations,
        IEnumerable<IndexField> fields,
        ContentProtection? protection)
    {
        var fieldsByFieldName = fields.GroupBy(field => field.FieldName);
        var allFieldMappings = new List<IndexFieldMapping>();
        var documents = new List<SearchDocument>();

        foreach (var variation in variations)
        {
            var (document, variationMappings) = MapVariationToDocument(
                id,
                objectType,
                variation,
                fieldsByFieldName,
                protection);

            documents.Add(document);
            allFieldMappings.AddRange(variationMappings);
        }

        // Deduplicate field mappings by field name (keep first occurrence)
        var uniqueMappings = allFieldMappings
            .GroupBy(m => m.FieldName)
            .Select(g => g.First())
            .ToList();

        return new MappingResult
        {
            Documents = documents,
            FieldMappings = uniqueMappings
        };
    }

    private (SearchDocument Document, List<IndexFieldMapping> FieldMappings) MapVariationToDocument(
        Guid id,
        UmbracoObjectTypes objectType,
        Variation variation,
        IEnumerable<IGrouping<string, IndexField>> fieldsByFieldName,
        ContentProtection? protection)
    {
        // document variation
        var culture = variation.Culture.IndexCulture();
        var segment = variation.Segment.IndexSegment();

        // document access (no access maps to an empty key for querying)
        Guid[] accessKeys = protection?.AccessIds.Any() is true
            ? protection.AccessIds.ToArray()
            : [Guid.Empty];

        // relevant field values for this variation (including invariant fields)
        IndexField[] variationFields = fieldsByFieldName.Select(
                g =>
                {
                    IndexField[] applicableFields = g.Where(f =>
                        (variation.Culture is not null
                         && variation.Segment is not null
                         && f.Culture == variation.Culture
                         && f.Segment == variation.Segment)
                        || (variation.Culture is not null
                            && f.Culture == variation.Culture
                            && f.Segment is null)
                        || (variation.Segment is not null
                            && f.Culture is null
                            && f.Segment == variation.Segment)
                        || (f.Culture is null && f.Segment is null)
                    ).ToArray();

                    return applicableFields.Any()
                        ? new IndexField(
                            g.Key,
                            new IndexValue
                            {
                                DateTimeOffsets = applicableFields
                                    .SelectMany(f => f.Value.DateTimeOffsets ?? []).NullIfEmpty(),
                                Decimals = applicableFields.SelectMany(f => f.Value.Decimals ?? [])
                                    .NullIfEmpty(),
                                Integers = applicableFields.SelectMany(f => f.Value.Integers ?? [])
                                    .NullIfEmpty(),
                                Keywords = applicableFields.SelectMany(f => f.Value.Keywords ?? [])
                                    .NullIfEmpty(),
                                Texts = applicableFields.SelectMany(f => f.Value.Texts ?? []).NullIfEmpty(),
                                TextsR1 = applicableFields.SelectMany(f => f.Value.TextsR1 ?? []).NullIfEmpty(),
                                TextsR2 = applicableFields.SelectMany(f => f.Value.TextsR2 ?? []).NullIfEmpty(),
                                TextsR3 = applicableFields.SelectMany(f => f.Value.TextsR3 ?? []).NullIfEmpty(),
                            },
                            variation.Culture,
                            variation.Segment
                        )
                        : null;
                }
            )
            .WhereNotNull()
            .ToArray();

        // all text fields for "free text query on all fields"
        var allTexts = variationFields
            .SelectMany(field => field.Value.Texts ?? [])
            .ToArray();
        var allTextsR1 = variationFields
            .SelectMany(field => field.Value.TextsR1 ?? [])
            .ToArray();
        var allTextsR2 = variationFields
            .SelectMany(field => field.Value.TextsR2 ?? [])
            .ToArray();
        var allTextsR3 = variationFields
            .SelectMany(field => field.Value.TextsR3 ?? [])
            .ToArray();

        // explicit document field values - using intermediary mappings
        var fieldMappings = CreateFieldMappings(variationFields).ToList();

        var document = new SearchDocument()
        {
            [IndexConstants.FieldNames.Id] = $"{id.AsKeyword()}_{culture}_{segment}",
            [IndexConstants.FieldNames.ObjectType] = objectType.ToString(),
            [IndexConstants.FieldNames.Key] = id.AsKeyword(),
            [IndexConstants.FieldNames.Culture] = culture,
            [IndexConstants.FieldNames.Segment] = segment,
            [IndexConstants.FieldNames.AccessKeys] = accessKeys.Select(g => g.AsKeyword()).ToArray(),
            [IndexConstants.FieldNames.AllTexts] = allTexts,
            [IndexConstants.FieldNames.AllTextsR1] = allTextsR1,
            [IndexConstants.FieldNames.AllTextsR2] = allTextsR2,
            [IndexConstants.FieldNames.AllTextsR3] = allTextsR3,
        };

        foreach (var mapping in fieldMappings)
        {
            document[mapping.FieldName] = mapping.IsCollection                                                                                                                                                                                
                         ? mapping.Values                                                                                                                                                                                                              
                         : mapping.Values.FirstOrDefault(); 
        }

        return (document, fieldMappings);
    }

    private IEnumerable<IndexFieldMapping> CreateFieldMappings(IndexField[] variationFields)
    {
        foreach (var field in variationFields)
        {
            // Texts (aggregates all text relevance levels for TextFilter support)
            var allTexts = (field.Value.Texts ?? [])
                .Concat(field.Value.TextsR1 ?? [])
                .Concat(field.Value.TextsR2 ?? [])
                .Concat(field.Value.TextsR3 ?? [])
                .Distinct()
                .ToArray();

            // Always create all four text fields when ANY text content exists
            // This ensures consistent schema for TextFilter relevance queries
            if (allTexts.Length > 0)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Texts}",
                    Values = allTexts.OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.String,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = true,
                    IsFacetable = false,
                    SourceField = field
                };

                // TextsR1 (relevance 1) - always create when any text exists
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.TextsR1}",
                    Values = (field.Value.TextsR1 ?? []).OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.String,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = true,
                    IsFacetable = false,
                    SourceField = field
                };

                // TextsR2 (relevance 2) - always create when any text exists
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.TextsR2}",
                    Values = (field.Value.TextsR2 ?? []).OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.String,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = true,
                    IsFacetable = false,
                    SourceField = field
                };

                // TextsR3 (relevance 3) - always create when any text exists
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.TextsR3}",
                    Values = (field.Value.TextsR3 ?? []).OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.String,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = true,
                    IsFacetable = false,
                    SourceField = field
                };
            }

            // Keywords (exact match, filterable, facetable)
            if (field.Value.Keywords?.Any() is true)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Keywords}",
                    Values = field.Value.Keywords.OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.String,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = false,
                    IsFacetable = true,
                    SourceField = field
                };

                // Sortable keyword field (single value - first value for sorting)
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Keywords}{IndexConstants.FieldTypePostfix.Sortable}",
                    Values = [field.Value.Keywords.First()],
                    FieldType = SearchFieldDataType.String,
                    IsCollection = false,
                    IsSortable = true,
                    IsSearchable = false,
                    IsFacetable = false,
                    SourceField = field
                };
            }

            // Integers (collection - filterable, facetable)
            if (field.Value.Integers?.Any() is true)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Integers}",
                    Values = field.Value.Integers.OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.Int64,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = false,
                    IsFacetable = true,
                    SourceField = field
                };

                // Sortable integer field (single value - first value for sorting)
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Integers}{IndexConstants.FieldTypePostfix.Sortable}",
                    Values = [field.Value.Integers.First()],
                    FieldType = SearchFieldDataType.Int64,
                    IsCollection = false,
                    IsSortable = true,
                    IsSearchable = false,
                    IsFacetable = false,
                    SourceField = field
                };
            }

            // Decimals (collection - filterable, facetable)
            if (field.Value.Decimals?.Any() is true)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Decimals}",
                    Values = field.Value.Decimals.OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.Double,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = false,
                    IsFacetable = true,
                    SourceField = field
                };

                // Sortable decimal field (single value - first value for sorting)
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Decimals}{IndexConstants.FieldTypePostfix.Sortable}",
                    Values = [field.Value.Decimals.First()],
                    FieldType = SearchFieldDataType.Double,
                    IsCollection = false,
                    IsSortable = true,
                    IsSearchable = false,
                    IsFacetable = false,
                    SourceField = field
                };
            }

            // DateTimeOffsets (collection - filterable, facetable)
            if (field.Value.DateTimeOffsets?.Any() is true)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}",
                    Values = field.Value.DateTimeOffsets.OfType<object>().ToArray(),
                    FieldType = SearchFieldDataType.DateTimeOffset,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = false,
                    IsFacetable = true,
                    SourceField = field
                };

                // Sortable datetimeoffset field (single value - first value for sorting)
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}{IndexConstants.FieldTypePostfix.Sortable}",
                    Values = [field.Value.DateTimeOffsets.First()],
                    FieldType = SearchFieldDataType.DateTimeOffset,
                    IsCollection = false,
                    IsSortable = true,
                    IsSearchable = false,
                    IsFacetable = false,
                    SourceField = field
                };
            }

            // Sortable text field (combines all text relevance levels)
            var sortableTexts = (field.Value.TextsR1 ?? [])
                .Union(field.Value.TextsR2 ?? [])
                .Union(field.Value.TextsR3 ?? [])
                .Union(field.Value.Texts ?? [])
                .Take(5).ToArray();

            if (sortableTexts.Length > 0)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{IndexConstants.FieldTypePostfix.Texts}{IndexConstants.FieldTypePostfix.Sortable}",
                    Values = [string.Join(" ", sortableTexts).ToLowerInvariant()],
                    FieldType = SearchFieldDataType.String,
                    IsCollection = false,
                    IsSortable = true,
                    IsSearchable = false,
                    IsFacetable = false,
                    SourceField = field
                };
            }
        }
    }
}