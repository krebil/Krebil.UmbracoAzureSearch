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

None yet. Later versions add settings that need types, such as per-index indexers.

## Settings

| Setting | appsettings key | Builder method | Default | Needs a rebuild | Docs |
|---|---|---|---|---|---|
| Suggestions on or off | `Suggestions:Enabled` | `.Suggestions(bool)` | `true` | Yes, to fill suggestions after turning them on | [Suggestions](suggestions.md) |
| Suggestion fields | `Suggestions:Fields` | `.SuggestionFields(params string[])` | Highest-relevance texts | Yes | [Suggestions](suggestions.md) |

The appsettings keys are relative to `UmbracoAzureSearch:Indexes:<alias>`.

## Validation

The package checks the configuration when the application starts. An error stops startup. One exception lists every error.

| Check | Example error |
|---|---|
| The alias is registered with Umbraco Search | `UmbracoAzureSearch:Indexes:Umb_PublishedContnet configures the index "Umb_PublishedContnet", but Umbraco Search does not register an index with that alias. Registered content indexes: Umb_Content, Umb_Media, Umb_Members, Umb_PublishedContent.` |

The check applies to both sources. For an alias in code, the error starts with `ConfigureAzureSearchIndex("<alias>")`.

A custom index must be registered with Umbraco Search (`IndexOptions`) before you configure it:

```csharp
builder.Services.Configure<IndexOptions>(options =>
    options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IPublishedContentChangeStrategy>(
        "siteSearch", UmbracoObjectTypes.Document));

builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .Suggestions(false));
```
