# Suggestions spec

Fill `SearchResult.Suggestions` when a caller passes `maxSuggestions > 0` to `ISearcher.SearchAsync`. Use the Azure AI Search [Autocomplete API](https://learn.microsoft.com/en-us/rest/api/searchservice/documents/autocomplete-post) on a dedicated suggester.

This spec is locked. Each section links the decision ticket that holds the reasoning. The map is [Wayfinder: suggestions / typeahead](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/1). Delete this file in the PR that ships the feature.

## Scope

In scope:

- The existing core contract only: `maxSuggestions` in, `IEnumerable<string>? Suggestions` out.
- Index schema, document mapping, query behavior, schema upgrade, settings, tests, README.

Out of scope:

- New suggestion or autocomplete methods on `IAzureSearchSearcher`.
- The Elasticsearch provider.
- Backoffice UI.

## Meaning of a suggestion

A suggestion is a suggested next query: the typed query with its last term completed. "red bo" gives "red board". It is not a document title. This matches Umbraco Search RFC 0027 and the Elasticsearch reference provider.

Source: [What do Umbraco Search core and other providers mean by suggestions?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/19), [Suggest API or Autocomplete API?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/21)

## Settings

Add per-index-alias suggestion settings to `UmbracoAzureSearchOptions`. Keys are the Umbraco index aliases (for example `Umb_PublishedContent`), before `IndexAliasResolver` adds the environment suffix.

```json
{
  "UmbracoAzureSearch": {
    "Suggestions": {
      "Umb_PublishedContent": {
        "Enabled": true,
        "Fields": [ "title", "subtitle" ],
        "Fuzzy": false
      },
      "Umb_Members": {
        "Enabled": false
      }
    }
  }
}
```

```csharp
public class UmbracoAzureSearchOptions
{
    // existing properties ...

    public Dictionary<string, SuggestionOptions> Suggestions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class SuggestionOptions
{
    public bool Enabled { get; set; } = true;

    // Umbraco field names. Null or empty: use all R1 texts.
    public string[]? Fields { get; set; }

    public bool Fuzzy { get; set; }
}
```

| Setting | Default | Effect |
|---|---|---|
| `Enabled` | `true` | `false`: no suggestion field, no suggester, no Autocomplete call for this alias. |
| `Fields` | all R1 texts | Listed fields replace the R1 default. All text values of a listed field (Texts, R1, R2, R3) are used. |
| `Fuzzy` | `false` | `true`: Autocomplete runs with `UseFuzzyMatching = true` (edit distance 1). |

An alias with no entry uses the defaults. The setting names are a proposal and can change in review.

Source: [Are suggestions opt-in per index?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/26), [What content feeds the suggestion field?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/22), [How does the suggestion query handle edge cases?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/27)

## Index schema

For each enabled alias, the index gets:

- A field `suggestion`: `Edm.String`, searchable, analyzer `standard.lucene`. Not filterable, sortable or facetable.
- A suggester `sg` with source field `suggestion`.

Add `Suggestion = "suggestion"` to `IndexConstants.FieldNames` and `SuggesterName = "sg"` to `IndexConstants`.

Azure constraints that shape this:

- An index has at most one suggester.
- Suggester fields must be `Edm.String`. `Collection(Edm.String)` is not allowed, so the existing `allTexts*` fields cannot be used.
- Suggester fields must use `standard.lucene` or a language analyzer.

Put the schema in one method that both `EnsureAsync` and `ResetAsync` call, so both build the same definition.

Source: [Azure suggester constraints and API behavior](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/20)

## Document mapping

In `DocumentMapper.MapVariationToDocument`, for an enabled alias:

- Every culture and segment document gets its own variation's text, selected by the same rules as `allTexts`.
- Source: all `TextsR1` values of the variation, or all text values of the configured `Fields`.
- Use the values after the existing HTML stripping.
- Join the values with a single space. No length cap.
- No values: set the field to `null`.

For a disabled alias, do not set the field, even if the index still has it.

Joined values can give rare cross-value context ("home ab" can suggest "home about"). This is accepted.

`DocumentMapper` needs the index alias and the suggestion settings to do this.

Source: [What content feeds the suggestion field?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/22)

## Query

In `AzureSearchSearcher.SearchCoreAsync`, call Autocomplete when all of these are true:

- `maxSuggestions > 0`.
- The trimmed query is not blank.
- Suggestions are enabled for the alias.

Otherwise `Suggestions = null` and no call is made.

Note that the public `SearchAsync` overloads do not pass `maxSuggestions` to `SearchCoreAsync` today. Pass it through.

### Request

| Option | Value |
|---|---|
| Search text | The caller's `query`, trimmed, runs of whitespace collapsed to one space. Never the `*`-suffixed `searchText`. |
| Suggester | `sg` |
| `Mode` | `AutocompleteMode.OneTermWithContext` |
| `Size` | `Math.Min(maxSuggestions, 100)` |
| `Filter` | See below. |
| `UseFuzzyMatching` | The alias `Fuzzy` setting. |
| Highlight tags | Not set. |

No query length cap. Azure documents no length limit for Autocomplete. If Azure rejects a query, the failure rules below apply.

### Filter

Join with `and`:

- The base clauses of the document query, unchanged: culture (requested culture or invariant), segment (requested segment or default), access keys from `AccessContext`.
- Every keyword, integer, decimal, date and range filter from `filters`. Include negated filters and filters on faceted fields. Use the full `filtersArray`, not `regularFilters`.

Do not include text filters. They compile to `search.ismatchscoring`, which Azure rejects in Autocomplete filters. Suggestions can then be broader than the results, but never beyond the access clause.

Source: [Which search constraints apply to suggestions?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/23)

### Output

1. Take `QueryPlusText` from each result. Azure returns it in lowercase.
2. Remove duplicates with `StringComparer.OrdinalIgnoreCase`.
3. Keep Azure's order.
4. `Take(maxSuggestions)`.
5. No results: `null`.

Every "no suggestions" case returns `null`, never an empty list: not requested, blank query, alias disabled, no suggester, no match, request failed.

Examples from a live test with indexed text "Red boat", "Red board", "Read more":

| Query | Suggestions |
|---|---|
| `re` | `read`, `red` |
| `red bo` | `red board`, `red boat` |

Source: [How does the suggestion query handle edge cases?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/27), [Live test: add a field and suggester to an existing Azure index](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/25)

### Request flow and failures

- Once the filter clauses exist, start the main search, the same-field facet search (when needed) and the Autocomplete request (when needed) together. Await all of them before building the result.
- The facet search depends only on `searchText` and `facetFilterClauses`, so this changes ordering only.
- Main or facet request fails: throw, as today.
- Autocomplete fails with `RequestFailedException`: return documents and facets as normal with `Suggestions = null`, and log a warning with the status code.
- Autocomplete fails because the index has no suggester (an index not yet upgraded): log the warning once per index alias, not on every search.
- Do not look up the index schema per search.

`AzureSearchSearcher` needs an `ILogger<AzureSearchSearcher>` for this.

Source: [Does the Autocomplete request run in parallel with the search?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/28)

## Schema upgrade for existing indexes

Azure allows a new field and a suggester on it in one index update. Azure rejects a suggester on a field that already exists (HTTP 400 `OperationNotAllowed`). A live test confirmed both.

### `EnsureAsync`

Today it returns early when the index exists. Change it:

- Index missing: create it from the shared schema, as today.
- Index exists, alias enabled, `suggestion` field missing: add the field and suggester `sg` in one `CreateOrUpdateIndexAsync` call. Then log a warning that tells the admin to rebuild the index to fill suggestions.
- Index exists and already has the field, or alias disabled: change nothing.

Do not start a rebuild automatically. Documents also fill as content is saved.

### `ResetAsync`

Today it deletes the index and recreates the old definition. Change it to delete the index and create it from the shared schema. A rebuild then upgrades and refills the index in one step, also on sites that do not call `EnsureIndicesOnStartup()`. `EnsureFieldsExist` re-adds property fields during the rebuild, as today.

### Turning suggestions off and on

- Off for an alias that has the field: the mapper stops filling it and the searcher stops calling Autocomplete at once. Azure cannot remove a field or suggester in place, so they stay until the next `ResetAsync`, which drops them.
- On again after a reset removed them: `EnsureAsync` or the next rebuild adds them.

Source: [How do existing indexes get the suggestion field and suggester?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/24), [Live test: add a field and suggester to an existing Azure index](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/25)

## Tests

### Unit tests

Use `FakeSearchService`. These run in CI.

- Autocomplete request: suggester `sg`, mode `oneTermWithContext`, filter is the base clauses plus all non-text user filters, `top` clamped to 100, trimmed and collapsed raw query without `*`, fuzzy only when the alias flag is on, no highlight tags.
- No Autocomplete call and `Suggestions = null` for `maxSuggestions <= 0`, a blank query, and a disabled alias.
- Canned response: `queryPlusText` used, case-insensitive duplicates removed, order kept, `Take(maxSuggestions)`, empty response gives `null`.
- Autocomplete failure: documents and facets returned, `Suggestions = null`, warning logged. Missing-suggester warning logged once per alias.
- Main, facet and Autocomplete requests are all sent.
- `EnsureAsync`: one update adding field and suggester when missing, no update when present or alias disabled, rebuild warning logged.
- `ResetAsync`: creates the index from the shared schema.
- `DocumentMapper`: R1 default, `Fields` override, space join, `null` when empty, not set for a disabled alias.

### Integration tests

Inherit `AzureSearchTestBase` (category `Integration`). These run locally against a real service.

- Port every case from `ElasticsearchSuggestionTests.cs` in the [Elasticsearch reference provider](https://github.com/kjac/Kjac.SearchProvider.Elasticsearch) (commit `3fef9ab`). Adapt expected output to Azure: lowercase, oneTermWithContext. Invert the text-filter case: text filters do not narrow suggestions.
- Add cases for segment scoping and the `Fuzzy` setting.
- One upgrade test: create an index with the old schema, run `EnsureAsync`, assert the field and suggester exist, delete the index.

Source: [How are suggestions tested?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/29)

## README

Add a "Suggestions" section:

- A `maxSuggestions` example in a custom search controller. The Delivery API and backoffice do not request suggestions.
- The per-alias settings: `Enabled` (on by default), `Fields` (R1 by default), `Fuzzy`.
- Upgrade note: rebuild existing indexes after upgrading to fill suggestions.
- Output: lowercase full queries, `null` when there are none.

Add one line to "Azure AI Search limitations": text filters do not narrow suggestions.

Keep internals (field and suggester names, Autocomplete mode, request flow) out of the README.

Source: [Where does the final spec live, and what goes in the README?](https://github.com/krebil/Krebil.UmbracoAzureSearch/issues/30)
