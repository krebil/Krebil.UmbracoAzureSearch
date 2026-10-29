# Scoring profiles

## When to use this

Use scoring profiles to rank results by more than text relevance: a match in the title above a match in the body, newer pages first, or highly rated items first. A scoring profile is part of the Azure index. It weights searchable fields, adds functions on dates, numbers and tags, or both. A query uses one profile: the one it names, or the default profile of the index.

Defaults:

| Setting | Default |
|---|---|
| Field `Boost` | None |
| `DefaultScoringProfile` | `umbraco-boosts` when a field has a boost, otherwise `relevanceBoost` |
| Providers | None |

Needs a rebuild: no. Azure adds, updates and removes scoring profiles on a live index.

```csharp
public sealed class SiteSearchComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
            .Field("title", f => f.Searchable().Boost(3))
            .Field("date", f => f.Filterable().Sortable())
            .DefaultScoringProfile("recency"));

        builder.Services.AddScoringProfileProvider<SiteSearchScoringProfileProvider>();
    }
}
```

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "siteSearch": {
        "DefaultScoringProfile": "recency",
        "Fields": {
          "title": { "Searchable": true, "Boost": 3 }
        }
      }
    }
  }
}
```

When appsettings and code set the same value, code wins.

## Sources and merge order

The package builds the profiles of an index from these sources, in this order. When two sources give a profile with the same name, the later source wins.

1. `relevanceBoost`: the package's relevance weights (R1 4, R2 3, R3 2, all texts 1).
2. `umbraco-boosts`: generated from field boosts.
3. The profiles of the scoring profile providers, in registration order.
4. The [`ConfigureIndex`](customizing-azure.md) callback.

## Providers

A provider gives profiles per index alias. Several providers are allowed.

```csharp
public sealed class SiteSearchScoringProfileProvider : IScoringProfileProvider
{
    public Task<IEnumerable<ScoringProfile>> GetScoringProfilesAsync(string indexAlias, CancellationToken cancellationToken)
    {
        if (indexAlias != "siteSearch")
        {
            return Task.FromResult(Enumerable.Empty<ScoringProfile>());
        }

        var recency = new ScoringProfile("recency")
        {
            Functions =
            {
                new FreshnessScoringFunction("date_datetimeoffsets_sort", 2, new FreshnessScoringParameters(TimeSpan.FromDays(30)))
            }
        };

        return Task.FromResult<IEnumerable<ScoringProfile>>([recency]);
    }
}
```

The package calls the providers:

- when it creates an index (at startup with `EnsureIndicesOnStartup()`, and on a rebuild),
- at startup, to update existing indexes and to validate the profiles,
- when a zero-downtime rebuild creates the new index,
- when you call `RefreshScoringProfilesAsync`.

### Insert and update only

The package inserts new profiles and replaces a profile with the same name. It never removes a provider's profile on its own. When you stop returning a profile, or remove a provider, the profile stays in Azure. Remove it with `RemoveScoringProfileAsync`:

```csharp
await indexManager.RemoveScoringProfileAsync("siteSearch", "recency");
```

`RemoveScoringProfileAsync` removes the profile from every Azure index behind the alias. It throws for `umbraco-boosts`, `relevanceBoost` and the current default profile. Inject `IAzureSearchIndexManager` to call it.

### Refresh at runtime

For profiles that change while the site runs, call the providers again without a restart or a rebuild:

```csharp
await indexManager.RefreshScoringProfilesAsync("siteSearch");
```

A refresh validates the profiles as startup does, and throws when one does not fit the index.

## umbraco-boosts

The package generates `umbraco-boosts` from the `Boost` of each opted-in searchable field. It has the relevance weights of `relevanceBoost`, and the boost of each field on the field's four text fields (`title_texts`, `title_texts_r1` to `title_texts_r3`). With [`FieldPerCulture`](analyzers.md#fieldperculture) and `.PerCulture()`, the per-culture copies of the field get the boost too.

The package updates `umbraco-boosts` when boosts change, and removes it when no field has a boost. You cannot remove it yourself, and you should not name your own profile `umbraco-boosts`.

With `FieldPerCulture`, a query in a mapped culture searches the per-culture copies. A boost on a field without `.PerCulture()` has no effect on those queries. Use `.PerCulture()` on boosted fields.

## Default profile

`DefaultScoringProfile` sets the profile a query uses when it names none. Unset, the default is `umbraco-boosts` when a field has a boost, and `relevanceBoost` otherwise.

## Per query

`SearchAsync` takes an optional profile and its parameters:

```csharp
var result = await searcher.SearchAsync(
    indexAlias: "siteSearch",
    query: query,
    culture: "fr",
    scoringProfile: "recency");
```

`scoringParameters` feeds functions that take parameters, such as a tag function: `scoringParameters: ["tags-news,sport"]`. `ConfigureSearch` can also set `ScoringProfile` and `ScoringParameters`.

## Azure indexes

The package applies the profiles to every Azure index behind the alias: both slots of a [zero-downtime rebuild](zero-downtime-rebuild.md), each culture index and the fallback index of [`IndexPerCulture`](analyzers.md#indexperculture).

## Field names

Profiles use Azure field names, not Umbraco field names. The name says the value type:

| Umbraco field value | Azure field | Use in a profile |
|---|---|---|
| Texts | `<field>_texts`, `<field>_texts_r1` to `_r3` | Text weights |
| Keywords | `<field>_keywords` (collection) | Tag function |
| Integers | `<field>_integers_sort` (single value) | Magnitude function |
| Decimals | `<field>_decimals_sort` (single value) | Magnitude function |
| Dates | `<field>_datetimeoffsets_sort` (single value) | Freshness function |

When a profile names a field in this form that the index does not have yet, the package creates it with the right type, and filterable when a function uses it. A new index can then take the profile before any document has the field. With `FieldPerCulture`, use the per-culture names from the [naming rule](analyzers.md#physical-names).

## Leftover profiles

Profiles on an index that no source returns, for example one added in the Azure portal, are logged at startup (Information). The package does not remove them. A zero-downtime rebuild builds the new index from the sources only, so it drops them, and the swap log lists the dropped profiles.

## Validation

At startup (and at a refresh, where it fails the refresh):

| Check | Example error |
|---|---|
| A field the profile uses exists | `the scoring profile recency uses the field lastUpdated, which the index does not have` |
| A text weight uses a searchable field | `... uses the field date_datetimeoffsets_sort (Edm.DateTimeOffset), but a text weight needs a searchable field` |
| A freshness function uses a single date field | `... uses the field title_texts (Collection(Edm.String)), but a freshness function needs a single date field (Edm.DateTimeOffset)` |
| A magnitude function uses a single number field | `... but a magnitude function needs a single number field (Edm.Int32, Edm.Int64 or Edm.Double)` |
| A tag function uses a string field | `... but a tag function needs an Edm.String or Collection(Edm.String) field` |
| A function uses a filterable field | `... but a scoring function needs a filterable field` |
| The default profile exists | `the default scoring profile "recency" is not a profile of the index.` |
| The index has at most 100 profiles | `the index has 101 scoring profiles, and Azure allows 100.` |
| A boost is greater than 0 | `The field "title" has the boost 0. A boost must be greater than 0.` |

The errors start with `Scoring profiles of "<alias>":`. With a `ConfigureIndex` callback, the startup check does not report missing fields or a missing default profile, because the callback can add them. Azure reports those when the package applies the profile.

On an existing index, a field that a function needs can exist but not be filterable, because a document created it before the profile. The package then skips that profile and logs "Rebuild required". A rebuild creates the field filterable.

## Gotchas

- Removing a provider from code does not remove its profiles from Azure. Call `RemoveScoringProfileAsync`.
- A rebuild drops profiles added by hand in the Azure portal. Add them with a provider or `ConfigureIndex`.
- A freshness function needs a single date field that is filterable. Use `<field>_datetimeoffsets_sort`. With automatic fields off, opt in the field.
- Do not name a profile `umbraco-boosts`. A provider or callback with that name replaces the package's profile.
- Azure allows 100 scoring profiles per index.
