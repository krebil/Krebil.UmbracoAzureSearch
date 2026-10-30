using System.Collections.Concurrent;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Scoring;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>
/// Writes documents to Azure indexes: adds the fields they need, uploads them, retries throttled writes, and collects the
/// writes of a rebuild in batches when the index has a <c>RebuildBatchSize</c>.
/// </summary>
/// <remarks>
/// A service that is short of resources answers 503 or 429, for the whole request or for single documents. The Azure SDK
/// retries a throttled request 3 times within a few seconds; after that, the request or the throttled documents are sent
/// again after 2, 4, 8, 16 and 32 seconds. Then the last answer counts.
/// </remarks>
internal sealed class DocumentWriter(
    IAzureSearchClientFactory azureSearchClientFactory,
    IndexSchemaUpdates schemaUpdates,
    IServiceProvider serviceProvider,
    ILogger<DocumentWriter> logger)
{
    /// <summary>The attempts of one send: the first, and the retries after throttling.</summary>
    public const int Attempts = 6;

    // Index names already warned about missing suggestion copies: one warning per index.
    private readonly ConcurrentDictionary<string, bool> _missingSuggestionCopyWarnings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The wait before retry n (1 based): 2, 4, 8, 16 and 32 seconds.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = retry => TimeSpan.FromSeconds(2 * Math.Pow(2, retry - 1));

    /// <summary>
    /// How often and how long the swap waits for the documents of the last batch to be searchable: Azure counts a new
    /// document about a second after the write.
    /// </summary>
    internal (TimeSpan Interval, TimeSpan Limit) SearchableWait { get; set; } = (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    public static bool IsThrottled(int status) => status is 429 or 503;

    /// <summary>
    /// Adds the fields the documents need, then uploads them. Upload replaces the whole document. A merge would keep fields
    /// that the content no longer has, so a cleared value would stay searchable. The documents in
    /// <paramref name="deleteIds"/> are deleted in the same request; Azure accepts the delete of a document that does not
    /// exist. Per-document failures are in the results; a request that Azure rejects throws.
    /// </summary>
    public Task<IReadOnlyList<IndexingResult>> UploadAsync(
        PhysicalIndex physicalIndex, string indexAlias, IndexDefinition definition, List<SearchDocument> documents, List<IndexFieldMapping> fieldMappings,
        IReadOnlyCollection<string>? deleteIds = null)
        => UploadAsync(physicalIndex, indexAlias, definition, documents, fieldMappings, readSchemaOnFailure: true, deleteIds);

    private async Task<IReadOnlyList<IndexingResult>> UploadAsync(
        PhysicalIndex physicalIndex, string indexAlias, IndexDefinition definition, List<SearchDocument> documents,
        List<IndexFieldMapping> fieldMappings, bool readSchemaOnFailure, IReadOnlyCollection<string>? deleteIds = null)
    {
        var searchClient = azureSearchClientFactory.GetSearchIndexClient().GetSearchClient(physicalIndex.Name);
        var (indexFields, known) = await EnsureFieldsExist(physicalIndex, indexAlias, fieldMappings, documents, definition);
        DropMissingSuggestionCopies(physicalIndex.Name, documents, indexFields);
        var actions = documents.Select(IndexDocumentsAction.Upload).Concat((deleteIds ?? []).Select(DeleteAction)).ToArray();
        try
        {
            return await SendAsync(searchClient, actions);
        }
        catch (RequestFailedException ex) when (ex.Status == 400 && known && readSchemaOnFailure)
        {
            // The known field names were out of date: another server created the index again without a field. Read the
            // schema, add the field, and write once more.
            logger.LogDebug(ex, "Azure rejected a write to {IndexName}. Reading the schema again", physicalIndex.Name);
            schemaUpdates.Forget(physicalIndex.Name);
            await EnsureFieldsExist(physicalIndex, indexAlias, fieldMappings, documents, definition);
            return await SendAsync(searchClient, actions);
        }
    }

    /// <summary>Deletes documents by their id.</summary>
    public Task<IReadOnlyList<IndexingResult>> DeleteAsync(SearchClient searchClient, IEnumerable<string> documentIds)
        => SendAsync(searchClient, documentIds.Select(DeleteAction).ToArray());

    private static IndexDocumentsAction<SearchDocument> DeleteAction(string documentId)
        => IndexDocumentsAction.Delete(new SearchDocument { [IndexConstants.FieldNames.Id] = documentId });

    /// <summary>
    /// Sends the actions and returns a result per document. Throttled requests and documents are sent again (see the
    /// remarks of the class); other per-document failures are in the results, and a request that Azure rejects throws.
    /// </summary>
    public async Task<IReadOnlyList<IndexingResult>> SendAsync(
        SearchClient searchClient, IReadOnlyList<IndexDocumentsAction<SearchDocument>> actions)
    {
        var results = new List<IndexingResult>();
        var pending = actions;
        for (var attempt = 1; ; attempt++)
        {
            string reason;
            try
            {
                var response = await searchClient.IndexDocumentsAsync(
                    IndexDocumentsBatch.Create(pending.ToArray()), new IndexDocumentsOptions { ThrowOnAnyError = false });
                var throttled = response.Value.Results.Where(r => IsThrottled(r.Status) && attempt < Attempts).Select(r => r.Key).ToHashSet();
                results.AddRange(response.Value.Results.Where(r => !throttled.Contains(r.Key)));
                if (throttled.Count == 0)
                    return results;

                pending = pending.Where(a => throttled.Contains(Id(a.Document))).ToArray();
                reason = $"{throttled.Count} document(s) throttled";
            }
            catch (RequestFailedException ex) when (IsThrottled(ex.Status) && attempt < Attempts)
            {
                reason = $"request throttled ({ex.Status})";
            }

            var delay = RetryDelay(attempt);
            logger.LogWarning(
                "Azure AI Search throttled a write to {IndexName}: {Reason}. Retry {Retry} of {Retries} in {Delay}",
                searchClient.IndexName, reason, attempt, Attempts - 1, delay);
            await Task.Delay(delay);
        }
    }

    /// <summary>
    /// Adds the documents of one item to the batch of their Azure index, and sends the batch when it is full. The documents
    /// of an item stay together in one request.
    /// </summary>
    public async Task AddAsync(
        RebuildBatch batch, PhysicalIndex physicalIndex, string indexAlias, IndexDefinition definition,
        List<SearchDocument> documents, List<IndexFieldMapping> fieldMappings)
    {
        await batch.Lock.WaitAsync();
        try
        {
            if (!batch.Pending.TryGetValue(physicalIndex.Name, out var pending))
            {
                pending = new PendingDocuments(physicalIndex, indexAlias, definition);
                batch.Pending[physicalIndex.Name] = pending;
            }

            // Azure takes at most 1000 documents per request, so a full batch goes before the item that does not fit.
            if (pending.Documents.Count > 0 && pending.Documents.Count + documents.Count > batch.Size)
            {
                await SendPendingAsync(batch, pending);
            }

            pending.Documents.AddRange(documents);
            pending.FieldMappings.AddRange(fieldMappings.Where(m => pending.FieldNames.Add(m.FieldName)));
            if (pending.Documents.Count >= batch.Size)
            {
                await SendPendingAsync(batch, pending);
            }
        }
        finally
        {
            batch.Lock.Release();
        }
    }

    /// <summary>Sends the documents waiting in a batch. Does nothing without a batch.</summary>
    public async Task FlushAsync(RebuildBatch? batch)
    {
        if (batch is null)
            return;

        await batch.Lock.WaitAsync();
        try
        {
            foreach (var pending in batch.Pending.Values.Where(p => p.Documents.Count > 0))
            {
                await SendPendingAsync(batch, pending);
            }
        }
        finally
        {
            batch.Lock.Release();
        }
    }

    /// <summary>Drops the documents waiting in a batch, e.g. of a cancelled rebuild.</summary>
    public static async Task<int> DropAsync(RebuildBatch? batch)
    {
        if (batch is null)
            return 0;

        await batch.Lock.WaitAsync();
        try
        {
            var dropped = batch.Pending.Values.Sum(p => p.Documents.Count);
            batch.Pending.Clear();
            return dropped;
        }
        finally
        {
            batch.Lock.Release();
        }
    }

    /// <summary>
    /// Waits until a search counts the documents that the batches wrote to an index, or until the limit. Azure counts a
    /// new document about a second after the write, so a count right after the last batch can miss it.
    /// </summary>
    public async Task WaitUntilSearchableAsync(RebuildBatch? batch, string indexName, Func<string, Task<long>> count)
    {
        var written = batch?.Written.GetValueOrDefault(indexName) ?? 0;
        if (written == 0)
            return;

        var (interval, limit) = SearchableWait;
        for (var waited = TimeSpan.Zero; waited < limit && await count(indexName) < written; waited += interval)
        {
            await Task.Delay(interval);
        }
    }

    private async Task SendPendingAsync(RebuildBatch batch, PendingDocuments pending)
    {
        var documents = pending.Documents.ToList();
        pending.Documents.Clear();
        logger.LogDebug("Indexing a batch of {DocumentCount} document(s) into {IndexAlias} ({IndexName})",
            documents.Count, pending.IndexAlias, pending.PhysicalIndex.Name);
        var written = await SendSplittingAsync(pending, documents, readSchemaOnFailure: true);
        batch.Written[pending.PhysicalIndex.Name] = batch.Written.GetValueOrDefault(pending.PhysicalIndex.Name) + written;
    }

    // One broken document must not stop the rebuild. A document Azure rejects in an accepted request is logged; a request
    // Azure rejects as a whole (400, 413 too large, ...) is split in two halves, down to single documents. A document that
    // still fails alone is logged and left out. Throttling is retried in SendAsync and is not split. Returns the documents
    // written.
    private async Task<int> SendSplittingAsync(PendingDocuments pending, List<SearchDocument> documents, bool readSchemaOnFailure)
    {
        try
        {
            var results = await UploadAsync(pending.PhysicalIndex, pending.IndexAlias, pending.Definition, documents, pending.FieldMappings, readSchemaOnFailure);
            foreach (var failure in results.Where(r => !r.Succeeded))
            {
                logger.LogError(
                    "Failed to index document {DocumentKey} for content {Id} into {IndexAlias}: [{StatusCode}] {ErrorMessage}",
                    failure.Key, ContentKey(documents, failure.Key), pending.IndexAlias, failure.Status, failure.ErrorMessage);
            }

            return results.Count(r => r.Succeeded);
        }
        catch (RequestFailedException ex) when (ex.Status is >= 400 and < 500 and not 404 and not 429)
        {
            if (documents.Count == 1)
            {
                logger.LogError(ex,
                    "Failed to index document {DocumentKey} for content {Id} into {IndexAlias}: [{StatusCode}] {ErrorMessage}. The rebuild goes on without it",
                    Id(documents[0]), ContentKey(documents, Id(documents[0])), pending.IndexAlias, ex.Status, ex.Message);
                return 0;
            }

            logger.LogWarning("Azure rejected a batch of {DocumentCount} document(s) for {IndexName} ({StatusCode}). Sending it in two halves",
                documents.Count, pending.PhysicalIndex.Name, ex.Status);
            var half = documents.Count / 2;
            return await SendSplittingAsync(pending, documents.GetRange(0, half), readSchemaOnFailure: false)
                   + await SendSplittingAsync(pending, documents.GetRange(half, documents.Count - half), readSchemaOnFailure: false);
        }
    }

    // Adds the fields the documents need and the index lacks. Returns the field names of the index, and true when they
    // came from memory without a read.
    private async Task<(IReadOnlySet<string> Fields, bool Known)> EnsureFieldsExist(
        PhysicalIndex physicalIndex, string indexAlias, List<IndexFieldMapping> fieldMappings, IEnumerable<SearchDocument> documents, IndexDefinition definition)
    {
        var indexName = physicalIndex.Name;

        // A suggestion copy the index lacks is dropped from the documents, not added (see DropMissingSuggestionCopies).
        var needed = documents.SelectMany(d => d.Keys).Concat(fieldMappings.Select(m => m.FieldName))
            .Where(name => !name.StartsWith($"{IndexConstants.FieldNames.Suggestion}_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        bool HasAll(IReadOnlySet<string> fields) => needed.All(fields.Contains);
        if (schemaUpdates.KnownFields(indexName) is { } known && HasAll(known))
            return (known, true);

        var fields = await schemaUpdates.UpdateAsync(
            indexName,
            index =>
            {
                // The per-culture aggregate copies of a culture added to the definition
                var changed = IndexSchema.AddMissingCopies(index, definition);
                var currentFieldNames = index.Fields.Select(f => f.Name).ToHashSet();

                // Find missing fields, and add them to the index schema using metadata from field mappings
                foreach (var mapping in fieldMappings.Where(m => !currentFieldNames.Contains(m.FieldName)).DistinctBy(m => m.FieldName))
                {
                    index.Fields.Add(IndexSchema.CreateField(mapping));
                    currentFieldNames.Add(mapping.FieldName);
                    changed = true;
                }

                return changed;
            },
            definition.IndexPerCulture
                ? async () =>
                {
                    // The fallback index of IndexPerCulture, for the first document of a language outside the map.
                    logger.LogWarning("Creating index {IndexName} for a culture outside the culture map. Rebuild the index to fill it with existing content.", indexName);
                    var profiles = await serviceProvider.GetRequiredService<ScoringProfileSources>().GetAsync(indexAlias);
                    return IndexSchema.BuildNew(physicalIndex, indexAlias, definition.WithProviderProfiles(profiles));
                }
                : null,
            HasAll);
        return (fields, false);
    }

    // Azure adds fields to a suggester only when it creates the suggester, so a suggestion copy of a culture added
    // after the index was created waits for a rebuild. Writing it before would fail the whole document.
    private void DropMissingSuggestionCopies(string indexName, List<SearchDocument> documents, IReadOnlySet<string> indexFields)
    {
        var prefix = $"{IndexConstants.FieldNames.Suggestion}_";
        var missing = documents.SelectMany(d => d.Keys)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !indexFields.Contains(k))
            .ToHashSet();
        if (missing.Count == 0)
            return;

        foreach (var document in documents)
        {
            foreach (var key in missing)
            {
                document.Remove(key);
            }
        }

        if (_missingSuggestionCopyWarnings.TryAdd(indexName, true))
        {
            logger.LogWarning(
                "Index {IndexName} has no suggestion fields {Fields}. Rebuild the index to add them.",
                indexName, string.Join(", ", missing.Order()));
        }
    }

    private static string? Id(SearchDocument document)
        => document.TryGetValue(IndexConstants.FieldNames.Id, out var id) ? id?.ToString() : null;

    private static string? ContentKey(IEnumerable<SearchDocument> documents, string? documentId)
        => documents.FirstOrDefault(d => Id(d) == documentId) is { } document && document.TryGetValue(IndexConstants.FieldNames.Key, out var key)
            ? key?.ToString()
            : null;
}

/// <summary>The rebuild writes that wait to be sent, per Azure index.</summary>
internal sealed class RebuildBatch(int size)
{
    /// <summary>The documents per request.</summary>
    public int Size { get; } = size;

    public SemaphoreSlim Lock { get; } = new(1, 1);

    public Dictionary<string, PendingDocuments> Pending { get; } = new(StringComparer.Ordinal);

    /// <summary>The documents the batches wrote, per Azure index.</summary>
    public ConcurrentDictionary<string, long> Written { get; } = new(StringComparer.Ordinal);
}

/// <summary>The documents waiting for one Azure index, and the fields they bring.</summary>
internal sealed class PendingDocuments(PhysicalIndex physicalIndex, string indexAlias, IndexDefinition definition)
{
    public PhysicalIndex PhysicalIndex { get; } = physicalIndex;

    public string IndexAlias { get; } = indexAlias;

    public IndexDefinition Definition { get; } = definition;

    public List<SearchDocument> Documents { get; } = [];

    public List<IndexFieldMapping> FieldMappings { get; } = [];

    public HashSet<string> FieldNames { get; } = new(StringComparer.Ordinal);
}
