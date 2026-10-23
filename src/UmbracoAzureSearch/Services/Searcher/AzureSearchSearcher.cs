using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoFacetResult = Umbraco.Cms.Search.Core.Models.Searching.Faceting.FacetResult;
using AzureFacetResult = Azure.Search.Documents.Models.FacetResult;

namespace UmbracoAzureSearch.Services.Searcher;

public class AzureSearchSearcher(
    IServerRoleAccessor serverRoleAccessor,
    IAzureSearchClientFactory azureSearchClientFactory,
    IOptionsMonitor<AzureSearchIndexOptions> azureSearchIndexOptions,
    ILogger<AzureSearchSearcher> logger)
    : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchSearcher
{
    private const int MaxOrderByClauses = 32;

    // Field names go into the OData text unquoted, so only identifiers are accepted: anything else could end a clause.
    private static readonly System.Text.RegularExpressions.Regex FieldNamePattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Azure accepts 1 to 100 for the Autocomplete top parameter.
    private const int MaxAutocompleteSize = 100;

    // Index aliases already warned about a missing suggester; shared because the searcher is transient.
    private static readonly ConcurrentDictionary<string, bool> MissingSuggesterWarnings = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public Task<SearchResult> SearchAsync(
        string indexAlias,
        string? query = null,
        IEnumerable<Filter>? filters = null,
        IEnumerable<Facet>? facets = null,
        IEnumerable<Sorter>? sorters = null,
        string? culture = null,
        string? segment = null,
        AccessContext? accessContext = null,
        int skip = 0,
        int take = 10,
        int maxSuggestions = 0,
        SearchQueryType queryType = SearchQueryType.Simple,
        SearchMode searchMode = SearchMode.All)
        => SearchCoreAsync(indexAlias, query, filters, facets, sorters, culture, segment,
            accessContext, skip, take, maxSuggestions, queryType, searchMode);

    // Explicit ISearcher implementation forwards to the core method with defaults.
    Task<SearchResult> ISearcher.SearchAsync(
        string indexAlias,
        string? query,
        IEnumerable<Filter>? filters,
        IEnumerable<Facet>? facets,
        IEnumerable<Sorter>? sorters,
        string? culture,
        string? segment,
        AccessContext? accessContext,
        int skip,
        int take,
        int maxSuggestions)
        => SearchCoreAsync(indexAlias, query, filters, facets, sorters, culture, segment,
            accessContext, skip, take, maxSuggestions, SearchQueryType.Simple, SearchMode.All);

    // Azure has no field for a value no document ever had, where Examine has a field with no values. A clause on one is
    // a 400 naming the field; the search is repeated without it, answered as Examine would: a filter on it finds nothing
    // (everything, negated), a sort or facet on it is dropped.
    private static readonly System.Text.RegularExpressions.Regex UnknownFieldPattern =
        new("Could not find a property named '([^']+)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<SearchResult> SearchCoreAsync(
        string indexAlias,
        string? query,
        IEnumerable<Filter>? filters,
        IEnumerable<Facet>? facets,
        IEnumerable<Sorter>? sorters,
        string? culture,
        string? segment,
        AccessContext? accessContext,
        int skip,
        int take,
        int maxSuggestions,
        SearchQueryType queryType,
        SearchMode searchMode)
    {
        var unknownFields = new HashSet<string>();
        while (true)
        {
            try
            {
                return await SearchOnceAsync(indexAlias, query, filters, facets, sorters, culture, segment, accessContext,
                    skip, take, maxSuggestions, queryType, searchMode, unknownFields);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 400
                && UnknownFieldPattern.Match(ex.Message) is { Success: true } match
                && unknownFields.Add(match.Groups[1].Value))
            {
                // Repeated without the field. An error about a field the search did not ask for comes back unchanged
                // the second time and is not swallowed.
            }
        }
    }

    private async Task<SearchResult> SearchOnceAsync(
        string indexAlias,
        string? query,
        IEnumerable<Filter>? filters,
        IEnumerable<Facet>? facets,
        IEnumerable<Sorter>? sorters,
        string? culture,
        string? segment,
        AccessContext? accessContext,
        int skip,
        int take,
        int maxSuggestions,
        SearchQueryType queryType,
        SearchMode searchMode,
        ISet<string> unknownFields)
    {
        var searchClient = azureSearchClientFactory.GetSearchClient(indexAlias);

        var searchOptions = new SearchOptions
        {
            Skip = skip,
            Size = take,
            IncludeTotalCount = true,
            QueryType = queryType,
            SearchMode = searchMode
        };

        // Only a search without any criteria finds nothing, as in the Examine provider; otherwise no query matches all.
        // For single-word queries, append * for prefix matching
        if (query is null && filters is null && facets is null && sorters is null
            && culture is null && segment is null && accessContext is null)
        {
            return new SearchResult(0, [], []);
        }
        var searchText = string.IsNullOrWhiteSpace(query)
            ? "*"
            : query.Contains(' ') ? query : $"{query}*";

        // Build base filter clauses (culture/segment)
        // Escaped like every other value: an unescaped quote could end the clause and turn the whole filter, the
        // access clause included, into something that matches everything.
        var baseFilterClauses = new List<string>();

        // Culture filter
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var cultureValue = EscapeODataString(culture.IndexCulture());
            baseFilterClauses.Add(
                $"({IndexConstants.FieldNames.Culture} eq '{cultureValue}' or {IndexConstants.FieldNames.Culture} eq '{IndexConstants.Variation.InvariantCulture}')");
        }
        else
        {
            baseFilterClauses.Add($"{IndexConstants.FieldNames.Culture} eq '{IndexConstants.Variation.InvariantCulture}'");
        }

        // Segment filter
        if (!string.IsNullOrWhiteSpace(segment))
        {
            var segmentValue = EscapeODataString(segment.IndexSegment());
            baseFilterClauses.Add(
                $"({IndexConstants.FieldNames.Segment} eq '{segmentValue}' or {IndexConstants.FieldNames.Segment} eq '{IndexConstants.Variation.DefaultSegment}')");
        }
        else
        {
            baseFilterClauses.Add($"{IndexConstants.FieldNames.Segment} eq '{IndexConstants.Variation.DefaultSegment}'");
        }

        var accessClause = BuildAccessClause(accessContext);
        if (accessClause is not null)
        {
            baseFilterClauses.Add(accessClause);
        }

        var filtersArray = filters?.ToArray() ?? [];
        if (filtersArray.Any(f => !f.Negate && FilterFields(f).Any(unknownFields.Contains)))
        {
            return new SearchResult(0, [], []);
        }

        filtersArray = filtersArray.Where(f => !FilterFields(f).Any(unknownFields.Contains)).ToArray();
        var facetsList = (facets ?? []).Where(f => !unknownFields.Contains(BuildFacetExpression(f).FieldKey)).ToList();

        // Split user filters into regular filters and same-field-as-facet filters
        var facetFieldNames = facetsList.Select(f => f.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sameFieldFilters = filtersArray.Where(f => facetFieldNames.Contains(f.FieldName)).ToArray();
        var regularFilters = filtersArray.Except(sameFieldFilters).ToArray();

        // Build full filter clauses (for document query)
        var filterClauses = BuildFilterClauses(baseFilterClauses, filtersArray);

        // Build suggestion filter clauses: Autocomplete rejects search.ismatchscoring, so text filters are left out.
        // Suggestions can then be broader than the results, but never beyond the access clause.
        var suggestionFilterClauses = BuildFilterClauses(baseFilterClauses, filtersArray.Where(f => f is not TextFilter));

        // Build facet filter clauses (excludes same-field filters for proper facet counts)
        var facetFilterClauses = BuildFilterClauses(baseFilterClauses, regularFilters);

        // Sorting - default to score descending when no sorters provided
        var effectiveSorters = (sorters ?? []).Where(sorter => SortField(sorter) is not { } field || !unknownFields.Contains(field)).ToArray();
        if (effectiveSorters.Length == 0)
        {
            effectiveSorters = [new ScoreSorter(Direction.Descending)];
        }

        // Azure allows 32 $orderby clauses; the last one is the tie-breaker below.
        foreach (var sorter in effectiveSorters.Take(MaxOrderByClauses - 1))
        {
            var sortClause = BuildSortClause(sorter);
            if (!string.IsNullOrEmpty(sortClause))
            {
                searchOptions.OrderBy.Add(sortClause);
            }
        }

        // Ties (every score is equal for "*") come back in no fixed order, which breaks skip/take paging.
        searchOptions.OrderBy.Add($"{IndexConstants.FieldNames.Id} asc");

        // Facets - deduplicate by field key and separate into same-field and different-field groups
        // NOTE: Azure Search does not allow multiple facet types on the same field in a single query
        var facetFieldMap = new Dictionary<string, List<Facet>>();
        var addedFacetFields = new HashSet<string>();
        var sameFieldFacetExpressions = new List<string>(); // Facets on same field as a filter
        var diffFieldFacetExpressions = new List<string>(); // Facets on different fields from all filters
        var filterFieldNames = filtersArray.Select(f => f.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var facet in facetsList)
        {
            var (facetExpression, fieldKey) = BuildFacetExpression(facet);
            if (!string.IsNullOrEmpty(facetExpression) && !addedFacetFields.Contains(fieldKey))
            {
                // Determine if this facet is on a field that has a filter
                if (filterFieldNames.Contains(facet.FieldName))
                {
                    sameFieldFacetExpressions.Add(facetExpression);
                }
                else
                {
                    diffFieldFacetExpressions.Add(facetExpression);
                }
                addedFacetFields.Add(fieldKey);
            }
            if (!facetFieldMap.ContainsKey(fieldKey))
            {
                facetFieldMap[fieldKey] = [];
            }
            facetFieldMap[fieldKey].Add(facet);
        }

        // Add different-field facets to main query (they get filtered counts)
        foreach (var expr in diffFieldFacetExpressions)
        {
            searchOptions.Facets.Add(expr);
        }

        if (filterClauses.Any())
        {
            searchOptions.Filter = string.Join(" and ", filterClauses);
        }

        // Same-field facets need a separate query without the same-field filters
        SearchOptions? facetSearchOptions = null;
        if (sameFieldFacetExpressions.Count > 0)
        {
            facetSearchOptions = new SearchOptions
            {
                Size = 0, // Don't need documents, just facets
                IncludeTotalCount = false,
                SearchMode = SearchMode.All
            };

            foreach (var expr in sameFieldFacetExpressions)
            {
                facetSearchOptions.Facets.Add(expr);
            }

            if (facetFilterClauses.Any())
            {
                facetSearchOptions.Filter = string.Join(" and ", facetFilterClauses);
            }
        }

        // None of the requests depends on another's result, so they run together.
        var mainSearchTask = searchClient.SearchAsync<SearchDocument>(searchText, searchOptions);
        var facetSearchTask = facetSearchOptions is null
            ? Task.FromResult<Response<SearchResults<SearchDocument>>?>(null)
            : SearchOrNullAsync(searchClient, searchText, facetSearchOptions);
        var suggestionsTask = GetSuggestionsAsync(
            searchClient, indexAlias, query, maxSuggestions, suggestionFilterClauses, azureSearchIndexOptions.Get(indexAlias).Suggestions);
        await Task.WhenAll(mainSearchTask, facetSearchTask, suggestionsTask);

        var result = await mainSearchTask;

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

        // Merge facet results from main query
        var facetResultsData = new Dictionary<string, IList<AzureFacetResult>>();
        if (result.Value.Facets != null)
        {
            foreach (var kvp in result.Value.Facets)
            {
                facetResultsData[kvp.Key] = kvp.Value;
            }
        }

        // Merge same-field facet results (computed without same-field filters)
        var facetResult = await facetSearchTask;
        if (facetResult?.Value.Facets != null)
        {
            foreach (var kvp in facetResult.Value.Facets)
            {
                facetResultsData[kvp.Key] = kvp.Value;
            }
        }

        // Parse facet results
        var facetResults = ParseFacetResults(facetResultsData, facetFieldMap);

        var suggestions = await suggestionsTask;
        return new SearchResult(result.Value.TotalCount ?? 0, documents.ToArray(), facetResults.ToArray(), suggestions);
    }

    private static async Task<Response<SearchResults<SearchDocument>>?> SearchOrNullAsync(
        SearchClient searchClient, string searchText, SearchOptions options)
        => await searchClient.SearchAsync<SearchDocument>(searchText, options);

    private async Task<IEnumerable<string>?> GetSuggestionsAsync(
        SearchClient searchClient,
        string indexAlias,
        string? query,
        int maxSuggestions,
        List<string> filterClauses,
        SuggestionOptions suggestionOptions)
    {
        // The raw query, not the "*"-suffixed search text: Autocomplete completes the last term itself.
        var suggestionQuery = WhitespacePattern.Replace(query ?? string.Empty, " ").Trim();
        if (maxSuggestions <= 0 || suggestionQuery.Length == 0 || !suggestionOptions.Enabled)
            return null;

        var options = new AutocompleteOptions
        {
            Mode = AutocompleteMode.OneTermWithContext,
            Size = Math.Min(maxSuggestions, MaxAutocompleteSize),
            Filter = string.Join(" and ", filterClauses)
        };
        Response<AutocompleteResults> response;
        try
        {
            response = await searchClient.AutocompleteAsync(suggestionQuery, IndexConstants.SuggesterName, options);
        }
        catch (RequestFailedException ex) when (IsMissingSuggester(ex))
        {
            // An index created before suggestions existed; EnsureAsync or a rebuild adds the suggester.
            if (MissingSuggesterWarnings.TryAdd(indexAlias, true))
            {
                logger.LogWarning(
                    "Index {IndexAlias} has no suggester (status {StatusCode}), so it returns no suggestions. Rebuild the index to add it.",
                    indexAlias, ex.Status);
            }
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 400 && UnknownFieldPattern.IsMatch(ex.Message))
        {
            // The search fails on the same field and is repeated without it, suggestions included.
            return null;
        }
        catch (RequestFailedException ex)
        {
            // Suggestions are optional: a failed Autocomplete request must not fail the search.
            logger.LogWarning(ex, "Suggestions for index {IndexAlias} failed with status {StatusCode}", indexAlias, ex.Status);
            return null;
        }

        var suggestions = response.Value.Results
            .Select(r => r.QueryPlusText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSuggestions)
            .ToArray();
        return suggestions.Length > 0 ? suggestions : null;
    }

    private static bool IsMissingSuggester(RequestFailedException ex)
        => ex.Status == 400 && ex.Message.Contains("suggester name", StringComparison.OrdinalIgnoreCase);

    // Mirrors the Examine provider: without a member only unprotected documents (Guid.Empty) match.
    private static string? BuildAccessClause(AccessContext? accessContext)
    {
        if (accessContext?.Bypass is true)
            return null;

        var accessKeys = new List<Guid> { Guid.Empty };
        if (accessContext is not null && accessContext.PrincipalId != Guid.Empty)
        {
            accessKeys.Add(accessContext.PrincipalId);
            accessKeys.AddRange(accessContext.GroupIds ?? []);
        }

        var values = string.Join(",", accessKeys.Distinct().Select(key => key.AsKeyword()));
        return $"{IndexConstants.FieldNames.AccessKeys}/any(k: search.in(k, '{values}', ','))";
    }

    // The index fields a filter reads, as the index names them.
    private static IEnumerable<string> FilterFields(Filter filter) => filter switch
    {
        KeywordAnyFilter or KeywordFilter => [$"{filter.FieldName}{IndexConstants.FieldTypePostfix.Keywords}"],
        IntegerExactFilter or IntegerRangeFilter => [$"{filter.FieldName}{IndexConstants.FieldTypePostfix.Integers}"],
        DecimalExactFilter or DecimalRangeFilter => [$"{filter.FieldName}{IndexConstants.FieldTypePostfix.Decimals}"],
        DateTimeOffsetExactFilter or DateTimeOffsetRangeFilter => [$"{filter.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}"],
        TextFilter =>
        [
            $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Texts}",
            $"{filter.FieldName}{IndexConstants.FieldTypePostfix.TextsR1}",
            $"{filter.FieldName}{IndexConstants.FieldTypePostfix.TextsR2}",
            $"{filter.FieldName}{IndexConstants.FieldTypePostfix.TextsR3}",
        ],
        _ => [],
    };

    private static string? SortField(Sorter sorter) => sorter switch
    {
        IntegerSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Integers}{IndexConstants.FieldTypePostfix.Sortable}",
        DecimalSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Decimals}{IndexConstants.FieldTypePostfix.Sortable}",
        DateTimeOffsetSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}{IndexConstants.FieldTypePostfix.Sortable}",
        KeywordSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Keywords}{IndexConstants.FieldTypePostfix.Sortable}",
        TextSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Texts}{IndexConstants.FieldTypePostfix.Sortable}",
        _ => null,
    };
    private static void ValidateFieldName(string fieldName)
    {
        if (!FieldNamePattern.IsMatch(fieldName))
            throw new ArgumentException($"'{fieldName}' is not a valid field name: letters, digits and underscores only.", nameof(fieldName));
    }

    private static List<string> BuildFilterClauses(IEnumerable<string> baseFilterClauses, IEnumerable<Filter> filters)
        => baseFilterClauses
            .Concat(filters.Select(BuildFilterClause).Where(clause => !string.IsNullOrEmpty(clause)))
            .ToList();

    private static string BuildFilterClause(Filter filter)
    {
        ValidateFieldName(filter.FieldName);
        return filter switch
        {
            KeywordAnyFilter keywordAnyFilter => BuildKeywordAnyFilter(keywordAnyFilter),
            KeywordFilter keywordFilter => BuildKeywordFilter(keywordFilter),
            IntegerExactFilter integerExactFilter => BuildIntegerExactFilter(integerExactFilter),
            IntegerRangeFilter integerRangeFilter => BuildIntegerRangeFilter(integerRangeFilter),
            DecimalExactFilter decimalExactFilter => BuildDecimalExactFilter(decimalExactFilter),
            DecimalRangeFilter decimalRangeFilter => BuildDecimalRangeFilter(decimalRangeFilter),
            DateTimeOffsetExactFilter dateTimeOffsetExactFilter => BuildDateTimeOffsetExactFilter(dateTimeOffsetExactFilter),
            DateTimeOffsetRangeFilter dateTimeOffsetRangeFilter => BuildDateTimeOffsetRangeFilter(dateTimeOffsetRangeFilter),
            TextFilter textFilter => BuildTextFilterWithRelevance(textFilter),
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
        return BuildNumericExactFilter(fieldName, filter.Values.Select(DecimalLiteral), filter.Negate);
    }

    // Decimals are stored as Edm.Double: written without a decimal point, a value beyond Int64 is an integer literal
    // Azure rejects.
    private static string DecimalLiteral(decimal value)
    {
        var literal = value.ToString(CultureInfo.InvariantCulture);
        return literal.Contains('.') ? literal : $"{literal}.0";
    }

    private static string BuildNumericExactFilter(string fieldName, IEnumerable<string> rawValues, bool negate)
    {
        var valueList = rawValues.ToArray();
        if (valueList.Length == 0)
            return string.Empty;

        var clause = string.Join(" or ", valueList.Select(v => $"{fieldName}/any(f: f eq {v})"));
        return negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildKeywordAnyFilter(KeywordAnyFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Keywords}";
        var clause = $"{fieldName}/any()";
        return filter.Negate ? $"not ({clause})" : clause;
    }

    private static string BuildKeywordFilter(KeywordFilter filter)
    {
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Keywords}";
        var valueList = filter.Values.ToArray();
        if (valueList.Length == 0)
            return string.Empty;

        var condition = string.Join(" or ", valueList.Select(v => $"k eq '{EscapeODataString(v)}'"));
        var clause = $"{fieldName}/any(k: {condition})";
        return filter.Negate ? $"not ({clause})" : clause;
    }

    private static string EscapeODataString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildIntegerRangeFilter(IntegerRangeFilter filter)
    {
        var rangeList = filter.Ranges.ToArray();
        if (rangeList.Length == 0)
            return string.Empty;

        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Integers}";
        var clause = string.Join(" or ", rangeList.Select(r => BuildRangeClause(fieldName,
            r.MinValue?.ToString(CultureInfo.InvariantCulture),
            r.MaxValue?.ToString(CultureInfo.InvariantCulture))));
        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    // Min is inclusive and max exclusive, as in the Examine provider; a null bound leaves that side open.
    private static string BuildRangeClause(string fieldName, string? minValue, string? maxValue)
    {
        var bounds = new List<string>(2);
        if (minValue is not null)
            bounds.Add($"f ge {minValue}");
        if (maxValue is not null)
            bounds.Add($"f lt {maxValue}");

        return bounds.Count == 0
            ? $"{fieldName}/any()"
            : $"{fieldName}/any(f: {string.Join(" and ", bounds)})";
    }

    private static string BuildDecimalRangeFilter(DecimalRangeFilter filter)
    {
        var rangeList = filter.Ranges.ToArray();
        if (rangeList.Length == 0)
            return string.Empty;

        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Decimals}";
        var clause = string.Join(" or ", rangeList.Select(r => BuildRangeClause(fieldName,
            r.MinValue is { } min ? DecimalLiteral(min) : null,
            r.MaxValue is { } max ? DecimalLiteral(max) : null)));
        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildDateTimeOffsetExactFilter(DateTimeOffsetExactFilter filter)
    {
        var valueList = filter.Values.ToArray();
        if (valueList.Length == 0)
            return string.Empty;

        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}";
        var clause = string.Join(" or ", valueList.Select(v =>
            $"{fieldName}/any(f: f eq {v.ToString("O", CultureInfo.InvariantCulture)})"));
        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildDateTimeOffsetRangeFilter(DateTimeOffsetRangeFilter filter)
    {
        var rangeList = filter.Ranges.ToArray();
        if (rangeList.Length == 0)
            return string.Empty;

        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}";
        var clause = string.Join(" or ", rangeList.Select(r => BuildRangeClause(fieldName,
            r.MinValue?.ToString("O", CultureInfo.InvariantCulture),
            r.MaxValue?.ToString("O", CultureInfo.InvariantCulture))));

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildTextFilter(TextFilter filter)
    {
        // Search the base _texts field using search.ismatchscoring() for scoring contribution
        // Note: This searches only the base _texts field. For relevance-based sorting that
        // differentiates between R1/R2/R3 text fields, see BuildTextFilterWithRelevance().
        var fieldName = $"{filter.FieldName}{IndexConstants.FieldTypePostfix.Texts}";
        var values = filter.Values.Select(v =>
            $"search.ismatchscoring('{EscapeODataString(v)}*', '{fieldName}')");
        var clause = string.Join(" or ", values);

        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string BuildTextFilterWithRelevance(TextFilter filter)
    {
        // Search all four text relevance fields with boosting to enable relevance-based sorting
        // Uses search.ismatchscoring() with Lucene query syntax and field-specific boosts
        // This matches Elasticsearch behavior where TextFilter searches all relevance fields with boosts
        // Boost factors: R1=4.0, R2=3.0, R3=2.0, Base=1.0 (matching the scoring profile ratios)
        // NOTE: This requires all four text fields to exist in the index schema
        var baseFieldName = filter.FieldName;
        var fieldR1 = $"{baseFieldName}{IndexConstants.FieldTypePostfix.TextsR1}";
        var fieldR2 = $"{baseFieldName}{IndexConstants.FieldTypePostfix.TextsR2}";
        var fieldR3 = $"{baseFieldName}{IndexConstants.FieldTypePostfix.TextsR3}";
        var fieldBase = $"{baseFieldName}{IndexConstants.FieldTypePostfix.Texts}";

        var allClauses = new List<string>();
        foreach (var value in filter.Values)
        {
            var escapedValue = EscapeLuceneQuery(value);
            // Use Lucene query syntax with field-specific boosts
            // Higher boost on R1 fields means documents matching there rank higher
            var luceneQuery = $"{fieldR1}:{escapedValue}*^4 OR {fieldR2}:{escapedValue}*^3 OR {fieldR3}:{escapedValue}*^2 OR {fieldBase}:{escapedValue}*";

            // Use search.ismatchscoring with full Lucene query mode
            // Parameters: query, fields (empty = use query's field specs), queryType, searchMode
            allClauses.Add($"search.ismatchscoring('{EscapeODataString(luceneQuery)}', '', 'full', 'any')");
        }

        var clause = string.Join(" or ", allClauses);
        return filter.Negate ? $"not ({clause})" : $"({clause})";
    }

    private static string EscapeLuceneQuery(string value)
    {
        // Escape special Lucene query characters, but preserve the query structure
        // Characters that need escaping: + - && || ! ( ) { } [ ] ^ " ~ * ? : \ /
        var specialChars = new[] { '+', '-', '&', '|', '!', '(', ')', '{', '}', '[', ']', '^', '"', '~', '?', ':', '\\', '/' };
        var result = value;
        foreach (var c in specialChars)
        {
            result = result.Replace(c.ToString(), "\\" + c);
        }
        return result;
    }

    private static string BuildSortClause(Sorter sorter)
    {
        var direction = sorter.Direction == Direction.Ascending ? "asc" : "desc";
        if (sorter is not ScoreSorter)
            ValidateFieldName(sorter.FieldName);

        return sorter switch
        {
            IntegerSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Integers}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            DecimalSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Decimals}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            DateTimeOffsetSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.DateTimeOffsets}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            KeywordSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Keywords}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            TextSorter s => $"{s.FieldName}{IndexConstants.FieldTypePostfix.Texts}{IndexConstants.FieldTypePostfix.Sortable} {direction}",
            ScoreSorter => $"search.score() {direction}",
            _ => string.Empty
        };
    }

    private static (string Expression, string FieldKey) BuildFacetExpression(Facet facet)
    {
        ValidateFieldName(facet.FieldName);
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
            long count = 0;

            foreach (var v in azureFacetValues)
            {
                // Try to get range bounds from the Azure facet result
                long? fromValue = null;

                // Try as range facet with different numeric types
                try
                {
                    var rangeFacet = v.AsRangeFacetResult<long>();
                    fromValue = rangeFacet.From;
                }
                catch
                {
                    try
                    {
                        var rangeFacet = v.AsRangeFacetResult<double>();
                        fromValue = rangeFacet.From.HasValue ? (long)rangeFacet.From.Value : null;
                    }
                    catch
                    {
                        try
                        {
                            var rangeFacet = v.AsRangeFacetResult<int>();
                            fromValue = rangeFacet.From;
                        }
                        catch
                        {
                            // Try Value property as fallback for non-range facets
                            if (v.Value != null)
                            {
                                try { fromValue = Convert.ToInt64(v.Value); }
                                catch { /* ignore conversion errors */ }
                            }
                        }
                    }
                }

                // Match by From bound (MinValue)
                if (range.MinValue.HasValue && fromValue.HasValue && fromValue.Value == range.MinValue.Value)
                {
                    count = v.Count ?? 0;
                    break;
                }
                // Handle unbounded lower range (MinValue is null)
                if (!range.MinValue.HasValue && !fromValue.HasValue)
                {
                    count = v.Count ?? 0;
                    break;
                }
            }

            facetValues.Add(new IntegerRangeFacetValue(range.Key, range.MinValue, range.MaxValue, count));
        }

        return new UmbracoFacetResult(facet.FieldName, facetValues.ToArray());
    }

    private static UmbracoFacetResult MapDecimalRangeFacetResult(DecimalRangeFacet facet, IList<AzureFacetResult> azureFacetValues)
    {
        var facetValues = new List<DecimalRangeFacetValue>();

        foreach (var range in facet.Ranges)
        {
            long count = 0;

            foreach (var v in azureFacetValues)
            {
                double? fromValue = null;

                try
                {
                    var rangeFacet = v.AsRangeFacetResult<double>();
                    fromValue = rangeFacet.From;
                }
                catch
                {
                    try
                    {
                        var rangeFacet = v.AsRangeFacetResult<decimal>();
                        fromValue = rangeFacet.From.HasValue ? (double)rangeFacet.From.Value : null;
                    }
                    catch
                    {
                        if (v.Value != null)
                        {
                            try { fromValue = Convert.ToDouble(v.Value); }
                            catch { /* ignore */ }
                        }
                    }
                }

                // Match by From bound with tolerance for floating point comparison
                if (range.MinValue.HasValue && fromValue.HasValue &&
                    Math.Abs(fromValue.Value - (double)range.MinValue.Value) < 0.0001)
                {
                    count = v.Count ?? 0;
                    break;
                }
                if (!range.MinValue.HasValue && !fromValue.HasValue)
                {
                    count = v.Count ?? 0;
                    break;
                }
            }

            facetValues.Add(new DecimalRangeFacetValue(range.Key, range.MinValue, range.MaxValue, count));
        }

        return new UmbracoFacetResult(facet.FieldName, facetValues.ToArray());
    }

    private static UmbracoFacetResult MapDateTimeOffsetRangeFacetResult(DateTimeOffsetRangeFacet facet, IList<AzureFacetResult> azureFacetValues)
    {
        var facetValues = new List<DateTimeOffsetRangeFacetValue>();

        foreach (var range in facet.Ranges)
        {
            long count = 0;

            foreach (var v in azureFacetValues)
            {
                DateTimeOffset? fromValue = null;

                try
                {
                    var rangeFacet = v.AsRangeFacetResult<DateTimeOffset>();
                    fromValue = rangeFacet.From;
                }
                catch
                {
                    if (v.Value is DateTimeOffset dto)
                    {
                        fromValue = dto;
                    }
                }

                // Compare by UTC ticks to handle timezone differences
                if (range.MinValue.HasValue && fromValue.HasValue &&
                    fromValue.Value.UtcTicks == range.MinValue.Value.UtcTicks)
                {
                    count = v.Count ?? 0;
                    break;
                }
                if (!range.MinValue.HasValue && !fromValue.HasValue)
                {
                    count = v.Count ?? 0;
                    break;
                }
            }

            facetValues.Add(new DateTimeOffsetRangeFacetValue(range.Key, range.MinValue, range.MaxValue, count));
        }

        return new UmbracoFacetResult(facet.FieldName, facetValues.ToArray());
    }
}
