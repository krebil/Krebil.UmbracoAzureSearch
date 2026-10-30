using System.Collections.Concurrent;
using Azure;
using Microsoft.Extensions.Options;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Services.IndexManager;

/// <summary>One Azure index name behind which a rebuild fills a new slot.</summary>
/// <param name="Name">The name searches use: an Azure alias, or a plain index before the first rebuild.</param>
/// <param name="Old">The index the name pointed to when the rebuild started; null when nothing existed.</param>
/// <param name="New">The slot index the rebuild fills.</param>
/// <param name="OldIsPlainIndex">True when the old index is a plain index with the name itself (before the first swap).</param>
/// <param name="Started">When the rebuild started.</param>
internal sealed record SlotRebuild(string Name, string? Old, string New, bool OldIsPlainIndex, DateTimeOffset Started);

/// <summary>
/// Zero-downtime rebuild: an index name is an Azure alias over two slot indexes, <c>&lt;name&gt;-a</c> and
/// <c>&lt;name&gt;-b</c>. A rebuild fills the slot the alias does not point to, and the swap moves the alias.
/// The rebuild state lives in memory: the server that runs the rebuild also handles its completion.
/// </summary>
internal sealed class IndexSlots(IAzureSearchClientFactory azureSearchClientFactory, IOptions<UmbracoAzureSearchOptions> azureSearchOptions)
{
    private static readonly TimeSpan LiveCacheAge = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SlotRebuild[]> _rebuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Read, string Live)> _live = new();

    /// <summary>True when the index uses zero-downtime rebuild. Serverless services have no aliases, so never there.</summary>
    public bool Enabled(IndexDefinition definition)
        => definition.Options.ZeroDowntimeRebuild && !azureSearchOptions.Value.IsServerless;

    public static string Slot(string name, char slot) => $"{name}-{slot}";

    /// <summary>The slot a rebuild fills: the one the live index is not.</summary>
    public static string Inactive(string name, string? live) => live == Slot(name, 'a') ? Slot(name, 'b') : Slot(name, 'a');

    /// <summary>
    /// The Azure index behind a name: the index its alias points to, or the name itself when it has no alias (a plain index,
    /// or no index yet). Index definitions and statistics need the index, not the alias.
    /// </summary>
    public async Task<string> LiveAsync(string name, IndexDefinition definition)
    {
        if (!Enabled(definition))
            return name;

        if (_live.TryGetValue(name, out var cached) && DateTimeOffset.UtcNow - cached.Read < LiveCacheAge)
            return cached.Live;

        var live = await AliasTargetAsync(name) ?? name;
        _live[name] = (DateTimeOffset.UtcNow, live);
        return live;
    }

    /// <summary>The index an alias points to, or null when the alias does not exist.</summary>
    public async Task<string?> AliasTargetAsync(string name)
    {
        try
        {
            var alias = await azureSearchClientFactory.GetSearchIndexClient().GetAliasAsync(name);
            return alias.Value.Indexes?.FirstOrDefault();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Drops the cached live index of a name, after the alias moved.</summary>
    public void Forget(string name) => _live.TryRemove(name, out _);

    /// <summary>
    /// The Azure indexes a write goes to. During a rebuild: the old index and the new slot, so the old index stays current
    /// until the swap and the new one misses nothing. Otherwise the live index.
    /// </summary>
    public async Task<string[]> WriteTargetsAsync(string name, IndexDefinition definition)
    {
        if (Enabled(definition) && _rebuilds.Values.SelectMany(r => r).FirstOrDefault(r => r.Name == name) is { } rebuild)
            return rebuild.Old is null ? [rebuild.New] : [rebuild.Old, rebuild.New];

        return [await LiveAsync(name, definition)];
    }

    /// <summary>The new slot of the rebuild in progress for a name, or null when no zero-downtime rebuild runs.</summary>
    public string? NewSlot(string name, IndexDefinition definition)
        => Enabled(definition) && _rebuilds.Values.SelectMany(r => r).FirstOrDefault(r => r.Name == name) is { } rebuild ? rebuild.New : null;

    public void Start(string indexAlias, SlotRebuild[] rebuilds) => _rebuilds[indexAlias] = rebuilds;

    /// <summary>The slots of the rebuild of an alias, or an empty array when none runs. The rebuild goes on.</summary>
    public SlotRebuild[] Running(string indexAlias) => _rebuilds.TryGetValue(indexAlias, out var rebuilds) ? rebuilds : [];

    /// <summary>Ends the rebuild of an alias and returns its slots, or an empty array when none runs.</summary>
    public SlotRebuild[] Take(string indexAlias) => _rebuilds.TryRemove(indexAlias, out var rebuilds) ? rebuilds : [];
}
