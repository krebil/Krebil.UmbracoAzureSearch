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

Krebil reads the old location in versions 17.4 and 18.3 and logs a warning at startup that names the old key and the new key. Versions 17.5 and 18.4 stop reading it.

When both locations are set for an index, Krebil uses the new location and ignores the old one as a whole. The warning names the ignored old key.

`UmbracoAzureSearchOptions.Suggestions` and `UmbracoAzureSearchOptions.GetSuggestionOptions` are obsolete. Use `ConfigureAzureSearchIndex` to set suggestions in code, and `IOptionsMonitor<AzureSearchIndexOptions>.Get(alias).Suggestions` to read them.

## New startup validation

Krebil checks the [index configuration](index-configuration.md#validation) at startup. A configuration that worked before can now stop startup:

- An alias under `UmbracoAzureSearch:Indexes`, or in `ConfigureAzureSearchIndex`, that Umbraco Search does not register. A typo in an alias is the usual cause. The old `Suggestions` location is not checked.

## What needs a rebuild

| Change | Rebuild |
|---|---|
| Upgrade from a version before 17.3.0 or 18.2.0 to get term suggestions | Yes, to fill suggestions for existing content. See [Suggestions](suggestions.md#upgrading). |
| Move the `Suggestions` settings, same values | No |

## Upgrade order

None yet.

## Version branches

Krebil.UmbracoAzureSearch has one major version per Umbraco major version. Version 17 supports Umbraco 17. Version 18 supports Umbraco 18. Each feature ships in both versions.
