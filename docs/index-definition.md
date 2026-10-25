# Index definition

## When to use this

Use the index definition to choose what goes into an index: which content, which fields, and how Azure treats each field. Without a definition, an index holds every content item and every field that the Umbraco Search indexers produce, as before.

Defaults:

| Setting | Default |
|---|---|
| `AutomaticFields` | `true`: every field is written |
| `Fields` | None opted in |
| Content types | Every content type |
| Cultures | Every culture |
| Per-index indexers | None |

Needs a rebuild: yes for most changes to an existing index. See [Gotchas](#gotchas) and the [upgrade guide](upgrading.md#what-needs-a-rebuild).

The definition is part of the [index configuration](index-configuration.md). appsettings applies first, then code. When both set the same value, code wins.

## Parts of the definition

| Part | appsettings | Code |
|---|---|---|
| Automatic fields on or off | `AutomaticFields` | `.AutomaticFields(bool)` |
| Opted-in fields and their attributes | `Fields:<field>` | `.Field("<field>", f => ...)` |
| Content types | Code only | `.ContentTypes(params string[])` |
| Cultures | Code only | `.Cultures(params string[])` |
| Per-index indexers | Code only | `.Indexer<TIndexer>()` |

Content scope and per-index indexers are code only, because they need types and logic.

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "Umb_PublishedContent": {
        "AutomaticFields": true,
        "Fields": {
          "author":   { "Filterable": true },
          "category": { "Filterable": true, "Facetable": true }
        }
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .ContentTypes("articlePage", "playerPage", "matchSheet", "coldPage")
    .Cultures("fr", "en", "es", "pt", "ar")
    .AutomaticFields(false)
    .Indexer<SiteSearchTextIndexer>()
    .Field("title", f => f.Searchable().Suggestable().Boost(3))
    .Field("names", f => f.Searchable().Suggestable().Boost(2))
    .Field("summary", f => f.Searchable())
    .Field("body", f => f.Searchable())
    .Field("date", f => f.Filterable().Sortable()));
```

When appsettings and code set the same attribute of a field, code wins. Attributes that code does not set keep their appsettings value.

## Automatic fields

With `AutomaticFields` on (the default), the package writes every field the Umbraco Search indexers produce. Each field gets attributes by its value type, as before:

- Texts are searchable and filterable, and feed the free-text query on all fields.
- Keywords, integers, decimals and dates are filterable and facetable.
- Every field is sortable.

An opt-in changes the attributes of an automatic field. An attribute that the opt-in does not set keeps its automatic value. For example, `"summary": { "Searchable": false }` takes `summary` out of the free-text query, and `summary` stays sortable.

With `AutomaticFields` off, a field is in the index only when it is opted in. An attribute that the opt-in does not set is off. This applies to every indexer, including the Delivery API `IContentIndexHandler` fields.

The Umbraco system fields (`Umb_Id`, `Umb_ParentId`, `Umb_PathIds`, `Umb_ContentTypeId`, `Umb_Name`, `Umb_CreateDate`, `Umb_UpdateDate`, `Umb_Level`, `Umb_SortOrder`, `Umb_ObjectType`, `Umb_Tags`) are always written. Deletes, the backoffice and the Delivery API use them.

## Field attributes

| Attribute | appsettings | Builder method | Azure meaning |
|---|---|---|---|
| Searchable | `Searchable` | `.Searchable()` | The texts go into four searchable text fields (`<field>_texts`, `_texts_r1` to `_texts_r3`) and into the free-text query on all fields. A text filter needs it. |
| Filterable | `Filterable` | `.Filterable()` | The values can be used in filters (`$filter`). |
| Facetable | `Facetable` | `.Facetable()` | Keywords, numbers and dates can be used in facets. |
| Sortable | `Sortable` | `.Sortable()` | The package writes a single-value `<field>_<type>_sort` field to sort on. |
| Suggestable | `Suggestable` | `.Suggestable()` | The texts feed [suggestions](suggestions.md). Needs Searchable. |
| Analyzer | `Analyzer` | `.Analyzer("en.microsoft")` | The Azure analyzer of the searchable text fields. Unset uses `standard.lucene`. A custom analyzer must be added with [ConfigureIndex](customizing-azure.md). |
| PerCulture | `PerCulture` | `.PerCulture()` | The searchable text fields get a copy per mapped culture with the culture analyzer. See [Analyzers](analyzers.md#perculture-fields). |
| Boost | `Boost` | `.Boost(3)` | The relevance boost of the field. The package stores it. A later version turns it into a scoring profile. |

Each builder method takes an optional `bool`, so `.Searchable(false)` turns an attribute off.

Suggestable fields add to the `Suggestions:Fields` setting. When neither sets a field, suggestions come from the searchable highest-relevance texts, as before.

## Content scope

`.ContentTypes(...)` takes document, media or member type aliases. A document of another content type is not written to the index.

`.Cultures(...)` takes culture keys. A key matches a culture exactly or by its language part, case-insensitive. `pt` holds `pt-BR` and `pt-PT`. A culture outside the list is not written. Invariant documents are always written.

## Per-index indexers

`.Indexer<TIndexer>()` runs an `IContentIndexer` for this index only. Its fields go through the same definition as other fields. With automatic fields off, opt in each field it produces.

Do not register the indexer as `IContentIndexer` in the container. Umbraco Search runs every registered `IContentIndexer` for every index. The package resolves the indexer type from the container when it is registered as itself, or creates it with its dependencies.

The package loads the content item for each document it indexes for the index. The indexer gets the cultures of the document, and `published: true` for an index with the published content change strategy.

## Custom index from start to end

Register the index with Umbraco Search and configure it in one composer:

```csharp
public sealed class SiteSearchComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.Configure<IndexOptions>(options =>
            options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IPublishedContentChangeStrategy>(
                "siteSearch", UmbracoObjectTypes.Document));

        builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
            .ContentTypes("articlePage")
            .AutomaticFields(false)
            .Indexer<SiteSearchTextIndexer>()
            .Field("title", f => f.Searchable().Suggestable())
            .Field("date", f => f.Filterable().Sortable()));
    }
}
```

## Indexing paths

Every indexing path ends in the package's indexer, and the indexer applies the definition. The definition applies to:

- The Umbraco Search queue: publish, save and delete.
- A rebuild from the backoffice.
- Direct calls to `IIndexer` or `IAzureSearchIndexer` from your code, for example in a background job.

The indexer also adds new fields to the Azure schema as documents bring them. The schema follows the same attributes.

## Field budget

Azure limits the number of fields in one index: 1000 on every tier, and 100 on Basic services created before April 3, 2024. Set `UmbracoAzureSearch:MaxFieldsPerIndex` to `100` on an older Basic service.

The package counts the fields at startup and stops startup when the count is over the limit. The count is:

| Part | Fields |
|---|---|
| Base fields (`id`, `key`, `objectType`, `culture`, `segment`, `accessKeys`, four `allTexts` fields) | 10 |
| Umbraco system fields | 25 |
| Suggestion field, when suggestions are on | 1, plus 1 per mapped culture and 1 invariant with [suggestion copies](analyzers.md#suggestionanalyzer) |
| [Per-culture copies](analyzers.md#fieldperculture) | 4 per mapped culture and 4 invariant, plus 4 more of each per `.PerCulture()` field |
| Each searchable field (automatic fields off) | 4 |
| Each filterable or facetable field (automatic fields off) | 1 |
| Each sortable field (automatic fields off) | 1 |

The count is an upper limit for a field with one value type. A searchable and filterable text field counts 5 and uses 4.

With automatic fields on, the other fields depend on the content, so the package counts only the fields in the table that do not depend on it. Azure rejects a new field over the limit when a document brings it.

## Validation

| Check | Example error |
|---|---|
| A suggestable field is searchable | `The field "title" is suggestable but not searchable. Make it searchable, or remove Suggestable.` |
| The analyzer exists in Azure | `The field "title" uses the analyzer "en.microsft", which Azure AI Search does not have. Use a built-in analyzer name such as "standard.lucene" or "en.microsoft", or add the analyzer with ConfigureIndex.` |
| The fields fit the limit (automatic fields off) | `The index needs 43 Azure fields, but the service allows 40 (UmbracoAzureSearch:MaxFieldsPerIndex). Opt in fewer fields or fewer attributes.` |

## Gotchas

- Azure cannot remove a field or change its attributes. Turning automatic fields off on an existing index, or changing filterable, facetable, sortable, searchable or the analyzer of an existing field, needs a rebuild.
- A rebuild reuses the cached `umbracoIndexDocument` rows that Umbraco Search stores for each content item. Fields from per-index indexers are not cached, so a rebuild runs them again. Set [`ClearDocumentCacheOnRebuild`](zero-downtime-rebuild.md#cleardocumentcacheonrebuild) to refresh the cached rows on a rebuild.
- With automatic fields off, opt in every field the Delivery API filters or sorts on. Otherwise its queries fail or return nothing.
- Content scope and per-index indexers are code only.
- A document that leaves the scope after a configuration change stays in the index until the next rebuild.
