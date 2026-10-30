using System.Collections.Concurrent;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>
/// Tells the writes of a rebuild from other writes. Umbraco Search rebuilds an index in one async flow on its background
/// queue: <c>IIndexer.ResetAsync</c>, then <c>AddOrUpdateAsync</c> and <c>DeleteAsync</c> for each item, then the
/// <c>IndexRebuildCompletedNotification</c>. <see cref="Begin"/> runs in <c>ResetAsync</c> outside an async method, so the
/// <see cref="AsyncLocal{T}"/> it sets stays in the flow of the caller, and only the writes of that flow carry it.
/// A publish runs in its own queue item, and site code that calls the indexer runs in its own flow, so neither carries it.
/// The value ends with the rebuild method of Umbraco Search, and <see cref="End"/> ends the rebuild for every flow.
/// </summary>
internal sealed class RebuildWrites
{
    private static readonly AsyncLocal<RebuildFlow?> Flow = new();

    private readonly ConcurrentDictionary<string, RebuildFlow> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts the rebuild of an index in the current flow. Call it outside an async method.</summary>
    public RebuildFlow Begin(string indexAlias)
    {
        var flow = new RebuildFlow(indexAlias);
        _active[indexAlias] = flow;
        Flow.Value = flow;
        return flow;
    }

    /// <summary>The rebuild a write to the index belongs to, or null when the write is not a rebuild write.</summary>
    public RebuildFlow? Current(string indexAlias)
        => Flow.Value is { } flow
           && string.Equals(flow.IndexAlias, indexAlias, StringComparison.OrdinalIgnoreCase)
           && _active.TryGetValue(indexAlias, out var active)
           && ReferenceEquals(active, flow)
            ? flow
            : null;

    /// <summary>Ends the rebuild of an index and returns it, or null when none runs.</summary>
    public RebuildFlow? End(string indexAlias) => _active.TryRemove(indexAlias, out var flow) ? flow : null;
}

/// <summary>One rebuild of an index.</summary>
internal sealed class RebuildFlow(string indexAlias)
{
    public string IndexAlias { get; } = indexAlias;
}
