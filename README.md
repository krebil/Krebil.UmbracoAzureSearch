# Krebil.UmbracoAzureSearch

An [Azure AI Search](https://azure.microsoft.com/en-us/products/ai-services/ai-search) provider for [Umbraco Search](https://github.com/umbraco/Umbraco.Cms.Search).

[![NuGet](https://img.shields.io/nuget/v/Krebil.UmbracoAzureSearch)](https://www.nuget.org/packages/Krebil.UmbracoAzureSearch)

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

For a serverless Azure AI Search service, also set `Serverless`:

```json
{
  "UmbracoAzureSearch": {
    "Serverless": true
  }
}
```

The package also reads the older key `IsServerless`. When both keys are set, `IsServerless` wins.

### Microsoft Entra ID (managed identity)

To authenticate without an API key, pass a `TokenCredential` when registering and leave `Key` out of the configuration:

```csharp
builder.Services.AddUmbracoAzureSearch(builder.Config, new DefaultAzureCredential());
```

`DefaultAzureCredential` comes from the `Azure.Identity` package. The search service's API access control must allow role-based access, and the identity needs the **Search Service Contributor** role (to create indexes) and the **Search Index Data Contributor** role (to index and query). A credential passed in code takes precedence over a configured `Key`.

## Registration

Register the services via a composer:

```csharp
public sealed class SearchComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddSearchCore();
        builder.Services.AddUmbracoAzureSearch(builder.Config);
    }
}
```

`AddUmbracoAzureSearch` registers the four default Umbraco Search indexes (`Umb_Content`, `Umb_PublishedContent`, `Umb_Media` and `Umb_Members`) against Azure AI Search. To change one of them or add your own, configure `IndexOptions` after this call; the last registration for an alias wins. To change the Azure settings of an index, see [Index configuration](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/index-configuration.md).

## Usage

Inject `IAzureSearchSearcher` (or `ISearcherResolver` to resolve it by index alias) and call `SearchAsync`. See the [Umbraco Search documentation](https://github.com/umbraco/Umbraco.Cms.Search/blob/main/docs/searching.md) for the full API: filtering, faceting, sorting, pagination, culture/segment variants, and protected content all work the same way.

```csharp
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

## Features

- [Index configuration](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/index-configuration.md): settings per index, from appsettings or code.
- [Index definition](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/index-definition.md): choose the content, fields and field attributes of an index.
- [Analyzers](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/analyzers.md): language analyzers per culture, in one index or one index per culture.
- [Customizing Azure](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/customizing-azure.md): change the Azure index definition and queries in code.
- [Zero-downtime rebuild](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/zero-downtime-rebuild.md): rebuild into a second index and swap, so searches keep working.
- [Spell check](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/spell-check.md): correct mistyped query terms per culture.
- [Suggestions](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/suggestions.md): complete the last word of a query, or suggest documents, for a search box.
- [Azure AI Search limitations](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/limitations.md): Azure limits that change how some Umbraco Search features behave.

## Upgrading

Read the [upgrade guide](https://github.com/krebil/Krebil.UmbracoAzureSearch/blob/main/docs/upgrading.md) before you update the package.
