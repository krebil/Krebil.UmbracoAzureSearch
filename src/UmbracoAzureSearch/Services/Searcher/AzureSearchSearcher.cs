using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Factory;

namespace UmbracoAzureSearch.Services.Searcher;

public class AzureSearchSearcher(IServerRoleAccessor serverRoleAccessor, IAzureSearchClientFactory azureSearchClientFactory)
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
            IncludeTotalCount = true
        };

        // Build the search query - use wildcard for match all
        var searchText = string.IsNullOrWhiteSpace(query) ? "*" : query;

        // Build OData filter
        var filterClauses = new List<string>();

        // Culture filter
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var cultureValue = culture.IndexCulture();
            filterClauses.Add($"({IndexConstants.FieldNames.Culture} eq '{cultureValue}' or {IndexConstants.FieldNames.Culture} eq '{IndexConstants.Variation.InvariantCulture}')");
        }
        else
        {
            filterClauses.Add($"{IndexConstants.FieldNames.Culture} eq '{IndexConstants.Variation.InvariantCulture}'");
        }

        // Segment filter
        if (!string.IsNullOrWhiteSpace(segment))
        {
            var segmentValue = segment.IndexSegment();
            filterClauses.Add($"({IndexConstants.FieldNames.Segment} eq '{segmentValue}' or {IndexConstants.FieldNames.Segment} eq '{IndexConstants.Variation.DefaultSegment}')");
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

        // TODO: Add support for access context, facets, and sorting

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

        return new SearchResult(result.Value.TotalCount ?? 0, documents.ToArray(), []);
    }

    private static string BuildFilterClause(Filter filter)
    {
        return filter switch
        {
            KeywordFilter keywordFilter => BuildKeywordFilter(keywordFilter),
            // TODO: Add other filter types as needed
            _ => string.Empty
        };
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
}