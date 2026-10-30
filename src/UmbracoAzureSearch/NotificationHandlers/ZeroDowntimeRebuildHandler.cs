using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Search.Core.Notifications;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Notifications;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.NotificationHandlers;

/// <summary>
/// Ends a rebuild: sends the rest of a batched rebuild. For a zero-downtime rebuild it then moves the alias to the new slot
/// when the rebuild succeeded, or deletes the new slot.
/// Umbraco Search publishes the completed notification also for a cancelled rebuild, so the token decides; a rebuild that
/// throws publishes nothing, and the next rebuild or the next startup cleans up.
/// </summary>
internal sealed class ZeroDowntimeRebuildHandler(
    IndexSlots slots,
    IndexSchemaUpdates schemaUpdates,
    RebuildWrites rebuildWrites,
    DocumentWriter documentWriter,
    IAzureSearchClientFactory azureSearchClientFactory,
    IOptionsMonitor<AzureSearchIndexOptions> azureSearchIndexOptions,
    IEventAggregator eventAggregator,
    ILogger<ZeroDowntimeRebuildHandler> logger)
    : INotificationAsyncHandler<IndexRebuildCompletedNotification>
{
    public async Task HandleAsync(IndexRebuildCompletedNotification notification, CancellationToken cancellationToken)
    {
        var indexAlias = notification.IndexAlias;
        var batch = rebuildWrites.Active(indexAlias)?.Batch;
        // The rebuild stays in progress until the rest of the batch is written and counted, so a publish in that time
        // still goes to both slots.
        var rebuilds = slots.Running(indexAlias);
        if (rebuilds.Length == 0)
        {
            // An index without zero-downtime rebuild: the index is live, so the rest of the batch goes also after a cancel.
            await EndAsync(indexAlias, batch, cancellationToken.IsCancellationRequested);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // No swap happens, so the documents that wait in the batch are dropped with the new slot.
            var dropped = await DocumentWriter.DropAsync(batch);
            slots.Take(indexAlias);
            rebuildWrites.End(indexAlias);
            await SkipAsync(indexAlias, rebuilds, dropped > 0 ? $"the rebuild was cancelled ({dropped} batched documents not sent)" : "the rebuild was cancelled", []);
            return;
        }

        // The rest of the batch goes before the count check. Azure counts a document about a second after the write, so
        // the count waits for the last batch.
        try
        {
            await documentWriter.FlushAsync(batch);
            await Task.WhenAll(rebuilds.Select(rebuild => documentWriter.WaitUntilSearchableAsync(batch, rebuild.New, CountAsync)));
        }
        catch (Exception exception)
        {
            slots.Take(indexAlias);
            rebuildWrites.End(indexAlias);
            await SkipAsync(indexAlias, rebuilds, $"the last batch failed: {exception.Message}", []);
            return;
        }

        // Writes from now on are not rebuild writes, and go to the live index only.
        slots.Take(indexAlias);
        rebuildWrites.End(indexAlias);

        var options = azureSearchIndexOptions.Get(indexAlias);
        (SlotRebuild Rebuild, long OldCount, long NewCount)[] counts;
        try
        {
            counts = await Task.WhenAll(rebuilds.Select(async rebuild => (
                rebuild,
                rebuild.Old is null ? 0 : await CountAsync(rebuild.Old),
                await CountAsync(rebuild.New))));
        }
        catch (Exception exception)
        {
            await SkipAsync(indexAlias, rebuilds, $"the document count failed: {exception.Message}", []);
            return;
        }

        // All indexes of an alias swap together, or none does.
        var percent = options.RebuildMinimumDocumentPercent;
        if (percent > 0 && counts.Any(c => c.OldCount > 0 && c.NewCount * 100 < c.OldCount * percent))
        {
            await SkipAsync(indexAlias, rebuilds, $"the count check failed: a new index has fewer than {percent}% of the old index's documents", counts);
            return;
        }

        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        foreach (var (rebuild, oldCount, newCount) in counts)
        {
            // The new index has only the profiles the sources return; profiles added another way stay behind.
            var dropped = rebuild.Old is null ? [] : await DroppedProfilesAsync(rebuild.Old, rebuild.New);

            // Before the first swap the name is a plain index. The plain index goes first, so the swap does not rely on
            // Azure accepting an alias with the name of an index; searches fail between the delete and the alias.
            if (rebuild.OldIsPlainIndex)
            {
                await indexClient.DeleteIndexAsync(rebuild.Name, CancellationToken.None);
                schemaUpdates.Forget(rebuild.Name);
            }

            await indexClient.CreateOrUpdateAliasAsync(new SearchAlias(rebuild.Name, rebuild.New), cancellationToken: CancellationToken.None);
            slots.Forget(rebuild.Name);
            schemaUpdates.Forget(rebuild.New);
            logger.LogInformation(
                "Swap done for {IndexAlias}: {Name} moved from {OldIndex} to {NewIndex}. Documents {OldDocumentCount} before, {NewDocumentCount} after, rebuild took {Duration}. Dropped scoring profiles: {DroppedScoringProfiles}",
                indexAlias, rebuild.Name, rebuild.Old ?? "(none)", rebuild.New, oldCount, newCount, DateTimeOffset.UtcNow - rebuild.Started,
                dropped.Length == 0 ? "none" : string.Join(", ", dropped));
        }

        var swaps = rebuilds.Select(r => new AzureIndexSwap(r.Name, r.Old, r.New)).ToArray();
        await PublishSwappedAsync(indexAlias, swaps);

        var oldIndexes = rebuilds.Where(r => r is { Old: not null, OldIsPlainIndex: false }).Select(r => r.Old!).ToArray();
        if (oldIndexes.Length == 0)
            return;

        // Searches that started on the old index finish before it goes.
        var delay = TimeSpan.FromSeconds(options.RebuildDeleteDelaySeconds);
        if (delay == TimeSpan.Zero)
        {
            await DeleteOldAsync(indexAlias, oldIndexes);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            await DeleteOldAsync(indexAlias, oldIndexes);
        });
    }

    // Ends the rebuild of an index without zero-downtime rebuild: the rest of the batch goes to the live index.
    private async Task EndAsync(string indexAlias, RebuildBatch? batch, bool cancelled)
    {
        try
        {
            await documentWriter.FlushAsync(batch);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The last batch of the rebuild of {IndexAlias} failed{Cancelled}. Rebuild the index again", indexAlias, cancelled ? " after a cancel" : string.Empty);
        }
        finally
        {
            // Writes from now on are not rebuild writes.
            rebuildWrites.End(indexAlias);
        }
    }

    // The swap is done, so a failing handler must not stop the delete of the old indexes.
    // The swap is committed, so the handlers get no cancellation, as the alias update.
    private async Task PublishSwappedAsync(string indexAlias, AzureIndexSwap[] swaps)
    {
        try
        {
            await eventAggregator.PublishAsync(new IndexSwappedNotification(indexAlias, swaps), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A handler of IndexSwappedNotification failed for {IndexAlias}. The swap is done", indexAlias);
        }
    }

    private async Task DeleteOldAsync(string indexAlias, string[] oldIndexes)
    {
        foreach (var oldIndex in oldIndexes)
        {
            try
            {
                await azureSearchClientFactory.GetSearchIndexClient().DeleteIndexAsync(oldIndex, CancellationToken.None);
                schemaUpdates.Forget(oldIndex);
                logger.LogInformation("Old index deleted for {IndexAlias}: {OldIndex}", indexAlias, oldIndex);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete the old index {OldIndex} of {IndexAlias}. The next rebuild or startup deletes it", oldIndex, indexAlias);
            }
        }
    }

    // The alias stays on the old index, and the new slot goes.
    private async Task SkipAsync(string indexAlias, SlotRebuild[] rebuilds, string reason, (SlotRebuild Rebuild, long OldCount, long NewCount)[] counts)
    {
        logger.LogWarning(
            "Swap skipped for {IndexAlias}: {Reason}. Searches keep using the old index. Documents (old/new): {DocumentCounts}",
            indexAlias, reason, counts.Length == 0 ? "not counted" : string.Join(", ", counts.Select(c => $"{c.Rebuild.Name} {c.OldCount}/{c.NewCount}")));

        foreach (var rebuild in rebuilds)
        {
            try
            {
                await azureSearchClientFactory.GetSearchIndexClient().DeleteIndexAsync(rebuild.New, CancellationToken.None);
                schemaUpdates.Forget(rebuild.New);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete the new index {NewIndex} of {IndexAlias}. The next rebuild or startup deletes it", rebuild.New, indexAlias);
            }
        }
    }

    private async Task<string[]> DroppedProfilesAsync(string oldIndex, string newIndex)
    {
        try
        {
            var indexClient = azureSearchClientFactory.GetSearchIndexClient();
            SearchIndex old = await indexClient.GetIndexAsync(oldIndex);
            SearchIndex @new = await indexClient.GetIndexAsync(newIndex);
            return old.ScoringProfiles.Select(p => p.Name).Except(@new.ScoringProfiles.Select(p => p.Name)).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not compare the scoring profiles of {OldIndex} and {NewIndex}", oldIndex, newIndex);
            return [];
        }
    }

    // A search count is current within about a second; index statistics can lag for minutes.
    private async Task<long> CountAsync(string indexName)
    {
        var response = await azureSearchClientFactory.GetSearchIndexClient().GetSearchClient(indexName)
            .SearchAsync<SearchDocument>("*", new SearchOptions { Size = 0, IncludeTotalCount = true });
        return response.Value.TotalCount ?? 0;
    }
}
