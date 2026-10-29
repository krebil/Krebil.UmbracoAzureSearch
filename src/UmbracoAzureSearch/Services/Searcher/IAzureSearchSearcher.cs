using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Services.Searcher;

public interface IAzureSearchSearcher : ISearcher
{
    Task<SearchResult> SearchAsync(
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
        SearchMode searchMode = SearchMode.All,
        string? scoringProfile = null,
        IEnumerable<string>? scoringParameters = null);

    /// <summary>
    /// Suggests documents whose suggestion texts match a partial query, with the Azure Suggest API. Filters, culture,
    /// segment and access apply as in <see cref="SearchAsync"/>, except text filters. Azure errors are thrown.
    /// </summary>
    /// <param name="indexAlias">The Umbraco index alias.</param>
    /// <param name="query">The partial query. Longer than 100 characters is cut.</param>
    /// <param name="filters">Filters as for a search. Text filters are left out.</param>
    /// <param name="culture">The culture. Its documents and invariant documents match.</param>
    /// <param name="segment">The segment.</param>
    /// <param name="accessContext">The access context for protected content.</param>
    /// <param name="top">The most documents to return, 1 to 100. 5 by default.</param>
    /// <param name="fuzzy">Also match terms within one edit of the query. Slower; off by default.</param>
    /// <param name="select">Umbraco field names to return in <see cref="SuggestedDocument.Fields"/>.</param>
    Task<SuggestResult> SuggestAsync(
        string indexAlias,
        string query,
        IEnumerable<Filter>? filters = null,
        string? culture = null,
        string? segment = null,
        AccessContext? accessContext = null,
        int top = 5,
        bool fuzzy = false,
        IEnumerable<string>? select = null);
}
