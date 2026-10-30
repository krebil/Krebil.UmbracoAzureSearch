using Azure;
using System.Collections.Concurrent;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Scoring;

namespace UmbracoAzureSearch.Services.Indexer;

public class AzureSearchIndexer(
    IAzureSearchClientFactory azureSearchClientFactory,
    IAzureSearchIndexManager azureSearchIndexManager,
    IServerRoleAccessor serverRoleAccessor,
    IIndexAliasResolver indexAliasResolver,
    ILogger<AzureSearchIndexer> logger,
    DocumentMapper documentMapper,
    IOptionsMonitor<AzureSearchIndexOptions> azureSearchIndexOptions,
    IOptions<IndexOptions> indexOptions,
    IServiceProvider serviceProvider) : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchIndexer
{
    private readonly ConcurrentDictionary<Type, IContentIndexer> _indexers = new();

    public async Task AddOrUpdateAsync(string indexAlias, Guid id, UmbracoObjectTypes objectType,
        IEnumerable<Variation> variations,
        IEnumerable<IndexField> fields, ContentProtection? protection)
    {
        if (ShouldNotManipulateIndexes())
            return;

        // Every indexing path ends here (queue, rebuild, direct calls), so the index definition applies to all of them.
        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
        var fieldList = fields as IReadOnlyCollection<IndexField> ?? fields.ToArray();
        if (!InContentTypeScope(definition, objectType, fieldList))
        {
            logger.LogDebug("Skipping {Id} for {IndexAlias}: its content type is outside the index", id, indexAlias);
            return;
        }

        var rebuildWrites = serviceProvider.GetRequiredService<RebuildWrites>();
        var rebuild = rebuildWrites.Current(indexAlias);
        var rebuildWrite = rebuild is not null;
        if (!rebuildWrite)
        {
            await FlushRebuildBatchAsync(rebuildWrites, indexAlias);
        }

        var physicalIndexes = serviceProvider.GetRequiredService<PhysicalIndexes>();
        var all = await physicalIndexes.AllAsync(indexAlias, definition);

        var allVariations = variations.ToArray();
        var variationList = allVariations.Where(v => definition.HoldsCulture(v.Culture)).ToArray();
        if (variationList.Length == 0)
        {
            // The index may still hold a culture that the content no longer has.
            logger.LogDebug("Skipping {Id} for {IndexAlias}: none of its cultures is in the index", id, indexAlias);
            await DeleteRemovedVariationsAsync(indexAlias, definition, id, all, rebuildWrite,
                rebuildWrite ? null : await CandidateIdsAsync(indexAlias, definition, id, objectType, allVariations, fieldList));
            return;
        }

        fieldList = await AddIndexerFieldsAsync(indexAlias, definition, id, objectType, variationList, fieldList);
        var candidates = rebuildWrite ? null : await CandidateIdsAsync(indexAlias, definition, id, objectType, allVariations, fieldList);

        // IndexPerCulture: each culture goes to its own index, invariant documents to every index of the alias.
        var targets = variationList
            .SelectMany(variation => (variation.Culture is null && definition.IndexPerCulture
                    ? all
                    : [physicalIndexes.ForCulture(indexAlias, definition, variation.Culture ?? string.Empty)])
                .Select(physicalIndex => (PhysicalIndex: physicalIndex, Variation: variation)))
            .GroupBy(t => t.PhysicalIndex, t => t.Variation)
            .ToArray();

        foreach (var target in targets)
        {
            await WriteAsync(indexAlias, target.Key, definition.For(target.Key.Analyzer), id, objectType, target.ToArray(), fieldList, protection, rebuild,
                candidates?.Invoke(target.Key));
        }

        // A culture that is no longer published has documents in its own index, which got nothing to write.
        await DeleteRemovedVariationsAsync(indexAlias, definition, id, all.Except(targets.Select(t => t.Key)), rebuildWrite, candidates);
    }

    // The ids of the documents the content can have in an Azure index: one per Umbraco language, and the invariant
    // document. With IndexPerCulture an index only holds its own cultures, and the invariant document. The write deletes
    // the ids it does not upload, in the same request, so it needs no search. The package cannot list the segments, so
    // with a segment in the write, or a content type that varies by segment (an item can lose its last segment value),
    // the result is null and the write searches as before. Null too when the languages cannot be read.
    private async Task<Func<PhysicalIndex, string[]>?> CandidateIdsAsync(
        string indexAlias, IndexDefinition definition, Guid id, UmbracoObjectTypes objectType, Variation[] variations,
        IReadOnlyCollection<IndexField> fields)
    {
        var contentSource = serviceProvider.GetRequiredService<IUmbracoContentSource>();
        if (variations.Any(v => v.Segment is not null)
            || fields.Any(f => f.Segment is not null)
            || (ContentTypeKey(fields) is { } contentTypeKey && contentSource.VariesBySegment(contentTypeKey, objectType)))
            return null;

        string[] languages;
        try
        {
            languages = await contentSource.GetLanguageIsoCodesAsync();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read the Umbraco languages. Searching for the documents of removed variations of {Id}", id);
            return null;
        }

        var physicalIndexes = serviceProvider.GetRequiredService<PhysicalIndexes>();
        return physicalIndex => languages
            .Where(language => !definition.IndexPerCulture
                               || physicalIndexes.ForCulture(indexAlias, definition, language).Name == physicalIndex.Name)
            .Select(language => DocumentMapper.DocumentId(id, language, null))
            .Append(DocumentMapper.DocumentId(id, null, null))
            .Distinct()
            .ToArray();
    }

    // The rebuild documents that wait in a batch go before a write or delete from outside the rebuild, so the change is
    // not followed by an older document of the same item. A batch that fails here, for example of a rebuild that threw
    // and left its batch behind, must not fail the change: the rebuild fails or is run again.
    private async Task FlushRebuildBatchAsync(RebuildWrites rebuildWrites, string indexAlias)
    {
        var batch = rebuildWrites.Active(indexAlias)?.Batch;
        try
        {
            await serviceProvider.GetRequiredService<DocumentWriter>().FlushAsync(batch);
        }
        catch (Exception exception)
        {
            var dropped = await DocumentWriter.DropAsync(batch);
            logger.LogWarning(exception, "Could not send the waiting rebuild documents of {IndexAlias}. Dropped {DocumentCount} document(s). Rebuild the index again", indexAlias, dropped);
        }
    }

    // The Azure indexes a write goes to, and whether documents of removed variations can be there. A rebuild starts from
    // an empty index, so its own writes have nothing to clean up. A zero-downtime rebuild only fills the new slot: its own
    // writes go there only. Other writes during a rebuild go to the old index and the new slot, so the old index stays
    // current and the new slot misses nothing.
    private async Task<(string Name, bool Cleanup)[]> TargetsAsync(string name, IndexDefinition definition, bool rebuildWrite)
    {
        var slots = serviceProvider.GetRequiredService<IndexSlots>();
        if (rebuildWrite)
            return [(slots.NewSlot(name, definition) ?? (await slots.WriteTargetsAsync(name, definition))[0], false)];

        return (await slots.WriteTargetsAsync(name, definition)).Select(target => (target, true)).ToArray();
    }

    // Deletes every document of the content from indexes that got nothing to write.
    private async Task DeleteRemovedVariationsAsync(
        string indexAlias, IndexDefinition definition, Guid id, IEnumerable<PhysicalIndex> physicalIndexes, bool rebuildWrite,
        Func<PhysicalIndex, string[]>? candidates)
    {
        foreach (var physicalIndex in physicalIndexes)
        {
            // Zero-downtime rebuild: the deletes go where the writes go.
            foreach (var (target, cleanup) in await TargetsAsync(physicalIndex.Name, definition, rebuildWrite))
            {
                if (!cleanup)
                    continue;

                try
                {
                    var searchClient = azureSearchClientFactory.GetSearchIndexClient().GetSearchClient(target);
                    if (candidates is null)
                    {
                        await DeleteRemovedVariationsAsync(searchClient, indexAlias, id, []);
                    }
                    else
                    {
                        var candidateIds = candidates(physicalIndex);
                        logger.LogDebug("Deleting {DocumentCount} possible document(s) of removed variations for {Id} from {IndexAlias} ({IndexName})", candidateIds.Length, id, indexAlias, target);
                        LogDeleteFailures(await serviceProvider.GetRequiredService<DocumentWriter>().DeleteAsync(searchClient, candidateIds), id, indexAlias);
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // A fallback index that does not exist yet holds nothing to delete.
                }
                catch (Exception ex) when (ex is RequestFailedException or AggregateException)
                {
                    logger.LogWarning(ex, "Failed to delete documents of removed variations for {Id} from {IndexAlias} ({IndexName})", id, indexAlias, target);
                }
            }
        }
    }

    private async Task WriteAsync(
        string indexAlias,
        PhysicalIndex physicalIndex,
        IndexDefinition definition,
        Guid id,
        UmbracoObjectTypes objectType,
        Variation[] variations,
        IReadOnlyCollection<IndexField> fields,
        ContentProtection? protection,
        RebuildFlow? rebuild,
        string[]? candidateIds)
    {
        foreach (var (target, cleanup) in await TargetsAsync(physicalIndex.Name, definition, rebuild is not null))
        {
            if (rebuild?.Batch is { } batch)
            {
                // A batched rebuild write waits in the batch of its index; RebuildBatchSize documents go in one request.
                var mappingResult = documentMapper.MapToDocuments(id, objectType, variations, fields, protection, definition);
                await serviceProvider.GetRequiredService<DocumentWriter>().AddAsync(
                    batch, physicalIndex with { Name = target }, indexAlias, definition, mappingResult.Documents, mappingResult.FieldMappings);
                continue;
            }

            await WriteToIndexAsync(indexAlias, physicalIndex with { Name = target }, definition, id, objectType, variations, fields, protection, cleanup, candidateIds);
        }
    }

    private async Task WriteToIndexAsync(
        string indexAlias,
        PhysicalIndex physicalIndex,
        IndexDefinition definition,
        Guid id,
        UmbracoObjectTypes objectType,
        Variation[] variations,
        IReadOnlyCollection<IndexField> fields,
        ContentProtection? protection,
        bool cleanup,
        string[]? candidateIds)
    {
        var searchClient = azureSearchClientFactory.GetSearchIndexClient().GetSearchClient(physicalIndex.Name);

        var mappingResult = documentMapper.MapToDocuments(id, objectType, variations, fields, protection, definition);

        // The documents of the variations the content does not have go as deletes in the same request.
        var writtenIds = mappingResult.Documents.Select(d => d[IndexConstants.FieldNames.Id].ToString()).ToHashSet();
        var deleteIds = cleanup && candidateIds is not null ? candidateIds.Where(i => !writtenIds.Contains(i)).ToHashSet() : [];

        logger.LogDebug("Indexing {DocumentCount} document(s) for {Id} into {IndexAlias} ({IndexName}), deleting {DeleteCount} possible document(s) of removed variations",
            mappingResult.Documents.Count, id, indexAlias, physicalIndex.Name, deleteIds.Count);
        var results = await serviceProvider.GetRequiredService<DocumentWriter>()
            .UploadAsync(physicalIndex, indexAlias, definition, mappingResult.Documents, mappingResult.FieldMappings, deleteIds);
        LogDeleteFailures(results.Where(r => deleteIds.Contains(r.Key)), id, indexAlias);
        var failed = results.Where(r => !r.Succeeded && !deleteIds.Contains(r.Key)).ToList();
        if (failed.Count > 0)
        {
            foreach (var failure in failed)
            {
                logger.LogError(
                    "Failed to index document {DocumentKey} for content {Id} into {IndexAlias}: [{StatusCode}] {ErrorMessage}",
                    failure.Key, id, indexAlias, failure.Status, failure.ErrorMessage);
            }
        }
        else
        {
            logger.LogDebug("Successfully indexed {DocumentCount} document(s) for {Id} into {IndexAlias}", mappingResult.Documents.Count, id, indexAlias);
            if (!cleanup || candidateIds is not null)
                return;

            try
            {
                await DeleteRemovedVariationsAsync(searchClient, indexAlias, id, mappingResult.Documents);
            }
            catch (Exception ex) when (ex is RequestFailedException or AggregateException)
            {
                // The upload succeeded, so the content is indexed. The next reindex cleans up again.
                logger.LogWarning(ex, "Failed to delete documents of removed variations for {Id} from {IndexAlias}", id, indexAlias);
            }
        }
    }

    // Each variation is a document of its own, and every call holds all variations of the content.
    // A document for this content that was not just written belongs to a variation that no longer
    // exists, for example an unpublished culture. It is deleted after the upload, so the content is
    // never missing from search results.
    private async Task DeleteRemovedVariationsAsync(SearchClient searchClient, string indexAlias, Guid id, IEnumerable<SearchDocument> documents)
    {
        var writtenIds = documents.Select(d => d[IndexConstants.FieldNames.Id].ToString()).ToHashSet();

        var searchOptions = new SearchOptions
        {
            Filter = $"{IndexConstants.FieldNames.Key} eq '{id.AsKeyword()}'",
            Size = 1000,
            Select = { IndexConstants.FieldNames.Id }
        };
        var searchResult = await searchClient.SearchAsync<SearchDocument>("*", searchOptions);

        var removedIds = new List<string>();
        await foreach (var result in searchResult.Value.GetResultsAsync())
        {
            if (result.Document.TryGetValue(IndexConstants.FieldNames.Id, out var documentId)
                && documentId?.ToString() is { } value
                && writtenIds.Contains(value) is false)
            {
                removedIds.Add(value);
            }
        }

        if (removedIds.Count == 0)
            return;

        logger.LogDebug("Deleting {DocumentCount} document(s) of removed variations for {Id} from {IndexAlias}", removedIds.Count, id, indexAlias);
        LogDeleteFailures(await serviceProvider.GetRequiredService<DocumentWriter>().DeleteAsync(searchClient, removedIds), id, indexAlias);
    }

    // Azure answers the delete of a document that does not exist with a success (200). A 404 for one document also means
    // that the document is not there, so it is no failure.
    private void LogDeleteFailures(IEnumerable<IndexingResult> deleteResults, Guid id, string indexAlias)
    {
        foreach (var failure in deleteResults.Where(r => !r.Succeeded && r.Status != 404))
        {
            logger.LogError(
                "Failed to delete document {DocumentKey} of a removed variation for content {Id} from {IndexAlias}: [{StatusCode}] {ErrorMessage}",
                failure.Key, id, indexAlias, failure.Status, failure.ErrorMessage);
        }
    }

    private bool InContentTypeScope(IndexDefinition definition, UmbracoObjectTypes objectType, IReadOnlyCollection<IndexField> fields)
    {
        if (definition.Options.ContentTypes is not { Count: > 0 } contentTypes)
            return true;

        if (ContentTypeKey(fields) is not { } contentTypeKey)
            return false;

        var contentSource = serviceProvider.GetRequiredService<IUmbracoContentSource>();
        return contentTypes.Any(alias => contentSource.GetContentTypeKey(alias, objectType) == contentTypeKey);
    }

    private static Guid? ContentTypeKey(IReadOnlyCollection<IndexField> fields)
        => Guid.TryParse(
            fields.FirstOrDefault(f => f.FieldName == Umbraco.Cms.Search.Core.Constants.FieldNames.ContentTypeId)?.Value.Keywords?.FirstOrDefault(),
            out var contentTypeKey)
            ? contentTypeKey
            : null;

    // Runs the per-index indexers. Their fields go through the same definition as the fields from Umbraco Search.
    private async Task<IReadOnlyCollection<IndexField>> AddIndexerFieldsAsync(
        string indexAlias,
        IndexDefinition definition,
        Guid id,
        UmbracoObjectTypes objectType,
        Variation[] variations,
        IReadOnlyCollection<IndexField> fields)
    {
        if (definition.Options.Indexers.Count == 0)
            return fields;

        var content = serviceProvider.GetRequiredService<IUmbracoContentSource>().GetContent(id, objectType);
        if (content is null)
        {
            logger.LogWarning("Could not load {ObjectType} {Id} to run the indexers of {IndexAlias}", objectType, id, indexAlias);
            return fields;
        }

        var published = typeof(IPublishedContentChangeStrategy).IsAssignableFrom(
            indexOptions.Value.GetContentIndexRegistration(indexAlias)?.ContentChangeStrategy);
        var cultures = variations.Select(v => v.Culture).Distinct().ToArray();

        // The first field with a name, culture and segment wins, as in Umbraco Search.
        var result = fields.ToDictionary(Identifier);
        foreach (var indexerType in definition.Options.Indexers)
        {
            var indexer = _indexers.GetOrAdd(indexerType,
                type => (IContentIndexer)ActivatorUtilities.GetServiceOrCreateInstance(serviceProvider, type));
            foreach (var field in await indexer.GetIndexFieldsAsync(content, cultures, published, CancellationToken.None))
            {
                if (!result.TryAdd(Identifier(field), field))
                {
                    logger.LogWarning(
                        "Duplicate index field {FieldName} (culture {Culture}, segment {Segment}) from indexer {Indexer} for {Id} in {IndexAlias} was ignored",
                        field.FieldName, field.Culture ?? "[null]", field.Segment ?? "[null]", indexerType.FullName, id, indexAlias);
                }
            }
        }

        return result.Values;

        static string Identifier(IndexField field) => $"{field.FieldName}|{field.Culture}|{field.Segment}";
    }

    public async Task DeleteAsync(string indexAlias, IEnumerable<Guid> ids)
    {
        if (ShouldNotManipulateIndexes())
            return;

        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
        var idList = ids.ToArray();
        var rebuildWrites = serviceProvider.GetRequiredService<RebuildWrites>();
        var rebuild = rebuildWrites.Current(indexAlias);

        if (rebuild is null)
        {
            await FlushRebuildBatchAsync(rebuildWrites, indexAlias);
        }

        foreach (var physicalIndex in await serviceProvider.GetRequiredService<PhysicalIndexes>().AllAsync(indexAlias, definition))
        {
            foreach (var (target, _) in await TargetsAsync(physicalIndex.Name, definition, rebuild is not null))
            {
                // An index the reset of the rebuild emptied holds no document of an item the rebuild deletes, or of its
                // descendants, so the search would find nothing. See "Deletes of the rebuild" in zero-downtime-rebuild.md.
                if (rebuild?.WasReset(target) == true)
                {
                    logger.LogDebug("Skipping the delete of {Ids} from {IndexAlias} ({IndexName}): the rebuild wrote no document of them", idList, indexAlias, target);
                    continue;
                }

                try
                {
                    await DeleteAsync(indexAlias, azureSearchClientFactory.GetSearchIndexClient().GetSearchClient(target), idList);
                }
                catch (RequestFailedException ex) when (ex.Status == 404 && definition.IndexPerCulture)
                {
                    // A fallback index that does not exist yet holds nothing to delete.
                }
            }
        }
    }

    private async Task DeleteAsync(string indexAlias, SearchClient searchClient, Guid[] ids)
    {
        // Need to find all document IDs (which include culture and segment) for documents
        // that have any of the given IDs in their path (cascade delete for all descendants)
        var pathIdFilters = ids.Select(id => $"{Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds}{IndexConstants.FieldTypePostfix.Keywords}/any(p: p eq '{id:D}')");
        var filter = string.Join(" or ", pathIdFilters);

        var searchOptions = new SearchOptions
        {
            Filter = filter,
            Size = 10000, // Get all matching documents
            Select = { IndexConstants.FieldNames.Id }
        };

        // Search for all documents with these IDs in their path
        var searchResult = await searchClient.SearchAsync<SearchDocument>("*", searchOptions);

        // Extract the document IDs
        var documentIds = new List<string>();
        await foreach (var result in searchResult.Value.GetResultsAsync())
        {
            if (result.Document.TryGetValue(IndexConstants.FieldNames.Id, out var idValue) && idValue != null)
            {
                documentIds.Add(idValue.ToString()!);
            }
        }

        // Delete by the primary key field (Id)
        if (documentIds.Any())
        {
            logger.LogDebug("Deleting {DocumentCount} document(s) from {IndexAlias} for ids {Ids}", documentIds.Count, indexAlias, ids);
            var deleteResults = await serviceProvider.GetRequiredService<DocumentWriter>().DeleteAsync(searchClient, documentIds);
            var failedDeletes = deleteResults.Where(r => !r.Succeeded).ToList();
            if (failedDeletes.Count > 0)
            {
                foreach (var failure in failedDeletes)
                {
                    logger.LogError(
                        "Failed to delete document {DocumentKey} from {IndexAlias}: [{StatusCode}] {ErrorMessage}",
                        failure.Key, indexAlias, failure.Status, failure.ErrorMessage);
                }
            }
        }
    }

    // Not async: Umbraco Search calls ResetAsync at the start of a rebuild and writes the items in the same async flow.
    // The rebuild flow set here, outside an async method, stays in the flow of the caller (see RebuildWrites).
    public Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return Task.CompletedTask;

        serviceProvider.GetRequiredService<RebuildWrites>().Begin(indexAlias, azureSearchIndexOptions.Get(indexAlias).RebuildBatchSize);
        return azureSearchIndexManager.ResetAsync(indexAlias);
    }

    public async Task<IndexMetadata> GetMetadataAsync(string indexAlias)
    {
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();

        try
        {
            // IndexPerCulture: the documents of every index of the alias. Invariant documents count once per index.
            var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
            long documentCount = 0;
            foreach (var physicalIndex in await serviceProvider.GetRequiredService<PhysicalIndexes>().AllAsync(indexAlias, definition))
            {
                try
                {
                    var liveIndex = await serviceProvider.GetRequiredService<IndexSlots>().LiveAsync(physicalIndex.Name, definition);
                    var response = await indexClient.GetIndexStatisticsAsync(liveIndex);
                    documentCount += response.Value.DocumentCount;
                }
                catch (RequestFailedException ex) when (ex.Status == 404 && definition.IndexPerCulture)
                {
                    // A fallback index that does not exist yet.
                }
            }

            var healthStatus = documentCount > 0 ? HealthStatus.Healthy : HealthStatus.Empty;
            return new IndexMetadata(documentCount, healthStatus, IndexConstants.ProviderIdentifier);
        }
        catch
        {
            return new IndexMetadata(0, HealthStatus.Unknown, IndexConstants.ProviderIdentifier);
        }
    }
}
