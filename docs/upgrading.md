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

None yet.

## New startup validation

None yet.

## What needs a rebuild

| Change | Rebuild |
|---|---|
| Upgrade from a version before 17.3.0 to get term suggestions | Yes, to fill suggestions for existing content. See [Suggestions](suggestions.md#upgrading). |

## Upgrade order

None yet.

## Version branches

Krebil.UmbracoAzureSearch has one major version per Umbraco major version. Version 17 supports Umbraco 17. Version 18 supports Umbraco 18. Each feature ships in both versions.
