# Zero-downtime rebuild

## When to use this

Use zero-downtime rebuild when searches must keep working while an index rebuilds. Without it, a rebuild deletes the index first and fills it again, so searches fail or return part of the results until the rebuild ends.

Defaults:

| Setting | Default |
|---|---|
| `ZeroDowntimeRebuild` | `false`: a rebuild deletes and refills the index, as before |
| `RebuildMinimumDocumentPercent` | `50` |
| `RebuildDeleteDelaySeconds` | `10` |
| `ClearDocumentCacheOnRebuild` | `false` |
| `RebuildBatchSize` | `1`: one item per request, as before. See [Batch size](#batch-size). |

Needs a rebuild: turning the option on takes effect at the next rebuild. See [First migration](#first-migration).

appsettings:

```json
{
  "UmbracoAzureSearch": {
    "Indexes": {
      "Umb_PublishedContent": {
        "ZeroDowntimeRebuild": true,
        "RebuildMinimumDocumentPercent": 50,
        "RebuildDeleteDelaySeconds": 10,
        "RebuildBatchSize": 500,
        "ClearDocumentCacheOnRebuild": true
      }
    }
  }
}
```

Code:

```csharp
builder.Services.ConfigureAzureSearchIndex("siteSearch", index => index
    .ZeroDowntimeRebuild(r => r
        .MinimumDocumentPercent(50)
        .DeleteDelay(TimeSpan.FromSeconds(10))
        .BatchSize(500))
    .ClearDocumentCacheOnRebuild());
```

When appsettings and code set the same value, code wins.

## How it works

| Step | What happens |
|---|---|
| Names | The index name (for example `umb-publishedcontent`) is an Azure index alias. Two Azure indexes sit behind it: `<name>-a` and `<name>-b`. Searches use the alias. |
| Start | The rebuild creates the slot the alias does not point to, from the current definition and the [scoring profile sources](scoring-profiles.md#sources-and-merge-order). Profiles added another way are not copied, and the swap log lists them as dropped. |
| During the rebuild | Searches use the alias, so they find the old index. The writes of the rebuild go to the new index only. Other writes go to both indexes. See [Writes during a rebuild](#writes-during-a-rebuild). |
| Swap | When the rebuild completes, the package checks that it was not cancelled and that the count check passes. Then it moves the alias to the new index, publishes [`IndexSwappedNotification`](#swap-notification), waits, and deletes the old index. |
| Failure | When the rebuild is cancelled, or the count check fails, the alias stays on the old index and the package deletes the new one. A rebuild that throws leaves the new index; the next rebuild or the next startup deletes it. |

With [`IndexPerCulture`](analyzers.md#indexperculture), each culture index and the fallback index has its own alias and its own a/b pair, for example `sitesearch-fr` over `sitesearch-fr-a` and `sitesearch-fr-b`. All indexes of the alias rebuild and swap together, or none swaps.

At startup (with `EnsureIndicesOnStartup()`), the package deletes a half-built slot that a crashed rebuild left behind.

## Writes during a rebuild

Umbraco Search runs a rebuild as one background task: it calls `ResetAsync`, then writes each item, in one async flow. The package marks that flow when `ResetAsync` starts, so it knows the writes of the rebuild from other writes. The mark ends when the rebuild completes.

| Write | Goes to | Removed variations |
|---|---|---|
| A write of the rebuild | The new index only | None. The new index starts empty. |
| A delete of the rebuild | Nowhere. The package sends no request. | None. See [Deletes of the rebuild](#deletes-of-the-rebuild). |
| A publish, save or delete during the rebuild | Both indexes | Deleted in both indexes, in the request of the upload |
| A direct call to `IIndexer` or `IAzureSearchIndexer` from your code | Both indexes | Deleted in both indexes, in the request of the upload |

See [Removed variations](index-definition.md#removed-variations) for how the package finds the documents to delete.

### Deletes of the rebuild

During the rebuild, Umbraco Search deletes each item without a published variation, for example an unpublished page. Outside a rebuild, a delete searches the index for the documents of the item and its descendants (`Umb_PathIds`). Then it deletes these documents.

A delete of the rebuild sends no request to an index that the rebuild emptied. This applies to every rebuild:

- A zero-downtime rebuild fills a new, empty index.
- A rebuild without zero-downtime rebuild deletes the index and creates it again.
- A rebuild on a serverless service also deletes the index and creates it again.

An index that the rebuild emptied holds nothing that the search could find:

- Umbraco Search writes no document of an item that it deletes.
- Umbraco Search goes through the content tree by path. It deletes an item before it writes any descendant of that item.
- The index only holds what the rebuild wrote. The batch only holds documents of other items.

Example: a zero-downtime rebuild of 17,895 documents on a staging site sent 2,381 of these searches. Each search found nothing. The rebuild fell from about 2,000 to about 200 documents a minute.

These deletes search and delete as before:

- A delete outside the rebuild flow. During a zero-downtime rebuild it goes to both indexes.
- A delete of the rebuild to an index that the rebuild did not empty. Example: with `IndexPerCulture`, a language added during the rebuild needs the fallback index. The reset did not empty that index.

The package also skips a delete from code inside the rebuild flow, for example a `ContentIndexingNotification` handler. A document that the rebuild already wrote stays in the index. To keep an item out of the rebuild, cancel its `ContentIndexingNotification`.

Your code can also call `IIndexer` in its own flow during the rebuild. If it writes an item, and the rebuild then deletes the same item, the document stays in the index until the next write or delete of the item. Umbraco Search runs content changes after the rebuild. So only such direct calls cause this.

## Batch size

Umbraco Search rebuilds one item at a time, so by default each item is one request to Azure. `RebuildBatchSize` sends the writes of a rebuild in requests of that many documents. Use 1 to 1000; Azure takes at most 1000 documents per request. 1 (the default) sends each item at once. The setting also works without zero-downtime rebuild. In code without zero-downtime rebuild, use `.Rebuild(r => r.BatchSize(500))`.

Example: on a Basic service a zero-downtime rebuild of about 19,000 documents with one item per request ran at 110 to 200 documents a minute.

| Case | What happens |
|---|---|
| The batch of an Azure index is full | The package sends it. The documents of one item stay in one request. |
| The rebuild completes | The package sends the rest, before the count check and the swap. The count check waits up to 10 seconds until Azure counts the documents. |
| The rebuild is cancelled (zero-downtime rebuild) | The package drops the rest with the new index. No swap happens. |
| The rebuild is cancelled (no zero-downtime rebuild) | The package sends the rest. The index is live. |
| The application stops during the rebuild | The rest is lost with the half-built index, which the next startup deletes. |
| A publish or delete during the rebuild | The package first sends the documents that wait, then writes the change at once. A waiting rebuild document of the same item does not overwrite the change. |
| Azure rejects a document in a request | The package logs the document key and Azure's message at Error. The other documents count as written. |
| Azure rejects a whole request (400, 413 request too large, other 4xx) | The package sends the request in two halves, down to single documents. A document that still fails alone is logged at Error and left out. The rebuild goes on. |
| Azure throttles the request (429, 503) | The package retries it whole. See [Busy services](#busy-services). |

The fields of all documents of a batch go into one schema update.

Start with 100 to 500. A request can hold at most 16 MB, so large documents need a smaller batch. The package splits a request that is too large, but each split costs a request.

## Busy services

A Basic service has little capacity. During a rebuild it can answer with 503 ("not enough resources to cover 100% of the index"), 429, or 409 for a schema update.

- Throttled writes (429, 503): the Azure SDK retries 3 times within a few seconds. Then the package sends the request, or the throttled documents of it, again after 2, 4, 8, 16 and 32 seconds. Then the write fails as before. This applies to every write, with and without batches.
- Schema updates: a document with a new field updates the Azure index. The package runs the schema updates of one Azure index one at a time in each process, each on a fresh read of the schema. When Azure answers 409 ("cannot be updated due to a concurrent operation", or "cannot be updated in 'Updating' state"), for example for an update from another server, the package reads the schema again and retries after 0.5, 1, 2 and 4 seconds. Then the update fails as before.
- The package keeps the field names of each Azure index in memory. A write whose fields the index already has reads no schema.
- A write without segments sends its uploads and the deletes of removed variations in one request. It does not search. See [Removed variations](index-definition.md#removed-variations).
- A new index already has the Umbraco system fields and the Delivery API index handler fields, so the first documents of a rebuild do not update the schema. See [Fields of a new index](index-definition.md#fields-of-a-new-index).

## Count check

The package counts the documents of the old and the new index before the swap. When the new index has fewer than `RebuildMinimumDocumentPercent` percent of the old index's documents, it skips the swap and logs the counts. With `IndexPerCulture`, each index is checked, and one failed check stops the swap of all.

Set `RebuildMinimumDocumentPercent` to `0` to turn the check off, for example before a rebuild after you deleted much content.

## Swap notification

After a swap, the package publishes `IndexSwappedNotification` through the Umbraco event aggregator. Searches now read the new index. Use the notification to clear caches that hold search results, for example the output cache.

| Property | Value |
|---|---|
| `IndexAlias` | The Umbraco index alias, for example `Umb_PublishedContent` |
| `Swaps` | The Azure aliases that moved. Each item has `AzureAlias`, `OldIndex` and `NewIndex`. |

The package publishes one notification for each Umbraco index. With `IndexPerCulture`, it publishes the notification after all Azure aliases of the index moved, and `Swaps` has one item for each culture index and the fallback index. `OldIndex` is null when the alias had no index before. At the [first migration](#first-migration), `OldIndex` is the plain index that the alias replaced. The package deleted that index before the handlers run.

After a later swap, the old index still exists when the handlers run. The package deletes it after the handlers.

The package does not publish the notification when the swap is skipped or when an alias update fails. With `IndexPerCulture`, an alias update can fail after other aliases of the index moved. Searches on the moved aliases then read the new indexes, but the package publishes no notification. Run the rebuild again.

When a handler throws, the package logs the error and continues. The swap stays done, and the package deletes the old index.

```csharp
using Microsoft.AspNetCore.OutputCaching;
using Umbraco.Cms.Core.Events;
using UmbracoAzureSearch.Notifications;

public sealed class EvictSearchCacheHandler(IOutputCacheStore outputCacheStore)
    : INotificationAsyncHandler<IndexSwappedNotification>
{
    public async Task HandleAsync(IndexSwappedNotification notification, CancellationToken cancellationToken)
    {
        if (notification.IndexAlias == "siteSearch")
        {
            await outputCacheStore.EvictByTagAsync("search", cancellationToken);
        }
    }
}
```

Register the handler in a composer:

```csharp
builder.AddNotificationAsyncHandler<IndexSwappedNotification, EvictSearchCacheHandler>();
```

## Wait before delete

After the swap, the package waits `RebuildDeleteDelaySeconds` seconds before it deletes the old index, so searches that started on it can finish. The wait runs in the background, so the next queued changes do not wait for it.

## ClearDocumentCacheOnRebuild

Umbraco Search caches the index fields of each content item in the `umbracoIndexDocument` table, and a rebuild reads from that cache. A new field from a property value handler, or a changed per-index indexer, does not reach existing content while the cache holds the old fields.

With `ClearDocumentCacheOnRebuild: true`, a rebuild of the index clears the cache first, so every item is read again from content. The cache is shared by all indexes, so the other indexes also read their items again at their next write. This option works with and without zero-downtime rebuild.

## Cost and limits

- During a rebuild the alias has two indexes, so the index count of the alias doubles. With `IndexPerCulture`: (mapped cultures + 1 if a fallback index exists) × 2.
- Basic allows 15 indexes and 30 aliases. Count before you turn the option on.
- On dedicated tiers you pay per search unit, so the second index costs only storage during the rebuild.

## Serverless

Serverless services do not support index aliases. The option has no effect there: rebuilds work as without it, and the package logs a warning at startup.

## Definition changes at startup

With or without the option, the package compares each existing index with the index it builds from code at startup (with `EnsureIndicesOnStartup()`):

- It applies the changes Azure accepts on a live index: new fields, scoring profiles, the default scoring profile, and those changes from [`ConfigureIndex`](customizing-azure.md).
- It logs "Rebuild required" for the changes that need a rebuild: the analyzer of an existing field, the suggester fields, an attribute of an existing field, fields that automatic fields off or a field opt-in now leaves out, text analysis components (analyzers, tokenizers, filters) that `ConfigureIndex` adds, a new culture under `IndexPerCulture`, and a new Umbraco language outside the culture map.

## Log events

| Event | Level | Message starts with | Properties |
|---|---|---|---|
| Rebuild started | Information | `Rebuild started for` | `IndexAlias`, `NewIndex`, `OldIndex`, `Started` |
| Swap done | Information | `Swap done for` | `IndexAlias`, `Name`, `OldIndex`, `NewIndex`, `OldDocumentCount`, `NewDocumentCount`, `Duration`, `DroppedScoringProfiles` |
| Old index deleted | Information | `Old index deleted for` | `IndexAlias`, `OldIndex` |
| Swap notification handler failed | Error | `A handler of IndexSwappedNotification failed for` | `IndexAlias` |
| Swap skipped | Warning | `Swap skipped for` | `IndexAlias`, `Reason`, `DocumentCounts` |
| Rebuild required | Warning | `Rebuild required for index` | `IndexAlias`, `Change` |
| Half-built index deleted | Warning | `Deleted the half-built index` | `NewIndex`, `IndexAlias` |
| Write throttled | Warning | `Azure AI Search throttled a write to` | `IndexName`, `Reason`, `Retry`, `Retries`, `Delay` |
| Batch rejected and split | Warning | `Azure rejected a batch of` | `DocumentCount`, `IndexName`, `StatusCode` |
| Document not written | Error | `Failed to index document` | `DocumentKey`, `Id`, `IndexAlias`, `StatusCode`, `ErrorMessage` |
| Removed variation not deleted | Error | `Failed to delete document` | `DocumentKey`, `Id`, `IndexAlias`, `StatusCode`, `ErrorMessage` |
| Option has no effect | Warning | `ZeroDowntimeRebuild has no effect` | `IndexAlias` |

The package cannot see the Umbraco Search queue, so the swap event has no count of the changes that waited.

Find failed swaps in Application Insights:

```kusto
traces
| where message startswith "Swap skipped for"
| project timestamp, IndexAlias = tostring(customDimensions.IndexAlias), Reason = tostring(customDimensions.Reason), DocumentCounts = tostring(customDimensions.DocumentCounts)
| order by timestamp desc
```

## First migration

An index created before you turned the option on is a plain index with the name. It stays in use until the first rebuild. The first rebuild fills `<name>-a`. At the swap, the package deletes the plain index and then creates the alias with the same name. It does not rely on Azure accepting an alias with the name of an existing index. Searches fail between the delete and the alias, for about the time of two Azure requests. Run the first rebuild at a quiet time.

The package reads document counts and index statistics from the `-a` and `-b` indexes, not through the alias, so it does not depend on statistics through an alias either.

With `IndexPerCulture`, this happens once for each culture index and the fallback index.

## Gotchas

- Serverless ignores the option.
- The package publishes `IndexSwappedNotification` on the server that ran the rebuild only. On a load-balanced site, clear shared caches from that server, or tell the other servers.
- Editors may report "my change is not in search" during a rebuild. Their change shows when the rebuild ends.
- A rebuild with `RebuildBatchSize` writes the last documents when it completes. A search on the new index during the rebuild can miss them.
- A large content delete makes the count check fail. Lower `RebuildMinimumDocumentPercent`, or set it to `0`, for that rebuild.
- Umbraco Search publishes `IndexRebuildCompletedNotification` also for a cancelled rebuild. Code that listens to it must check the cancellation token.
- The index name is now an alias. Tools that read the index definition, such as the Azure portal's index page, must use `<name>-a` or `<name>-b`. Queries work through the alias.
- Turning the option off: the next rebuild deletes the alias and both slots, and creates a plain index with the name.
