# Suggestions

## When to use this

Use term suggestions to complete the last word of a query in a search box. Suggestions are on by default for every index.

Needs a rebuild: yes, to fill suggestions for content that was indexed before version 17.3.0 or 18.2.0. See [Upgrading](#upgrading).

## Term suggestions

Pass `maxSuggestions` to get suggested next queries for a search box. Each suggestion is the typed query with its last word completed, so `"red bo"` can give `"red boat"` and `"red board"`. Suggestions come back in the same response as the results.

```csharp
[ApiController]
[Route("api/search")]
public class SearchController(IAzureSearchSearcher searcher) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string query)
    {
        var result = await searcher.SearchAsync(
            indexAlias: SearchConstants.IndexAliases.PublishedContent,
            query: query,
            maxSuggestions: 5);

        return Ok(new { result.Total, suggestions = result.Suggestions ?? [] });
    }
}
```

- Suggestions are lowercase.
- `Suggestions` is `null` when there are none: `maxSuggestions` is 0, the query is blank, or nothing matches. Handle `null`, not only an empty list.
- Suggestions use the same culture, segment, protected-content and filter rules as the results, except text filters (see the limitations below).
- The Delivery API and the backoffice do not ask for suggestions, so only your own code triggers the extra request.

By default every index suggests from its highest-relevance texts, such as content names. Configure an index under its alias to change that. The settings are part of the [index configuration](index-configuration.md).

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "Umb_PublishedContent": {
        "Suggestions": {
          "Fields": [ "title", "subtitle" ]
        }
      },
      "Umb_Members": {
        "Suggestions": {
          "Enabled": false
        }
      }
    }
  }
}
```

```csharp
builder.Services
    .ConfigureAzureSearchIndex("Umb_PublishedContent", index => index.SuggestionFields("title", "subtitle"))
    .ConfigureAzureSearchIndex("Umb_Members", index => index.Suggestions(false));
```

When appsettings and code set the same value, code wins.

| Setting | Builder method | Default | Effect |
|---|---|---|---|
| `Enabled` | `.Suggestions(bool)` | `true` | `false` gives the index no suggestions and saves the index space. |
| `Fields` | `.SuggestionFields(params string[])` | Highest-relevance texts | The Umbraco fields whose texts feed suggestions. |

The settings used to be under `UmbracoAzureSearch:Suggestions:<alias>`. The package still reads that location and logs a warning. See [Settings that move](upgrading.md#settings-that-move).

### Upgrading

Existing indexes get suggestions added on startup when `EnsureIndicesOnStartup()` is registered, but their existing content has no suggestions yet. Rebuild each index in the backoffice to fill them. A rebuild also adds suggestions to an index on sites that do not ensure indexes on startup.
