# Suggestions

## When to use this

Use suggestions in a search box. The package has two kinds:

| | Term suggestions | Document suggestions |
|---|---|---|
| Returns | Queries: the typed query with its last word completed, for example "red boat" | Documents: the key, object type, matched text and selected fields of each document |
| API | `maxSuggestions` on `SearchAsync`, in the same response as the results | `SuggestAsync`, a call of its own |
| Azure endpoint | Autocomplete | Suggest |
| Errors | Logged; the search still returns its results without suggestions | Thrown to the caller |
| Fuzzy | No | Optional, per call |

Suggestions are on by default for every index. One suggester per index serves both kinds.

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

With the `FieldPerCulture` analyzer mode, the suggestion field gets a copy per mapped culture by default, and a mapped culture completes from its copy plus the invariant copy. Set `Analyzers:SuggestionAnalyzer` to a fixed analyzer, for example `standard.lucene`, to keep one shared suggestion field. See [SuggestionAnalyzer](analyzers.md#suggestionanalyzer) for when to pick which.

The settings used to be under `UmbracoAzureSearch:Suggestions:<alias>`. The package still reads that location and logs a warning. See [Settings that move](upgrading.md#settings-that-move).

## Suggester rules

- Each index has one suggester, `sg`. Term suggestions and document suggestions use it.
- The suggester holds the suggestion field. With the `FieldPerCulture` analyzer mode it also holds the [suggestion copies](analyzers.md#suggestionanalyzer).
- The suggestion texts come from the fields in `Suggestions:Fields` and the fields opted in with `.Suggestable()`. When both name fields, the suggestion texts come from all of them. When neither names a field, they come from the highest-relevance texts.
- Azure adds fields to a suggester only when it creates the suggester. A change to the suggestion fields or their analyzers needs a rebuild.
- With `Suggestions:Enabled: false` the index has no suggester.

## Document suggestions

`SuggestAsync` suggests documents whose suggestion texts match a partial query, for a search box that shows pages while the user types.

```csharp
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

public record SuggestResult(IEnumerable<SuggestedDocument> Documents);

public record SuggestedDocument(
    Guid Id,
    UmbracoObjectTypes ObjectType,
    string Text,                                  // matched text with highlight tags
    IReadOnlyDictionary<string, object?> Fields); // fields named in select
```

Example: suggestions under one site root.

```csharp
var result = await searcher.SuggestAsync(
    indexAlias: "siteSearch",
    query: query,
    filters: [new KeywordFilter("Umb_PathIds", [siteRootKey.ToString()], Negate: false)],
    culture: culture,
    top: 5,
    fuzzy: query.Length >= 4,
    select: ["title", "contentType"]);
```

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "siteSearch": {
        "Suggestions": {
          "Fields": [ "title", "names" ],
          "HighlightPreTag": "<em>",
          "HighlightPostTag": "</em>"
        }
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .SuggestionFields("title", "names")
    .SuggestionHighlight("<em>", "</em>"));
```

When appsettings and code set the same value, code wins.

| Parameter | Behavior |
|---|---|
| `filters` | The same `Filter` types as `SearchAsync`. Text filters are left out, as for term suggestions. A filter on a field no document has finds nothing; negated, it is left out. |
| `culture` | Documents of the culture and invariant documents match. With `FieldPerCulture` and suggestion copies, a mapped culture suggests from its copy plus the invariant copy. With a fixed `SuggestionAnalyzer`, the shared field and the culture filter apply. With `IndexPerCulture`, the culture picks the index, and a call without a culture throws. |
| `segment` | As in `SearchAsync`. |
| `accessContext` | Protected content as in `SearchAsync`. |
| `top` | 5 by default. Values are limited to 1 to 100. |
| `query` | Whitespace is collapsed. A query over 100 characters is cut to 100. A blank query returns no documents without calling Azure. |
| `fuzzy` | Off by default. See [Fuzzy](#fuzzy). |
| `select` | Umbraco field names, for example `title`. The package returns the values of the matching Azure fields (`title_texts`, `title_keywords` and so on) under the name you passed. A field the index does not have yet is null. Names must be letters, digits and underscores. |

### Highlights

`Text` is the matched suggestion text with the highlight tags around the match: `<em>Red</em> boat` by default. Set `Suggestions:HighlightPreTag` and `Suggestions:HighlightPostTag` per index to change the tags.

### Fuzzy

`fuzzy: true` also matches index terms within one edit of the typed term, so "baot" finds "boat". It is slower, so turn it on only when the query is long enough to have a typo, for example from 4 characters.

Fuzzy compared to spell check:

| | Fuzzy | Spell check |
|---|---|---|
| How | Accepts index terms within one edit of the typed term | Rewrites the query with a dictionary (the Azure lexicon speller) |
| Where | Document suggestions | Full search only |
| Languages | Every language | Only the languages the speller supports |

### Gotchas

- `SuggestAsync` throws Azure errors. Catch them in the endpoint that calls it.
- `Text` contains the highlight tags, and the suggestion text can contain HTML from the content. Encode or sanitize it before you render it.
- Text filters do not narrow suggestions. Azure rejects them in suggestion requests.
- An index with `Suggestions:Enabled: false` has no suggester. `SuggestAsync` throws `InvalidOperationException`: `Suggestions are off for the index "siteSearch" (UmbracoAzureSearch:Indexes:siteSearch:Suggestions:Enabled), so it has no suggester.`
- An existing index that gets suggestions needs a rebuild to fill them. See [Upgrading](#upgrading).

## Upgrading

Existing indexes get suggestions added on startup when `EnsureIndicesOnStartup()` is registered, but their existing content has no suggestions yet. Rebuild each index in the backoffice to fill them. A rebuild also adds suggestions to an index on sites that do not ensure indexes on startup.
