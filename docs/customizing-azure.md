# Customizing Azure

> **The package does not validate changes made in the callbacks.** A wrong field name, analyzer or filter in `ConfigureIndex` or `ConfigureSearch` fails at runtime, when the index is created or a query runs, not at startup.

## When to use this

Use the callbacks for Azure AI Search features that the package does not model: custom analyzers, your own scoring profiles, synonym maps, extra fields, or query options such as a scoring profile per culture.

Defaults: no callbacks.

Needs a rebuild: depends on the change. Azure applies new fields and scoring profiles to a live index. A new analyzer, or a change to an existing field, needs a rebuild. See [When the package applies ConfigureIndex](#when-the-package-applies-configureindex).

The callbacks are code only. They take Azure SDK types (`Azure.Search.Documents`) and logic, which appsettings cannot express.

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .ConfigureIndex(searchIndex =>
    {
        searchIndex.Analyzers.Add(new CustomAnalyzer("names_folded", LexicalTokenizerName.Standard)
        {
            TokenFilters = { TokenFilterName.Lowercase, TokenFilterName.AsciiFolding }
        });

        searchIndex.ScoringProfiles.Add(new ScoringProfile("titleFirst")
        {
            TextWeights = new TextWeights(new Dictionary<string, double> { ["allTextsR1_fr"] = 5 })
        });
    })
    .ConfigureSearch((options, request) =>
    {
        if (request.Culture?.StartsWith("fr", StringComparison.OrdinalIgnoreCase) is true)
        {
            options.ScoringProfile = "titleFirst";
        }
    }));
```

`allTextsR1_fr` in the example is a per-culture copy from the `FieldPerCulture` analyzer mode. See [Physical names](analyzers.md#physical-names).

## ConfigureIndex

`ConfigureIndex` gets the `SearchIndex` that the package built: its fields, the suggester, the `relevanceBoost` scoring profile and the analyzers it uses. Add to it or change it.

A second overload also gets an `AzureSearchIndexContext`, which says which Azure index the callback configures:

| Property | Value |
|---|---|
| `IndexAlias` | The Umbraco index alias, for example `siteSearch` |
| `IndexName` | The Azure index name, for example `sitesearch-fr` |
| `Culture` | The culture key of an `IndexPerCulture` culture index, for example `fr`. Null otherwise. |
| `IsFallback` | True for the `IndexPerCulture` fallback index |

```csharp
.ConfigureIndex((searchIndex, context) =>
{
    if (context.Culture == "fr")
    {
        searchIndex.ScoringProfiles.Add(new ScoringProfile("titleFirst")
        {
            TextWeights = new TextWeights(new Dictionary<string, double> { ["title_texts_r1"] = 5 })
        });
    }
})
```

Example: a custom analyzer for a field.

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .ConfigureIndex(searchIndex => searchIndex.Analyzers.Add(
        new CustomAnalyzer("names_folded", LexicalTokenizerName.Standard)
        {
            TokenFilters = { TokenFilterName.Lowercase, TokenFilterName.AsciiFolding }
        }))
    .Field("names", f => f.Searchable().Analyzer("names_folded")));
```

With a `ConfigureIndex` callback, startup validation accepts analyzer names that are not built into Azure, because the callback can add them. Azure reports a wrong name when the package creates the index.

### When the package applies ConfigureIndex

| When | What happens |
|---|---|
| The package creates an index (at startup with `EnsureIndicesOnStartup()`, on a rebuild, or when it creates an `IndexPerCulture` fallback index) | The callback runs on the new index before it is created. Every change applies. |
| Startup, on an existing index | The callback runs on the index built from code. The package applies new fields, scoring profiles and the default scoring profile to the live index. Other changes, for example new analyzers, apply on the next rebuild. |

With `IndexPerCulture`, the callback runs once for every Azure index of the alias. The package does not run it at any other time.

## ConfigureSearch

`ConfigureSearch` gets the `SearchOptions` of each Azure query on the index, after the package has set its own options, and an `AzureSearchRequest`:

| Property | Value |
|---|---|
| `IndexAlias` | The Umbraco index alias |
| `IndexName` | The Azure index the query goes to |
| `Query` | The query text as the caller passed it |
| `Culture` | The culture as the caller passed it, for example `fr-FR`. Null for no culture. |
| `Segment` | The segment as the caller passed it |

It runs on the main query and on the extra facet query that the package sends when a facet and a filter use the same field. It does not run on term autocomplete (`maxSuggestions`), which uses `AutocompleteOptions`.

Example: a per-culture query change.

```csharp
.ConfigureSearch((options, request) =>
{
    if (request.Culture?.StartsWith("fr", StringComparison.OrdinalIgnoreCase) is true)
    {
        options.ScoringProfile = "titleFirst";
    }
})
```

### SearchOptions the package sets

| Property | What the package puts there |
|---|---|
| `Filter` | Culture, segment and access (protected content) clauses, then the filters of the request |
| `SearchFields` | The per-culture copies in `FieldPerCulture` mode. Empty otherwise. |
| `OrderBy` | The sorters of the request, then `id asc` as a tie-breaker |
| `Facets` | The facets of the request |
| `Skip`, `Size`, `IncludeTotalCount`, `QueryType`, `SearchMode` | From the request |

Add to `Filter` and `SearchFields`. Do not replace them. Replacing `Filter` removes the culture and access clauses, so a query can return other cultures and protected content.

```csharp
options.Filter = $"({options.Filter}) and objectType eq 'Document'";
```

## Gotchas

- A wrong field name breaks indexing or queries at runtime, not at startup. Azure rejects a scoring profile that names a field the index does not have.
- Azure rejects a custom analyzer on a suggester field. Do not map a culture to a custom analyzer while suggestions use the culture analyzers. Set `Analyzers:SuggestionAnalyzer` to `standard.lucene`.
- `ConfigureSearch` runs for every query on the index, including backoffice and Delivery API queries. Keep it fast, and do not call services in it.
- Field names are Azure field names, not Umbraco field names. An Umbraco field `title` becomes `title_texts`, `title_texts_r1` to `title_texts_r3`, `title_keywords` and so on. Per-culture copies follow the [naming rule](analyzers.md#physical-names).
- A rebuild recreates the index from code. Scoring profiles or analyzers added in the Azure portal are lost. Add them with `ConfigureIndex`.
