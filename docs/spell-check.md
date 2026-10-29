# Spell check

## When to use this

Use spell check to find results when users mistype query terms. The Azure lexicon speller corrects each query term with a dictionary before the search runs, so "famly acitvites" finds "family activities".

Defaults: off. No culture has spell check.

Needs a rebuild: no. Spell check changes the query, not the index.

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "siteSearch": {
        "SpellCheck": {
          "Cultures": { "en": "en-us", "es": "es-es", "fr": "fr-fr" }
        }
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .SpellCheck(s => s
        .Culture("en", QueryLanguage.EnUs)
        .Culture("es", QueryLanguage.EsEs)
        .Culture("fr", QueryLanguage.FrFr)));
```

When appsettings and code set the same culture, code wins.

## What it does

For a search in a mapped culture, the package sends `speller=lexicon` and the `queryLanguage` of the culture to Azure. A culture without a mapping gets no correction and no speller parameters.

Culture keys match as for the [analyzers](analyzers.md#culture-keys): exactly first, then by the language part, case-insensitive. `en` covers `en-US` and `en-GB`.

Spell check runs on full search (`SearchAsync`) only. Term suggestions (`maxSuggestions`) and document suggestions (`SuggestAsync`) do not support it. Use `fuzzy: true` on `SuggestAsync` instead.

### What the caller sees

The results of the corrected query. Azure does not return the corrected query text, so the package cannot show "Did you mean ...". `SearchResult` is unchanged.

## Supported languages

| Language | queryLanguage values |
|---|---|
| English | `en`, `en-us` |
| Spanish | `es`, `es-es` |
| French | `fr`, `fr-fr` |
| German | `de`, `de-de` |
| Dutch | `nl`, `nl-be`, `nl-nl` |

Portuguese, Arabic and Chinese are not supported. See the [Azure list of speller languages](https://learn.microsoft.com/azure/search/speller-how-to-add#supported-languages) for the current list. A query language outside the list stops startup:

`SpellCheck:Cultures:pt is "pt-br", which the Azure lexicon speller does not support. Use one of: de, de-de, en, en-us, es, es-es, fr, fr-fr, nl, nl-be, nl-nl.`

## Requirements

- A Basic tier service or higher. When the service rejects the speller (for example on the Free tier), the package searches again without spell check, logs one warning per index, and skips spell check for that index until the application restarts.
- A preview API version. The package uses the default of the Azure SDK, `2026-05-01-preview`.
- No semantic ranker, and no extra charge.

## Turn on a culture later

Add the culture to `SpellCheck:Cultures` and restart the application. No rebuild, and no change to the code that calls `SearchAsync`.

## Languages the speller does not support

- Fuzzy matching. In a query with `queryType: Full`, `boat~1` matches terms within one edit. It works in every language, but queries are slower. For suggestions, use `fuzzy: true` on `SuggestAsync`.
- Query rewrite. The semantic ranker can rewrite queries. It needs the semantic ranker and is billed per request. Turn it on with [`ConfigureSearch`](customizing-azure.md#configuresearch).
- The lexicon speller, when Microsoft adds the language. Add the culture then; no index change is needed.

## Gotchas

- Spell check is an Azure preview feature. Microsoft can change it. The package uses API version `2026-05-01-preview`.
- A `queryLanguage` that does not match the content gives wrong corrections. Map each culture to its own language, and use language analyzers of the same language. See [Analyzers](analyzers.md).
- The package adds `*` to a one-word query for prefix matching (`boat` is sent as `boat*`). Azure does not correct prefix, wildcard, fuzzy or regex terms, so a one-word query is not corrected. Queries with two or more words are sent as typed and are corrected.
