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
| A culture analyzer exists in Azure | `Analyzers:Cultures:pt is "pt-BR", which Azure AI Search does not have. Use a built-in analyzer name such as "standard.lucene" or "pt-Br.microsoft".` |
| The invariant and suggestion analyzers exist in Azure | `Analyzers:Invariant is "standard", which Azure AI Search does not have. ...` |
| FieldPerCulture maps at least one culture | `Analyzers:Mode is FieldPerCulture, but Analyzers:Cultures maps no culture. Map at least one culture to an analyzer.` |
| A suggestion field analyzer is allowed | `The suggestion field "suggestion_fr" uses the analyzer "keyword". Suggestion fields need "standard.lucene" or a language analyzer. Set Analyzers:SuggestionAnalyzer to "standard.lucene".` |
| The fields fit the limit, copies included | `The index needs 56 Azure fields, but the service allows 50 (UmbracoAzureSearch:MaxFieldsPerIndex). ...` |

See [Field budget](index-definition.md#field-budget) for how fields are counted.

## Gotchas

- A change of mode, culture or analyzer needs a rebuild. Azure cannot change the analyzer of an existing field, and existing documents have no text in new copies.
- Each mapped culture adds about 5 fields: 4 aggregate copies and 1 suggestion copy. Each `.PerCulture()` field adds 4 more per mapped culture. Watch the field limit on older Basic services (100 fields).
- Use Azure analyzer names, not culture names: `pt-Br.microsoft`, not `pt-BR`. See the [list of language analyzers](https://learn.microsoft.com/azure/search/index-add-language-analyzers#supported-language-analyzers).
- A single-word query gets a `*` suffix for prefix matching. Azure does not run the language analyzer on prefix terms, so a one-word query matches the start of indexed tokens, not word forms.
