# Azure AI Search limitations

## When to use this

Read this list when a query result differs from the Umbraco Search documentation. These Azure AI Search limits change how some features behave with this provider. The list has no settings.

Needs a rebuild: no.

## Limitations

- **No guaranteed ordering without an explicit sort** — always provide a sorter when result order matters.
- **No multiple facet types on the same field** — e.g. `IntegerExactFacet` and `IntegerRangeFacet` cannot both be applied to the same field in a single query. The provider works around this internally by running a separate query per field, so mixed facet types across different fields work fine.
- **Text filters do not narrow suggestions** — Azure rejects them in suggestion requests, so suggestions ignore `TextFilter`. Suggestions still respect the culture, segment, protected-content and all other filters.
