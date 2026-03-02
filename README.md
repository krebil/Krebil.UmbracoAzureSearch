# Krebil.UmbracoAzureSearch

An [Azure AI Search](https://azure.microsoft.com/en-us/products/ai-services/ai-search) provider for [Umbraco Search](https://github.com/umbraco/Umbraco.Cms.Search).

> **Note:** Umbraco Search is a work in progress. Things might change at a moment's notice.

## Prerequisites

- Umbraco v17+
- An Azure AI Search instance ([create one in the Azure portal](https://portal.azure.com))

## Installation

Install the core Umbraco Search package and this provider:

```
dotnet add package Umbraco.Cms.Search.Core
dotnet add package Krebil.UmbracoAzureSearch
```

## Configuration

Add your Azure AI Search credentials to `appsettings.json`:

```json
{
  "UmbracoAzureSearch": {
    "Endpoint": "https://your-search-service.search.windows.net",
    "Key": "your-api-key"
  }
}
```

## Registration

Register the services via a composer:

```csharp
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Search.Core.DependencyInjection;
using UmbracoAzureSearch.Extensions;

namespace YourSite.DependencyInjection;

public sealed class SearchComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddSearchCore();
        builder.Services.AddUmbracoAzureSearch(builder.Config);
    }
}
```

## Usage

Inject `IAzureSearchSearcher` (or `ISearcherResolver` to resolve it by index alias) and call `SearchAsync`. See the [Umbraco Search documentation](https://github.com/umbraco/Umbraco.Cms.Search/blob/main/docs/searching.md) for the full API — filtering, faceting, sorting, pagination, culture/segment variants, and protected content all work the same way.

```csharp
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using UmbracoAzureSearch.Services.Searcher;
using SearchConstants = Umbraco.Cms.Search.Core.Constants;

namespace YourSite.Services;

public class MySearchService(IAzureSearchSearcher searcher)
{
    public async Task<SearchResult> SearchAsync(string query)
        => await searcher.SearchAsync(
            indexAlias: SearchConstants.IndexAliases.PublishedContent,
            query: query,
            filters:
            [
                new KeywordFilter("genre", ["rock", "pop"], Negate: false)
            ],
            facets:
            [
                new KeywordFacet("genre"),
                new IntegerRangeFacet("releaseYear",
                [
                    new("50s", 1950, 1960),
                    new("80s", 1980, 1990)
                ])
            ],
            sorters:
            [
                new IntegerSorter("releaseYear", Direction.Descending)
            ],
            skip: 0,
            take: 10
        );
}
```

## Azure AI Search limitations

- **No guaranteed ordering without an explicit sort** — always provide a sorter when result order matters.
- **No multiple facet types on the same field** — e.g. `IntegerExactFacet` and `IntegerRangeFacet` cannot both be applied to the same field in a single query. The provider works around this internally by running a separate query per field, so mixed facet types across different fields work fine.