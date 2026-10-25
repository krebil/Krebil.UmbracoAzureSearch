# Analyzers

## When to use this

Use language analyzers when a site searches text in several cultures. The default analyzer, `standard.lucene`, splits words and lowercases them. It does not handle plurals, verb forms, French elision (`l'avion`) or accents. A language analyzer such as `fr.microsoft` does, so "avions" finds "avion".

Defaults:

| Setting | Default |
|---|---|
| `Analyzers:Mode` | `None`: shared fields with `standard.lucene` |
| `Analyzers:Cultures` | None mapped |
| `Analyzers:Invariant` | `standard.lucene` |
| `Analyzers:SuggestionAnalyzer` | `PerCulture` (no effect while the mode is `None`) |

Needs a rebuild: yes. A change of mode, culture or analyzer needs a rebuild. See [Gotchas](#gotchas).

The analyzers are part of the [index configuration](index-configuration.md). appsettings applies first, then code. When both set the same value, code wins.

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "siteSearch": {
        "Analyzers": {
          "Mode": "FieldPerCulture",
          "Cultures": { "fr": "fr.microsoft", "en": "en.microsoft", "es": "es.microsoft", "pt": "pt-Br.microsoft", "ar": "ar.microsoft" },
          "Invariant": "standard.lucene",
          "SuggestionAnalyzer": "standard.lucene"
        }
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .Analyzers(a => a
        .FieldPerCulture()
        .Culture("fr", LexicalAnalyzerName.FrMicrosoft)
        .Invariant(LexicalAnalyzerName.StandardLucene)
        .SuggestionAnalyzer(LexicalAnalyzerName.StandardLucene))
    .Field("title", f => f.Searchable().PerCulture().Boost(3)));
```

## Modes

| Mode | Layout | Use when | Cost |
|---|---|---|---|
| `None` | Shared text fields with `standard.lucene` for every culture | One culture, or text where word forms do not matter | None |
| `FieldPerCulture` | One copy of the aggregate text fields and the suggestion field per mapped culture, plus an invariant copy | Several cultures in one index | About 5 extra fields per mapped culture, and the text is stored once per copy it goes into |
| `IndexPerCulture` | One Azure index per mapped culture, plus a fallback index for languages outside the map | Every field must use the culture analyzer, or the cultures differ a lot in size | One Azure index per mapped culture, and invariant documents are stored in every index |

## FieldPerCulture

Each mapped culture gets a copy of the four aggregate text fields (`allTexts`, `allTextsR1` to `allTextsR3`) with the culture analyzer. An invariant copy uses the `Invariant` analyzer. With `SuggestionAnalyzer: PerCulture`, the suggestion field also gets a copy per mapped culture and an invariant copy.

What each document writes:

| Document | Culture copies | Invariant copies | Shared fields |
|---|---|---|---|
| Mapped culture | The culture texts | The invariant property texts of the item | Empty |
| Invariant (`culture = inv`) | None | All texts | All texts |
| Unmapped culture | None | None | All texts |

What each query searches:

| Query culture | Fields searched |
|---|---|
| Mapped | The copies of that culture plus the invariant copies |
| Unmapped, or no culture | The shared aggregate text fields |

The culture filter is the same as before: `culture eq '<culture>' or culture eq 'inv'`. Term autocomplete (`maxSuggestions`) completes from the suggestion copy of the culture plus the invariant copy.

### PerCulture fields

`.PerCulture()` (or `"PerCulture": true`) on a searchable opted-in field gives its four text fields a copy per mapped culture and an invariant copy, with the culture analyzers. A query in a mapped culture searches those copies too. Its text is already part of the aggregate copies, so `.PerCulture()` matters most for scoring profiles that boost the field.

A field without `.PerCulture()` keeps its shared text fields only. `.PerCulture()` has no effect in mode `None`.

### Physical names

A copy is named `<field>_<suffix>`. The suffix is the culture key in lowercase, with `-` replaced by `_`. The invariant suffix is `inv`.

| Culture key | Aggregate copy | Suggestion copy | Copy of `title` (PerCulture) |
|---|---|---|---|
| `fr` | `allTextsR1_fr` | `suggestion_fr` | `title_texts_r1_fr` |
| `pt-BR` | `allTextsR1_pt_br` | `suggestion_pt_br` | `title_texts_r1_pt_br` |
| Invariant | `allTextsR1_inv` | `suggestion_inv` | `title_texts_r1_inv` |

Use these names in `ConfigureIndex` and in scoring profiles.

## IndexPerCulture

Each mapped culture gets its own Azure index, `<alias>-<culture>`, where the alias and the culture key are in lowercase. Every text field in that index uses the culture analyzer: the aggregate text fields, the suggestion field, and every field without its own `Analyzer`. There are no per-culture copies, so `.PerCulture()` has no effect.

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "siteSearch": {
        "Analyzers": {
          "Mode": "IndexPerCulture",
          "Cultures": { "fr": "fr.microsoft", "en": "en.microsoft" }
        }
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .Analyzers(a => a
        .IndexPerCulture()
        .Culture("fr", LexicalAnalyzerName.FrMicrosoft)
        .Culture("en", LexicalAnalyzerName.EnMicrosoft)));
// Azure indexes: sitesearch-fr, sitesearch-en.
// sitesearch-fallback only when Umbraco has a language other than fr and en.
```

Rules:

- A document of a mapped culture goes to the index of that culture. Culture keys match as described in [Culture keys](#culture-keys).
- Invariant documents (`culture = inv`) go to every index of the alias, including the fallback index.
- A query goes to the index of its culture and finds the invariant documents there. A query without a culture fails with an error, because there is no index to send it to.
- The mode is not allowed on the backoffice indexes (`Umb_Content`, `Umb_Media`, `Umb_Members`). The backoffice searches them without a culture. `Umb_PublishedContent` and custom indexes can use it.
- All indexes of one alias rebuild together. A rebuild from the backoffice deletes and recreates every one of them.
- `SuggestionAnalyzer: PerCulture` gives each index a suggestion field with its culture analyzer. A fixed analyzer applies to the suggestion field of every index.
- The document count in the backoffice is the sum over all indexes, so invariant documents count once per index.
- The index from the `None` or `FieldPerCulture` mode is not deleted when you switch modes. Delete it in the Azure portal.

### IndexPerCulture can also create a fallback index

The mode name does not say so, but the alias can have one more index: `<alias>-fallback`.

| Question | Answer |
|---|---|
| When does it exist? | Only when Umbraco has a language outside the culture map. When every language is mapped, there is no fallback index. |
| Which cultures go into it? | Every culture outside the map, for example `da-DK` when only `fr` and `en` are mapped. |
| Which analyzer does it use? | `standard.lucene`. |
| Does it hold invariant documents? | Yes. Invariant documents are copied into it, as into every index of the alias. |
| Does it count against the tier? | Yes. It counts against the index limit of the service like any other index. |

When you add an Umbraco language outside the map, the package creates the fallback index at the next startup (with `EnsureIndicesOnStartup()`), or when the first document of that language is indexed. It logs a warning that a rebuild is required. Rebuild the index to fill the fallback index with existing content.

### Index count

An alias with `IndexPerCulture` uses:

```
indexes = mapped cultures + (1 if a fallback index exists)
```

Zero-downtime rebuild, which a later version adds, keeps two indexes per index during a rebuild, so double the count for that.

Example: `fr` and `en` mapped, and Umbraco also has `da-DK`. The alias uses 3 indexes. The other default Umbraco indexes use 1 each, so the service needs 6 indexes.

The package checks the total at startup against `UmbracoAzureSearch:MaxIndexes` (default `15`, the Basic limit) when an index uses `IndexPerCulture`. Set it to your tier's limit: 3 on Free, 15 on Basic, 50 on S1, 200 on S2 and S3. When the package cannot read the Umbraco languages, it counts the fallback index.

## Culture keys

A culture key matches a culture exactly first, then by its language part. Matching is case-insensitive.

| Keys | Culture | Copy used |
|---|---|---|
| `pt` | `pt-BR`, `pt-PT` | `pt` |
| `pt`, `pt-BR` | `pt-BR` | `pt-BR` (the more specific key wins) |
| `pt`, `pt-BR` | `pt-PT` | `pt` |
| `fr` | `da-DK` | None: an unmapped culture uses the shared fields |

To add a culture, add its key and analyzer to `Cultures`, then rebuild the index. The package adds the new aggregate copies at startup or when the next document is indexed. Azure adds suggestion fields only when it creates the suggester, so the new suggestion copy comes with the rebuild. Until then the package leaves it out of documents and logs a warning.

## SuggestionAnalyzer

| Value | Suggestion fields |
|---|---|
| `PerCulture` (default) | One copy per mapped culture and an invariant copy, each with the culture analyzer. Mapped cultures complete from their copy. |
| An analyzer name, for example `standard.lucene` | One shared suggestion field with that analyzer. The culture filter keeps suggestions to the request culture. |

Term autocomplete returns the indexed tokens. A language analyzer indexes stems or base forms, so its suggestions can show broken words: "avion" for "avions", or "bateau" as "bat". Pick `standard.lucene` when suggestions must show whole words as they appear in the content. Keep `PerCulture` when suggestions should match the word forms that the search matches.

Suggestion fields allow only `standard.lucene` or a language analyzer (`<language>.microsoft` or `<language>.lucene`). Azure rejects other analyzers on suggester fields, so startup validation stops on them.

## Validation

| Check | Example error |
|---|---|
| A culture analyzer exists in Azure | `Analyzers:Cultures:pt is "pt-BR", which Azure AI Search does not have. Use a built-in analyzer name such as "standard.lucene" or "pt-Br.microsoft", or add the analyzer with ConfigureIndex.` |
| The invariant and suggestion analyzers exist in Azure | `Analyzers:Invariant is "standard", which Azure AI Search does not have. ...` |
| FieldPerCulture maps at least one culture | `Analyzers:Mode is FieldPerCulture, but Analyzers:Cultures maps no culture. Map at least one culture to an analyzer.` |
| A suggestion field analyzer is allowed | `The suggestion field "suggestion_fr" uses the analyzer "keyword". Suggestion fields need "standard.lucene" or a language analyzer. Set Analyzers:SuggestionAnalyzer to "standard.lucene".` |
| `IndexPerCulture` maps at least one culture | `Analyzers:Mode is IndexPerCulture, but Analyzers:Cultures maps no culture. ...` |
| `IndexPerCulture` is not on a backoffice index | `Analyzers:Mode IndexPerCulture is not allowed on the backoffice index "Umb_Content". Use FieldPerCulture.` |
| The Azure indexes fit the limit (when an index uses `IndexPerCulture`) | `The Umbraco indexes need 6 Azure indexes (Umb_Content: 1, Umb_PublishedContent: 3, Umb_Media: 1, Umb_Members: 1), but the service allows 5 (UmbracoAzureSearch:MaxIndexes). Map fewer cultures, or use FieldPerCulture.` |
| The fields fit the limit, copies included | `The index needs 56 Azure fields, but the service allows 50 (UmbracoAzureSearch:MaxFieldsPerIndex). ...` |

See [Field budget](index-definition.md#field-budget) for how fields are counted.

## Gotchas

- A change of mode, culture or analyzer needs a rebuild. Azure cannot change the analyzer of an existing field, and existing documents have no text in new copies.
- Each mapped culture adds about 5 fields: 4 aggregate copies and 1 suggestion copy. Each `.PerCulture()` field adds 4 more per mapped culture. Watch the field limit on older Basic services (100 fields).
- Basic allows 15 indexes, and older Basic services 5. Count the indexes before you turn on `IndexPerCulture`, and set `UmbracoAzureSearch:MaxIndexes`.
- Use Azure analyzer names, not culture names: `pt-Br.microsoft`, not `pt-BR`. See the [list of language analyzers](https://learn.microsoft.com/azure/search/index-add-language-analyzers#supported-language-analyzers).
- A single-word query gets a `*` suffix for prefix matching. Azure does not run the language analyzer on prefix terms, so a one-word query matches the start of indexed tokens, not word forms.
