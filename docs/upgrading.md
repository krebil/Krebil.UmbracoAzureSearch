# Upgrading

## When to use this

Read this guide before you update Krebil.UmbracoAzureSearch. It lists what changes for an existing site and which changes need an index rebuild.

## What changes by default

New settings are off or keep today's behavior. A site that changes no configuration keeps its current indexes and results.

| Setting | Default | Behavior without config |
|---|---|---|
| `AutomaticFields` | `true` | Every field is written, as today |
| `Analyzers:Mode` | `None` | Shared fields with `standard.lucene` |
| `SuggestionAnalyzer` | `PerCulture` | No effect while the mode is `None` |
| `ZeroDowntimeRebuild` | `false` | Old rebuild behavior |
| `ClearDocumentCacheOnRebuild` | `false` | A rebuild reuses cached `umbracoIndexDocument` rows, as today |
| Spell check | off | No correction |
| Scoring profiles | none | No `krebil-boosts` while no field has a boost |

These settings arrive in later versions. This table lists the default of each one, so you can see that an upgrade does not change behavior.

## Settings that move

### Suggestions

The suggestion settings move from `UmbracoAzureSearch:Suggestions:<alias>` to `UmbracoAzureSearch:Indexes:<alias>:Suggestions`. The values stay the same. No rebuild is needed.

Before:

```json
{
  "UmbracoAzureSearch": {
    "Suggestions": {
      "Umb_Members": { "Enabled": false }
    }
  }
}
```

After:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "Umb_Members": {
        "Suggestions": { "Enabled": false }
      }
    }
  }
}
```

The package reads the old location in versions 17.4 and 18.3 and logs a warning at startup that names the old key and the new key. Versions 17.5 and 18.4 stop reading it.

When both locations are set for an index, the package uses the new location and ignores the old one as a whole. The warning names the ignored old key.

`UmbracoAzureSearchOptions.Suggestions` and `UmbracoAzureSearchOptions.GetSuggestionOptions` are obsolete. Use `ConfigureAzureSearchIndex` to set suggestions in code, and `IOptionsMonitor<AzureSearchIndexOptions>.Get(alias).Suggestions` to read them.

### Serverless

The package now reads `UmbracoAzureSearch:Serverless`. Before, only `UmbracoAzureSearch:IsServerless` worked, and a `Serverless` key had no effect. Both keys work now. When both are set, `IsServerless` wins. A site that set `Serverless: true` and saw errors on a serverless service now works without a change.

## New startup validation

The package checks the [index configuration](index-configuration.md#validation) at startup. A configuration that worked before can now stop startup:

- An alias under `UmbracoAzureSearch:Indexes`, or in `ConfigureAzureSearchIndex`, that Umbraco Search does not register. A typo in an alias is the usual cause. The old `Suggestions` location is not checked.
- A field that is suggestable but not searchable.
- A field with an analyzer that Azure does not have.
- An index whose fields exceed `UmbracoAzureSearch:MaxFieldsPerIndex`.
- An unknown analyzer name in `Analyzers`, an analyzer that suggestion fields do not allow, and `FieldPerCulture` or `IndexPerCulture` without a mapped culture.
- A spell check query language that the Azure lexicon speller does not support.
- A scoring profile that uses a missing field or a field of the wrong type, a default profile that does not exist, more than 100 profiles, and a boost of 0 or less.
- `RebuildMinimumDocumentPercent` outside 0 to 100, or a negative `RebuildDeleteDelaySeconds`.
- `IndexPerCulture` on a backoffice index, and more Azure indexes than `UmbracoAzureSearch:MaxIndexes` when an index uses `IndexPerCulture`.

## What needs a rebuild

| Change | Rebuild |
|---|---|
| Upgrade from a version before 17.3.0 or 18.2.0 to get term suggestions | Yes, to fill suggestions for existing content. See [Suggestions](suggestions.md#upgrading). |
| Move the `Suggestions` settings, same values | No |
| Opt in a new field | No |
| Turn automatic fields off on an existing index | Yes. Azure cannot remove fields. |
| Change searchable, filterable, facetable, sortable or the analyzer of an existing field | Yes |
| Change the content types or cultures of an index | Yes |
| Add a per-index indexer | Yes, to fill existing content |
| Add fields or scoring profiles with `ConfigureIndex` | No, applied at startup |
| Turn on spell check for a culture | No |
| Add, change or remove scoring profiles, boosts or the default profile | No |
| Turn on `ZeroDowntimeRebuild` | First migration at the next rebuild. See [First migration](zero-downtime-rebuild.md#first-migration). |
| Add analyzers or change existing fields with `ConfigureIndex` | Yes |
| Change `Analyzers:Mode`, add a culture, change an analyzer | Yes |
| Add an Umbraco language outside the culture map of an `IndexPerCulture` index | Yes, to fill the new fallback index |
| Change `SuggestionAnalyzer` or suggestion fields | Yes |

## Rebuild required at startup

With `EnsureIndicesOnStartup()`, the package logs a "Rebuild required" warning at startup for each change that needs a rebuild. See [Definition changes at startup](zero-downtime-rebuild.md#definition-changes-at-startup) for the list. Changes that need no rebuild apply at startup.

## Zero-downtime rebuild

Turning on `ZeroDowntimeRebuild` takes effect at the next rebuild. The first rebuild replaces the plain index with an alias, with a short gap in searches. See [First migration](zero-downtime-rebuild.md#first-migration).

## Upgrade order

None yet.

## Version branches

Krebil.UmbracoAzureSearch has one major version per Umbraco major version. Version 17 supports Umbraco 17. Version 18 supports Umbraco 18. Each feature ships in both versions.
