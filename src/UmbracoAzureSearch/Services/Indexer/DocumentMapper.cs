using System.Text.RegularExpressions;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Extensions;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Services.Indexer;

public class DocumentMapper
{
    private static readonly Regex HtmlTagPattern =
        new(@"<[^>]+>", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static string[]? StripHtml(IEnumerable<string>? values)
        => values?
            .Select(v => HtmlTagPattern.Replace(v, " ").Trim())
            .Where(v => v.Length > 0)
            .ToArray();

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
        ContentProtection? protection,
        SuggestionOptions suggestionOptions)
        => MapToDocuments(id, objectType, variations, fields, protection,
            new IndexDefinition(new AzureSearchIndexOptions { Suggestions = suggestionOptions }));

    internal MappingResult MapToDocuments(
        Guid id,
        UmbracoObjectTypes objectType,
        IEnumerable<Variation> variations,
        IEnumerable<IndexField> fields,
        ContentProtection? protection,
        IndexDefinition definition)
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
                protection,
                definition);

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
        ContentProtection? protection,
        IndexDefinition definition)
    {
        // document variation
        var culture = variation.Culture.IndexCulture();
        var segment = variation.Segment.IndexSegment();

        // document access (no access maps to an empty key for querying)
        Guid[] accessKeys = protection?.AccessIds.Any() is true
            ? protection.AccessIds.ToArray()
            : [Guid.Empty];

        // relevant field values for this variation (including invariant fields), and the attributes of each
        var all = TextsOf(MergeFields(fieldsByFieldName, variation, _ => true), definition);

        // explicit document field values - using intermediary mappings
        var fieldMappings = all.Fields.SelectMany(f => CreateFieldMappings(f.Field, f.Resolved)).ToList();

        var document = new SearchDocument()
        {
            [IndexConstants.FieldNames.Id] = $"{id.AsKeyword()}_{culture}_{segment}",
            [IndexConstants.FieldNames.ObjectType] = objectType.ToString(),
            [IndexConstants.FieldNames.Key] = id.AsKeyword(),
            [IndexConstants.FieldNames.Culture] = culture,
            [IndexConstants.FieldNames.Segment] = segment,
            [IndexConstants.FieldNames.AccessKeys] = accessKeys.Select(g => g.AsKeyword()).ToArray(),
        };

        // FieldPerCulture: a mapped culture writes its culture texts to the culture copies and its invariant texts to
        // the invariant copies. An invariant document writes the invariant copies and the shared fields, so queries
        // in any culture find it. An unmapped culture writes the shared fields, as without copies.
        var suffix = definition.CultureSuffix(variation.Culture);
        Texts shared = suffix is null ? all : Texts.Empty;
        WriteAggregates(document, shared, null);
        if (definition.FieldPerCulture)
        {
            if (suffix is not null)
            {
                var cultureTexts = TextsOf(MergeFields(fieldsByFieldName, variation, f => f.Culture is not null), definition);
                var invariantTexts = TextsOf(MergeFields(fieldsByFieldName, variation, f => f.Culture is null), definition);
                WriteCopies(document, fieldMappings, cultureTexts, suffix, definition);
                WriteCopies(document, fieldMappings, invariantTexts, IndexDefinition.InvariantSuffix, definition);
            }
            else if (variation.Culture is null)
            {
                WriteCopies(document, fieldMappings, all, IndexDefinition.InvariantSuffix, definition);
            }
        }

        if (definition.Options.Suggestions.Enabled)
        {
            // Without suggestion copies every document fills the shared field, and the culture filter does the rest.
            var sharedSuggestions = definition.SuggestionsPerCulture ? shared : all;
            document[IndexConstants.FieldNames.Suggestion] = SuggestionText(sharedSuggestions, definition);
        }

        foreach (var mapping in fieldMappings)
        {
            document[mapping.FieldName] = mapping.IsCollection
                ? mapping.Values
                : mapping.Values.FirstOrDefault();
        }

        return (document, fieldMappings);
    }

    // The texts of one part of a variation (all of it, its culture fields or its invariant fields) that go into the index.
    private sealed record Texts(
        (IndexField Field, ResolvedField Resolved)[] Fields,
        string[] AllTexts,
        string[] AllTextsR1,
        string[] AllTextsR2,
        string[] AllTextsR3)
    {
        public static readonly Texts Empty = new([], [], [], [], []);
    }

    private static Texts TextsOf(IndexField[] fields, IndexDefinition definition)
    {
        // the index definition decides which fields are in the index and their attributes
        var resolvedFields = fields
            .Select(field => (Field: field, Resolved: definition.Resolve(field.FieldName)))
            .Where(f => f.Resolved is not null)
            .Select(f => (f.Field, Resolved: f.Resolved!))
            .ToArray();

        // all searchable text fields for "free text query on all fields"
        var searchable = resolvedFields.Where(f => f.Resolved.Searchable).Select(f => f.Field).ToArray();
        return new Texts(
            resolvedFields,
            searchable.SelectMany(field => field.Value.Texts ?? []).ToArray(),
            searchable.SelectMany(field => field.Value.TextsR1 ?? []).ToArray(),
            searchable.SelectMany(field => field.Value.TextsR2 ?? []).ToArray(),
            searchable.SelectMany(field => field.Value.TextsR3 ?? []).ToArray());
    }

    private static void WriteAggregates(SearchDocument document, Texts texts, string? suffix)
    {
        string Name(string field) => suffix is null ? field : IndexDefinition.CopyName(field, suffix);
        document[Name(IndexConstants.FieldNames.AllTexts)] = texts.AllTexts;
        document[Name(IndexConstants.FieldNames.AllTextsR1)] = texts.AllTextsR1;
        document[Name(IndexConstants.FieldNames.AllTextsR2)] = texts.AllTextsR2;
        document[Name(IndexConstants.FieldNames.AllTextsR3)] = texts.AllTextsR3;
    }

    // The copies of one suffix: aggregate texts, the suggestion copy, and the text fields of the PerCulture fields.
    private static void WriteCopies(
        SearchDocument document, List<IndexFieldMapping> fieldMappings, Texts texts, string suffix, IndexDefinition definition)
    {
        WriteAggregates(document, texts, suffix);

        if (definition.Options.Suggestions.Enabled && definition.SuggestionsPerCulture)
        {
            document[IndexDefinition.CopyName(IndexConstants.FieldNames.Suggestion, suffix)] = SuggestionText(texts, definition);
        }

        var analyzer = definition.CopyAnalyzer(suffix);
        foreach (var (field, resolved) in texts.Fields.Where(f => f.Resolved is { PerCulture: true, Searchable: true }))
        {
            var copy = resolved with { Analyzer = analyzer, Filterable = false };
            fieldMappings.AddRange(CreateFieldMappings(field, copy)
                .Where(m => m.IsSearchable)
                .Select(m => new IndexFieldMapping
                {
                    FieldName = IndexDefinition.CopyName(m.FieldName, suffix),
                    Values = m.Values,
                    FieldType = m.FieldType,
                    IsCollection = m.IsCollection,
                    IsSortable = false,
                    IsSearchable = true,
                    IsFilterable = false,
                    IsFacetable = false,
                    AnalyzerName = analyzer,
                    SourceField = m.SourceField
                }));
        }
    }

    private static string? SuggestionText(Texts texts, IndexDefinition definition)
    {
        var suggestionTexts = SuggestionTexts(texts.Fields, texts.AllTextsR1, definition.Options.Suggestions);
        return suggestionTexts.Length > 0 ? string.Join(" ", suggestionTexts) : null;
    }

    // One field per name with the values that apply to the variation, from the fields that pass the filter.
    private static IndexField[] MergeFields(
        IEnumerable<IGrouping<string, IndexField>> fieldsByFieldName, Variation variation, Func<IndexField, bool> include)
    {
        IndexField[] variationFields = fieldsByFieldName.Select(
                g =>
                {
                    IndexField[] applicableFields = g.Where(f => include(f) && (
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
                        || (f.Culture is null && f.Segment is null))
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

        // Strip HTML tags from all text values. Property editors (block lists, RTEs) can emit
        // large HTML blobs with inline styles. Azure Search imposes a 32,766-byte UTF-8 term
        // limit on any field that is filterable, sortable, or facetable — including the per-field
        // _texts_sort fields — so we sanitize here rather than at the field definition level.
        return variationFields
            .Select(field => field with
            {
                Value = field.Value with
                {
                    Texts   = StripHtml(field.Value.Texts),
                    TextsR1 = StripHtml(field.Value.TextsR1),
                    TextsR2 = StripHtml(field.Value.TextsR2),
                    TextsR3 = StripHtml(field.Value.TextsR3),
                }
            })
            .ToArray();
    }

    // The configured and suggestable fields with all their texts, or by default every searchable highest-relevance (R1) text.
    private static string[] SuggestionTexts(
        (IndexField Field, ResolvedField Resolved)[] resolvedFields, string[] allTextsR1, SuggestionOptions suggestionOptions)
    {
        var fieldNames = suggestionOptions.Fields ?? [];
        var suggestionFields = resolvedFields
            .Where(f => f.Resolved.Suggestable || fieldNames.Contains(f.Field.FieldName, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Field)
            .ToArray();
        if (suggestionFields.Length == 0 && fieldNames.Length == 0 && !resolvedFields.Any(f => f.Resolved.Suggestable))
            return allTextsR1;

        return suggestionFields
            .SelectMany(field => (field.Value.Texts ?? [])
                .Concat(field.Value.TextsR1 ?? [])
                .Concat(field.Value.TextsR2 ?? [])
                .Concat(field.Value.TextsR3 ?? []))
            .ToArray();
    }

    // The physical fields of one Umbraco field. Texts become four text fields (all levels, then R1 to R3), other values
    // one collection per value type, and each value type gets a single-value "_sort" field when the field is sortable.
    private static IEnumerable<IndexFieldMapping> CreateFieldMappings(IndexField field, ResolvedField resolved)
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
        if (allTexts.Length > 0 && resolved.Searchable)
        {
            yield return TextMapping(IndexConstants.FieldTypePostfix.Texts, allTexts);
            yield return TextMapping(IndexConstants.FieldTypePostfix.TextsR1, field.Value.TextsR1);
            yield return TextMapping(IndexConstants.FieldTypePostfix.TextsR2, field.Value.TextsR2);
            yield return TextMapping(IndexConstants.FieldTypePostfix.TextsR3, field.Value.TextsR3);
        }

        // Keywords (exact match), integers, decimals and dates: filterable and facetable collections
        foreach (var mapping in ValueMappings(IndexConstants.FieldTypePostfix.Keywords, SearchFieldDataType.String, field.Value.Keywords))
            yield return mapping;
        foreach (var mapping in ValueMappings(IndexConstants.FieldTypePostfix.Integers, SearchFieldDataType.Int64, field.Value.Integers))
            yield return mapping;
        foreach (var mapping in ValueMappings(IndexConstants.FieldTypePostfix.Decimals, SearchFieldDataType.Double, field.Value.Decimals))
            yield return mapping;
        foreach (var mapping in ValueMappings(IndexConstants.FieldTypePostfix.DateTimeOffsets, SearchFieldDataType.DateTimeOffset, field.Value.DateTimeOffsets))
            yield return mapping;

        // Sortable text field (combines all text relevance levels)
        var sortableTexts = (field.Value.TextsR1 ?? [])
            .Union(field.Value.TextsR2 ?? [])
            .Union(field.Value.TextsR3 ?? [])
            .Union(field.Value.Texts ?? [])
            .Take(5).ToArray();

        if (sortableTexts.Length > 0 && resolved.Sortable)
        {
            yield return SortMapping(IndexConstants.FieldTypePostfix.Texts, SearchFieldDataType.String, string.Join(" ", sortableTexts).ToLowerInvariant());
        }

        IndexFieldMapping TextMapping(string postfix, IEnumerable<string>? values) => new()
        {
            FieldName = $"{field.FieldName}{postfix}",
            Values = (values ?? []).OfType<object>().ToArray(),
            FieldType = SearchFieldDataType.String,
            IsCollection = true,
            IsSortable = false,
            IsSearchable = resolved.Searchable,
            IsFilterable = resolved.Filterable,
            IsFacetable = false,
            AnalyzerName = resolved.Analyzer,
            SourceField = field
        };

        IEnumerable<IndexFieldMapping> ValueMappings<T>(string postfix, SearchFieldDataType type, IEnumerable<T>? values)
        {
            if (values?.Any() is not true)
                yield break;

            if (resolved.Filterable || resolved.Facetable)
            {
                yield return new IndexFieldMapping
                {
                    FieldName = $"{field.FieldName}{postfix}",
                    Values = values.OfType<object>().ToArray(),
                    FieldType = type,
                    IsCollection = true,
                    IsSortable = false,
                    IsSearchable = false,
                    IsFilterable = resolved.Filterable,
                    IsFacetable = resolved.Facetable,
                    SourceField = field
                };
            }

            // Sortable field (single value - first value for sorting)
            if (resolved.Sortable)
            {
                yield return SortMapping(postfix, type, values.First()!);
            }
        }

        IndexFieldMapping SortMapping(string postfix, SearchFieldDataType type, object value) => new()
        {
            FieldName = $"{field.FieldName}{postfix}{IndexConstants.FieldTypePostfix.Sortable}",
            Values = [value],
            FieldType = type,
            IsCollection = false,
            IsSortable = true,
            IsSearchable = false,
            IsFilterable = false,
            IsFacetable = false,
            SourceField = field
        };
    }
}
