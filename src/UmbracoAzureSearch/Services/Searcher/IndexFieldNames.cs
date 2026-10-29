using System.Collections.Concurrent;
using UmbracoAzureSearch.Services.Factory;

namespace UmbracoAzureSearch.Services.Searcher;

/// <summary>
/// The field names of each Azure index, read from Azure and kept for a short time. The indexer adds fields as documents
/// bring them, so the names are read again after <see cref="MaxAge"/>.
/// </summary>
internal sealed class IndexFieldNames(IAzureSearchClientFactory azureSearchClientFactory)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Read, HashSet<string> Names)> _names = new();

    public async Task<HashSet<string>> GetAsync(string indexName)
    {
        if (_names.TryGetValue(indexName, out var cached) && DateTimeOffset.UtcNow - cached.Read < MaxAge)
            return cached.Names;

        var index = await azureSearchClientFactory.GetSearchIndexClient().GetIndexAsync(indexName);
        var names = index.Value.Fields.Select(f => f.Name).ToHashSet();
        _names[indexName] = (DateTimeOffset.UtcNow, names);
        return names;
    }
}
