using System.Collections.Concurrent;
using Azure;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Services.Factory;

namespace UmbracoAzureSearch.Services.IndexManager;

/// <summary>
/// Every schema change of an Azure index goes through here. Changes of one index run one at a time in this process, each
/// on a fresh read of the schema. Azure rejects an update while another update of the index runs (409), also from another
/// server, so a 409 reads the schema again and retries. The field names of each index stay in memory, so a write whose
/// fields the index already has does not read the schema.
/// </summary>
internal sealed class IndexSchemaUpdates(IAzureSearchClientFactory azureSearchClientFactory, ILogger<IndexSchemaUpdates> logger)
{
    /// <summary>The attempts of one schema update: the first, and the retries after a 409.</summary>
    public const int Attempts = 5;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _fields = new(StringComparer.Ordinal);

    /// <summary>The wait before retry n (1 based): 0.5, 1, 2 and 4 seconds.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = retry => TimeSpan.FromMilliseconds(500 * Math.Pow(2, retry - 1));

    /// <summary>The field names of an index, or null when this process does not know them.</summary>
    public IReadOnlySet<string>? KnownFields(string indexName) => _fields.GetValueOrDefault(indexName);

    /// <summary>Drops the field names of an index: it was deleted, created, or an update of it failed.</summary>
    public void Forget(string indexName) => _fields.TryRemove(indexName, out _);

    /// <summary>Creates an index and keeps its field names.</summary>
    public async Task CreateAsync(SearchIndex index)
    {
        await LockedAsync(index.Name, async () =>
        {
            Forget(index.Name);
            await azureSearchClientFactory.GetSearchIndexClient().CreateIndexAsync(index);
            return Remember(index);
        });
    }

    /// <summary>
    /// Reads an index, lets <paramref name="change"/> change it, and updates the index when it returns true. A 409 runs the
    /// read and the change again, up to <see cref="Attempts"/> times; then the 409 is thrown. Returns the field names of the index.
    /// </summary>
    /// <param name="indexName">The Azure index.</param>
    /// <param name="change">Changes the index it gets and returns true, or returns false when nothing is missing.</param>
    /// <param name="create">Builds the index when it does not exist; without it, a missing index throws the 404.</param>
    /// <param name="skipWhen">Skips the read when the known field names are enough, e.g. when they have every field a write needs.</param>
    public Task<IReadOnlySet<string>> UpdateAsync(
        string indexName,
        Func<SearchIndex, bool> change,
        Func<Task<SearchIndex>>? create = null,
        Func<IReadOnlySet<string>, bool>? skipWhen = null)
        => LockedAsync(indexName, async () =>
        {
            // Another write may have added the fields while this one waited.
            if (skipWhen is not null && KnownFields(indexName) is { } known && skipWhen(known))
                return known;

            var indexClient = azureSearchClientFactory.GetSearchIndexClient();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    SearchIndex index;
                    try
                    {
                        index = await indexClient.GetIndexAsync(indexName);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404 && create is not null)
                    {
                        index = await create();
                        await indexClient.CreateIndexAsync(index);
                        Remember(index);
                    }

                    if (change(index))
                    {
                        // The ETag makes Azure reject the update (412) when the index changed after the read.
                        await indexClient.CreateOrUpdateIndexAsync(index, onlyIfUnchanged: true);
                    }

                    return Remember(index);
                }
                catch (RequestFailedException ex) when (IsConflict(ex) && attempt < Attempts)
                {
                    logger.LogDebug("The schema update of index {IndexName} conflicts with another operation ({Status}). Retry {Retry} reads the schema again",
                        indexName, ex.Status, attempt);
                    Forget(indexName);
                    await Task.Delay(RetryDelay(attempt));
                }
                catch
                {
                    Forget(indexName);
                    throw;
                }
            }
        });

    // 409: a concurrent operation or an index in the 'Updating' state. 412: the index changed after the read.
    // 409 also answers the create of an index that another server just created.
    private static bool IsConflict(RequestFailedException ex) => ex.Status is 409 or 412;

    private IReadOnlySet<string> Remember(SearchIndex index)
        => _fields[index.Name] = index.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    private async Task<T> LockedAsync<T>(string indexName, Func<Task<T>> action)
    {
        var gate = _locks.GetOrAdd(indexName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
