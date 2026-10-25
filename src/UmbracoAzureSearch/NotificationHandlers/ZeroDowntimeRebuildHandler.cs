using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Search.Core.Notifications;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.NotificationHandlers;

/// <summary>
/// Ends a zero-downtime rebuild: moves the alias to the new slot when the rebuild succeeded, or deletes the new slot.
/// Umbraco Search publishes the completed notification also for a cancelled rebuild, so the token decides; a rebuild that
/// throws publishes nothing, and the next rebuild or the next startup cleans up.
/// </summary>
internal sealed class ZeroDowntimeRebuildHandler(
    IndexSlots slots,
    IAzureSearchClientFactory azureSearchClientFactory,
    IOptionsMonitor<AzureSearchIndexOptions> azureSearchIndexOptions,
    ILogger<ZeroDowntimeRebuildHandler> logger)
    : INotificationAsyncHandler<IndexRebuildCompletedNotification>
{
    public async Task HandleAsync(IndexRebuildCompletedNotification notification, CancellationToken cancellationToken)
    {
        var indexAlias = notification.IndexAlias;
        var rebuilds = slots.Take(indexAlias);
        if (rebuilds.Length == 0)
            return;

        if (cancellationToken.IsCancellationRequested)
        {
            await SkipAsync(indexAlias, rebuilds, "the rebuild was cancelled", []);
            return;
        }

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
            // Before the first swap the name is a plain index. The plain index goes first, so the swap does not rely on
            // Azure accepting an alias with the name of an index; searches fail between the delete and the alias.
            if (rebuild.OldIsPlainIndex)
            {
                await indexClient.DeleteIndexAsync(rebuild.Name, CancellationToken.None);
            }

            await indexClient.CreateOrUpdateAliasAsync(new SearchAlias(rebuild.Name, rebuild.New), cancellationToken: CancellationToken.None);
            slots.Forget(rebuild.Name);
            logger.LogInformation(
                "Swap done for {IndexAlias}: {Name} moved from {OldIndex} to {NewIndex}. Documents {OldDocumentCount} before, {NewDocumentCount} after, rebuild took {Duration}",
                indexAlias, rebuild.Name, rebuild.Old ?? "(none)", rebuild.New, oldCount, newCount, DateTimeOffset.UtcNow - rebuild.Started);
        }

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

    private async Task DeleteOldAsync(string indexAlias, string[] oldIndexes)
    {
        foreach (var oldIndex in oldIndexes)
        {
            try
            {
                await azureSearchClientFactory.GetSearchIndexClient().DeleteIndexAsync(oldIndex, CancellationToken.None);
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
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete the new index {NewIndex} of {IndexAlias}. The next rebuild or startup deletes it", rebuild.New, indexAlias);
            }
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
