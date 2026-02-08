using System.Globalization;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Factory;
using UmbracoFacetResult = Umbraco.Cms.Search.Core.Models.Searching.Faceting.FacetResult;
using AzureFacetResult = Azure.Search.Documents.Models.FacetResult;

namespace UmbracoAzureSearch.Services.Searcher;

public class AzureSearchSearcher(
    IServerRoleAccessor serverRoleAccessor,
    IAzureSearchClientFactory azureSearchClientFactory)
    : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchSearcher
{
    public async Task<SearchResult> SearchAsync(
        string indexAlias,
        string? query = null,
        IEnumerable<Filter>? filters = null,
        IEnumerable<Facet>? facets = null,
        IEnumerable<Sorter>? sorters = null,
        string? culture = null,
        string? segment = null,
        AccessContext? accessContext = null,
        int skip = 0,
        int take = 10)
    {
        var searchClient = azureSearchClientFactory.GetSearchClient(indexAlias);

        var searchOptions = new SearchOptions
        {
            Skip = skip,
            Size = take,
            IncludeTotalCount = true,
            SearchMode = SearchMode.All // AND matching for multi-word queries
        };

        // Build the search query
        // No query AND no filters AND no facets AND no sorters = no results;
        // for single-word queries, append * for prefix matching
        var hasFilters = filters?.Any() == true;
        var hasFacets = facets?.Any() == true;
        var hasSorters = sorters?.Any() == true;
        if (string.IsNullOrWhiteSpace(query) && !hasFilters && !hasFacets && !hasSorters)
        {
            return new SearchResult(0, [], []);
        }
        var searchText = string.IsNullOrWhiteSpace(query)
            ? "*"
            : query.Contains(' ') ? query : $"{query}*";

        // Build OData filter
        var filterClauses = new List<string>();

        // Culture filter
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var cultureValue = culture.IndexCulture();
            filterClauses.Add(
                $"({IndexConstants.FieldNames.Culture} eq '{cultureValue}' or {IndexConstants.FieldNames.Culture} eq '{IndexConstants.Variation.InvariantCulture}')");
        }
        else
        {
            filterClauses.Add($"{IndexConstants.FieldNames.Culture} eq '{IndexConstants.Variation.InvariantCulture}'");
        }

        // Segment filter
        if (!string.IsNullOrWhiteSpace(segment))
        {
            var segmentValue = segment.IndexSegment();
            filterClauses.Add(
                $"({IndexConstants.FieldNames.Segment} eq '{segmentValue}' or {IndexConstants.FieldNames.Segment} eq '{IndexConstants.Variation.DefaultSegment}')");
        }
        else
        {
            filterClauses.Add($"{IndexConstants.FieldNames.Segment} eq '{IndexConstants.Variation.DefaultSegment}'");
        }

        // User-provided filters
        if (filters != null)
        {
            foreach (var filter in filters)
            {
                var filterClause = BuildFilterClause(filter);
                if (!string.IsNullOrEmpty(filterClause))
                {
                    filterClauses.Add(filterClause);
                }
            }
        }

        // Sorting
        if (sorters != null)
        {
            foreach (var sorter in sorters)
            {
                var sortClause = BuildSortClause(sorter);
                if (!string.IsNullOrEmpty(sortClause))
                {
                    searchOptions.OrderBy.Add(sortClause);
                }
            }
        }

        // Facets - deduplicate by field key (Azure Search doesn't allow multiple facets on same field)
        var facetsList = facets?.ToList() ?? [];
        var facetFieldMap = new Dictionary<string, List<Facet>>();
        var addedFacetFields = new HashSet<string>();
        foreach (var facet in facetsList)
        {
            var (facetExpression, fieldKey) = BuildFacetExpression(facet);
            if (!string.IsNullOrEmpty(facetExpression) && !addedFacetFields.Contains(fieldKey))
            {
                searchOptions.Facets.Add(facetExpression);
                addedFacetFields.Add(fieldKey);
            }
            if (!facetFieldMap.ContainsKey(fieldKey))
            {
                facetFieldMap[fieldKey] = [];
            }
            facetFieldMap[fieldKey].Add(facet);
        }

        // TODO: Add support for access context

        if (filterClauses.Any())
        {
            searchOptions.Filter = string.Join(" and ", filterClauses);
        }

        // Execute search
        var result = await searchClient.SearchAsync<SearchDocument>(searchText, searchOptions);

        // Extract documents from results
        var documents = new List<Document>();
        await foreach (var searchResult in result.Value.GetResultsAsync())
        {
            var doc = searchResult.Document;
            if (doc.TryGetValue(IndexConstants.FieldNames.Key, out var keyValue) &&
                Guid.TryParse(keyValue?.ToString(), out var key) &&
                doc.TryGetValue(IndexConstants.FieldNames.ObjectType, out var objectTypeValue) &&
                Enum.TryParse<UmbracoObjectTypes>(objectTypeValue?.ToString(), out var objectType))
            {
                documents.Add(new Document(key, objectType));
            }
        }

        // Parse facet results
        var facetResults = ParseFacetResults(result.Value.Facets, facetFieldMap);

        return new SearchResult(result.Value.TotalCount ?? 0, documents.ToArray(), facetResults.ToArray());
    }

    private static string BuildFilterClause(Filter filter)
    {
        return filter switch
        {
            KeywordFilter keywordFilter => BuildKeywordFilter(keywordFilter),
            IntegerExactFilter integerExactFilter => BuildIntegerExactFilter(integerExactFilter),
            IntegerRangeFilter integerRangeFilter => BuildIntegerRangeFilter(integerRangeFilter),
            DecimalExactFilter decimalExactFilter => BuildDecimalExactFilter(decimalExactFilter),
            DecimalRangeFilter decimalRangeFilter => BuildDecimalRangeFilter(decimalRangeFilter),
            DateTimeOffsetExactFilter dateTimeOffsetExactFilter => BuildDateTimeOffsetExactFilter(dateTimeOffsetExactFilter),
            DateTimeOffsetRangeFilter dateTimeOffsetRangeFilter => BuildDateTimeOffsetRangeFilter(dateTimeOffsetRangeFilter),
            TextFilter textFilter => BuildTextFilter(textFilter),
            _ => string.Empty
        };
    }

    private static string BuildIntegerExactFilter(IntegerExactFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Integers}";
        return BuildNumericExactFilter(fieldName, filter.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)),
            filter.Negate);
    }

    private static string BuildDecimalExactFilter(DecimalExactFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Decimals}";
        return BuildNumericExactFilter(fieldName, filter.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)),
            filter.Negate);
    }

    private static string BuildNumericExactFilter(string fieldName, IEnumerable<string> rawValues, bool negate)
    {
        var values = rawValues.Select(v => $"{fieldName}/any(f: f eq {v})");
        var clause = string.Join(" or ", values);

        return negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildKeywordFilter(KeywordFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Keywords}";
        var values = filter.Values.Select(v => $"k eq '{EscapeODataString(v)}'");
        var condition = string.Join(" or ", values);

        var clause = $"{fieldName}/any(k: {condition})";
        return filter.Negate ? $"not ({clause})" : clause;
    }

    private static string EscapeODataString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildIntegerRangeFilter(IntegerRangeFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Integers}";
        var ranges = filter.Ranges.Select(r =>
            $"{fieldName}/any(f: f ge {r.MinValue} and f lt {r.MaxValue})");
        var clause = string.Join(" or ", ranges);

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildDecimalRangeFilter(DecimalRangeFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Decimals}";
        var ranges = filter.Ranges.Select(r =>
            $"{fieldName}/any(f: f ge {r.MinValue} and f lt {r.MaxValue})");
        var clause = string.Join(" or ", ranges);

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildDateTimeOffsetExactFilter(DateTimeOffsetExactFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}";
        var values = filter.Values.Select(v =>
            $"{fieldName}/any(f: f eq {v.ToString("O", CultureInfo.InvariantCulture)})");
        var clause = string.Join(" or ", values);

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildDateTimeOffsetRangeFilter(DateTimeOffsetRangeFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}";
        var ranges = filter.Ranges.Select(r =>
            $"{fieldName}/any(f: f ge {r.MinValue:O} and f lt {r.MaxValue:O})");
        var clause = string.Join(" or ", ranges);

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildTextFilter(TextFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Texts}";
        // Use search.ismatch for full-text search on collection fields
        // Append * for prefix matching on each value
        var values = filter.Values.Select(v =>
            $"search.ismatch('{EscapeODataString(v)}*', '{fieldName}')");
        var clause = string.Join(" or ", values);

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildSortClause(Sorter sorter)
    {
        var direction = sorter.Direction == Direction.Ascending ? "asc" : "desc";

        return sorter switch
        {
            IntegerSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Integers}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            DecimalSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Decimals}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            DateTimeOffsetSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            KeywordSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Keywords} {direction}",
            TextSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Texts}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            ScoreSorter => $"search.score() {direction}",
            _ => string.Empty
        };
    }

    private static (string Expression, string FieldKey) BuildFacetExpression(Facet facet)
    {
        return facet switch
        {
            IntegerExactFacet f => ($"{f.FieldName}{IndexConstants.FieldTypePostfix.Integers},count:10000", $"{f.FieldName}{IndexConstants.FieldTypePostfix.Integers}"),
            IntegerRangeFacet f => BuildIntegerRangeFacetExpression(f),
            DecimalExactFacet f => ($"{f.FieldName}{IndexConstants.FieldTypePostfix.Decimals},count:10000", $"{f.FieldName}{IndexConstants.FieldTypePostfix.Decimals}"),
            DecimalRangeFacet f => BuildDecimalRangeFacetExpression(f),
            DateTimeOffsetExactFacet f => ($"{f.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets},count:10000", $"{f.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}"),
            DateTimeOffsetRangeFacet f => BuildDateTimeOffsetRangeFacetExpression(f),
            KeywordFacet f => ($"{f.FieldName}{IndexConstants.FieldTypePostfix.Keywords},count:10000", $"{f.FieldName}{IndexConstants.FieldTypePostfix.Keywords}"),
            _ => (string.Empty, string.Empty)
        };
    }

    private static (string Expression, string FieldKey) BuildIntegerRangeFacetExpression(IntegerRangeFacet facet)
    {
        var fieldName = $"{facet.FieldName}{IndexConstants.FieldTypePostfix.Integers}";
        var boundaries = facet.Ranges.SelectMany(r => new[] { r.MinValue, r.MaxValue }).Distinct().OrderBy(x => x);
        var valuesExpr = string.Join("|", boundaries);
        return ($"{fieldName},values:{valuesExpr}", fieldName);
    }

    private static (string Expression, string FieldKey) BuildDecimalRangeFacetExpression(DecimalRangeFacet facet)
    {
        var fieldName = $"{facet.FieldName}{IndexConstants.FieldTypePostfix.Decimals}";
        var boundaries = facet.Ranges.SelectMany(r => new[] { r.MinValue, r.MaxValue }).Distinct().OrderBy(x => x);
        var valuesExpr = string.Join("|", boundaries);
        return ($"{fieldName},values:{valuesExpr}", fieldName);
    }

    private static (string Expression, string FieldKey) BuildDateTimeOffsetRangeFacetExpression(DateTimeOffsetRangeFacet facet)
    {
        var fieldName = $"{facet.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}";
        var boundaries = facet.Ranges
            .SelectMany(r => new[] { r.MinValue, r.MaxValue })
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .OrderBy(x => x);
        var valuesExpr = string.Join("|", boundaries.Select(v => v.ToString("O", CultureInfo.InvariantCulture)));
        return ($"{fieldName},values:{valuesExpr}", fieldName);
    }

    private static List<UmbracoFacetResult> ParseFacetResults(
        IDictionary<string, IList<AzureFacetResult>>? azureFacets,
        Dictionary<string, List<Facet>> facetFieldMap)
    {
        var results = new List<UmbracoFacetResult>();
        if (azureFacets == null) return results;

        foreach (var (fieldKey, facetList) in facetFieldMap)
        {
            if (!azureFacets.TryGetValue(fieldKey, out var azureFacetValues)) continue;

            // Only use the first facet definition for each field (duplicates are ignored)
            var facet = facetList.First();
            var facetResult = MapFacetResult(facet, azureFacetValues);
            if (facetResult != null)
            {
                results.Add(facetResult);
            }
        }

        return results;
    }

    private static UmbracoFacetResult? MapFacetResult(Facet facet, IList<AzureFacetResult> azureFacetValues)
    {
        return facet switch
        {
            IntegerExactFacet f => new UmbracoFacetResult(f.FieldName, azureFacetValues
                .Where(v => v.Value != null)
                .Select(v => new IntegerExactFacetValue(Convert.ToInt32(v.Value), v.Count ?? 0))
                .OrderBy(v => v.Key)
                .ToArray()),

            IntegerRangeFacet f => MapIntegerRangeFacetResult(f, azureFacetValues),

            DecimalExactFacet f => new UmbracoFacetResult(f.FieldName, azureFacetValues
                .Where(v => v.Value != null)
                .Select(v => new DecimalExactFacetValue(Convert.ToDecimal(v.Value), v.Count ?? 0))
                .OrderBy(v => v.Key)
                .ToArray()),

            DecimalRangeFacet f => MapDecimalRangeFacetResult(f, azureFacetValues),

            DateTimeOffsetExactFacet f => new UmbracoFacetResult(f.FieldName, azureFacetValues
                .Where(v => v.Value != null)
                .Select(v => new DateTimeOffsetExactFacetValue((DateTimeOffset)v.Value, v.Count ?? 0))
                .OrderBy(v => v.Key)
                .ToArray()),

            DateTimeOffsetRangeFacet f => MapDateTimeOffsetRangeFacetResult(f, azureFacetValues),

            KeywordFacet f => new UmbracoFacetResult(f.FieldName, azureFacetValues
                .Where(v => v.Value != null)
                .Select(v => new KeywordFacetValue(v.Value?.ToString() ?? "", v.Count ?? 0))
                .OrderBy(v => v.Key)
                .ToArray()),

            _ => null
        };
    }

    private static UmbracoFacetResult MapIntegerRangeFacetResult(IntegerRangeFacet facet, IList<AzureFacetResult> azureFacetValues)
    {
        var facetValues = new List<IntegerRangeFacetValue>();

        foreach (var range in facet.Ranges)
        {
            // Find the Azure facet result that matches this range
            // Azure range facets use AsRangeFacetResult<T>() with From/To properties
            var minValue = range.MinValue;
            AzureFacetResult? matchingFacet = null;

            if (minValue.HasValue)
            {
                foreach (var v in azureFacetValues)
                {
                    try
                    {
                        // Try as range facet first
                        var rangeFacet = v.AsRangeFacetResult<long>();
                        if (rangeFacet.From.HasValue && rangeFacet.From.Value == minValue.Value)
                        {
                            matchingFacet = v;
                            break;
                        }
                    }
                    catch
                    {
                        // Not a range facet, try value comparison
                        if (v.Value != null)
                        {
                            try
                            {
                                if (Convert.ToInt64(v.Value) == minValue.Value)
                                {
                                    matchingFacet = v;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            var count = matchingFacet?.Count ?? 0;
            facetValues.Add(new IntegerRangeFacetValue(range.Key, range.MinValue, range.MaxValue, count));
        }

        return new UmbracoFacetResult(facet.FieldName, facetValues.ToArray());
    }

    private static UmbracoFacetResult MapDecimalRangeFacetResult(DecimalRangeFacet facet, IList<AzureFacetResult> azureFacetValues)
    {
        var facetValues = new List<DecimalRangeFacetValue>();

        foreach (var range in facet.Ranges)
        {
            var minValue = range.MinValue;
            var matchingFacet = azureFacetValues.FirstOrDefault(v =>
            {
                if (!minValue.HasValue) return false;
                try
                {
                    var rangeFacet = v.AsRangeFacetResult<double>();
                    if (rangeFacet.From == null) return false;
                    return Math.Abs(rangeFacet.From.Value - (double)minValue.Value) < 0.0001;
                }
                catch
                {
                    return false;
                }
            });

            var count = matchingFacet?.Count ?? 0;
            facetValues.Add(new DecimalRangeFacetValue(range.Key, range.MinValue, range.MaxValue, count));
        }

        return new UmbracoFacetResult(facet.FieldName, facetValues.ToArray());
    }

    private static UmbracoFacetResult MapDateTimeOffsetRangeFacetResult(DateTimeOffsetRangeFacet facet, IList<AzureFacetResult> azureFacetValues)
    {
        var facetValues = new List<DateTimeOffsetRangeFacetValue>();

        foreach (var range in facet.Ranges)
        {
            // Compare by UTC ticks to handle timezone differences
            var minUtcTicks = range.MinValue?.UtcTicks;
            var matchingFacet = minUtcTicks.HasValue
                ? azureFacetValues.FirstOrDefault(v =>
                {
                    try
                    {
                        var rangeFacet = v.AsRangeFacetResult<DateTimeOffset>();
                        return rangeFacet.From?.UtcTicks == minUtcTicks.Value;
                    }
                    catch
                    {
                        return false;
                    }
                })
                : null;

            var count = matchingFacet?.Count ?? 0;
            facetValues.Add(new DateTimeOffsetRangeFacetValue(range.Key, range.MinValue, range.MaxValue, count));
        }

        return new UmbracoFacetResult(facet.FieldName, facetValues.ToArray());
    }
}
