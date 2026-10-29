using System.Collections.Concurrent;

namespace UmbracoAzureSearch.Services.Searcher;

/// <summary>
/// The Azure indexes whose service rejected the lexicon speller, for example a Free tier service. Searches on them run
/// without spell check until the application restarts.
/// </summary>
internal sealed class SpellerState
{
    private readonly ConcurrentDictionary<string, bool> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRejected(string indexName) => _rejected.ContainsKey(indexName);

    /// <summary>Marks the index; returns true the first time, so the caller logs once.</summary>
    public bool Reject(string indexName) => _rejected.TryAdd(indexName, true);
}
