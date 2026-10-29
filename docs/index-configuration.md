# Index configuration

## When to use this

Use the index configuration to change the settings of one index. Each index has its own settings, keyed by its Umbraco index alias. An index without an entry uses the defaults, so you only need this when you want to change a default.

Needs a rebuild: depends on the setting. See the settings table.

## Two sources

Two sources fill the settings of an index.

| Source | Where | Use it for |
|---|---|---|
| appsettings | `UmbracoAzureSearch:Indexes:<alias>` | The default Umbraco indexes (`Umb_Content`, `Umb_PublishedContent`, `Umb_Media`, `Umb_Members`) |
| Code | `ConfigureAzureSearchIndex("<alias>", index => ...)` | Custom indexes, and settings that need types or logic |

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "Umb_Members": {
        "Suggestions": {
          "Enabled": false
        }
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("Umb_PublishedContent", index => index
    .SuggestionFields("title", "subtitle"));
```

## Order

The package applies appsettings first, then code. When both set the same value, code wins. The order does not depend on where you call `ConfigureAzureSearchIndex`, before or after `AddUmbracoAzureSearch`.

A value that code does not set keeps its appsettings value. Several `ConfigureAzureSearchIndex` calls for one alias run in registration order.

Index aliases match in any case. `umb_publishedcontent` configures `Umb_PublishedContent`.

## Code-only settings

Content types, cultures, per-index indexers and the `ConfigureIndex` and `ConfigureSearch` callbacks need types and logic, so only code sets them. See [Index definition](index-definition.md).

## Settings

| Setting | appsettings key | Builder method | Default | Needs a rebuild | Docs |
|---|---|---|---|---|---|
| Suggestions on or off | `Suggestions:Enabled` | `.Suggestions(bool)` | `true` | Yes, to fill suggestions after turning them on | [Suggestions](suggestions.md) |
| Suggestion fields | `Suggestions:Fields` | `.SuggestionFields(params string[])` | Highest-relevance texts | Yes | [Suggestions](suggestions.md) |
| Suggestion highlight tags | `Suggestions:HighlightPreTag`, `Suggestions:HighlightPostTag` | `.SuggestionHighlight(pre, post)` | `<em>`, `</em>` | No | [Suggestions](suggestions.md#highlights) |
| Spell check per culture | `SpellCheck:Cultures:<culture>` | `.SpellCheck(s => s.Culture("en", ...))` | Off | No | [Spell check](spell-check.md) |
| Automatic fields | `AutomaticFields` | `.AutomaticFields(bool)` | `true` | Yes, to turn it off on an existing index | [Index definition](index-definition.md) |
| Opted-in fields and attributes | `Fields:<field>` | `.Field("<field>", f => ...)` | None | Yes, to change an attribute of an existing field | [Index definition](index-definition.md) |
| Content types | Code only | `.ContentTypes(params string[])` | Every content type | Yes | [Index definition](index-definition.md) |
| Cultures | Code only | `.Cultures(params string[])` | Every culture | Yes | [Index definition](index-definition.md) |
| Analyzer mode | `Analyzers:Mode` | `.Analyzers(a => a.FieldPerCulture())` or `.IndexPerCulture()` | `None` | Yes | [Analyzers](analyzers.md) |
| Culture analyzers | `Analyzers:Cultures:<culture>` | `.Analyzers(a => a.Culture("fr", ...))` | None | Yes | [Analyzers](analyzers.md) |
| Invariant analyzer | `Analyzers:Invariant` | `.Analyzers(a => a.Invariant(...))` | `standard.lucene` | Yes | [Analyzers](analyzers.md) |
| Suggestion analyzer | `Analyzers:SuggestionAnalyzer` | `.Analyzers(a => a.SuggestionAnalyzer(...))` | `PerCulture` | Yes | [Analyzers](analyzers.md#suggestionanalyzer) |
| Index definition changes | Code only | `.ConfigureIndex(...)` | None | Depends on the change | [Customizing Azure](customizing-azure.md) |
| Query changes | Code only | `.ConfigureSearch(...)` | None | No | [Customizing Azure](customizing-azure.md) |
| Per-index indexers | Code only | `.Indexer<TIndexer>()` | None | Yes, to fill existing content | [Index definition](index-definition.md) |

The appsettings keys are relative to `UmbracoAzureSearch:Indexes:<alias>`.

One setting applies to the whole service, not to one index:

| Setting | appsettings key | Default | Docs |
|---|---|---|---|
| Field limit of the service | `UmbracoAzureSearch:MaxFieldsPerIndex` | `1000` | [Field budget](index-definition.md#field-budget) |
| Index limit of the service | `UmbracoAzureSearch:MaxIndexes` | `15` | [Index count](analyzers.md#index-count) |

## Validation

The package checks the configuration when the application starts. An error stops startup. One exception lists every error.

| Check | Example error |
|---|---|
| The alias is registered with Umbraco Search | `UmbracoAzureSearch:Indexes:Umb_PublishedContnet configures the index "Umb_PublishedContnet", but Umbraco Search does not register an index with that alias. Registered content indexes: Umb_Content, Umb_Media, Umb_Members, Umb_PublishedContent.` |

The [index definition](index-definition.md#validation) adds checks for fields.

The check applies to both sources. For an alias in code, the error starts with `ConfigureAzureSearchIndex("<alias>")`.

A custom index must be registered with Umbraco Search (`IndexOptions`) before you configure it:

```csharp
builder.Services.Configure<IndexOptions>(options =>
    options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IPublishedContentChangeStrategy>(
        "siteSearch", UmbracoObjectTypes.Document));

builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .Suggestions(false));
```
