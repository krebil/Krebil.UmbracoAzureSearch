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

    // Index aliases already warned about missing suggestion copies; the indexer is a singleton, so one warning per alias.
    private readonly ConcurrentDictionary<string, bool> _missingSuggestionCopyWarnings = new(StringComparer.OrdinalIgnoreCase);

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

        var rebuildWrite = serviceProvider.GetRequiredService<RebuildWrites>().Current(indexAlias) is not null;
        var physicalIndexes = serviceProvider.GetRequiredService<PhysicalIndexes>();
        var all = await physicalIndexes.AllAsync(indexAlias, definition);

        var variationList = variations.Where(v => definition.HoldsCulture(v.Culture)).ToArray();
        if (variationList.Length == 0)
        {
            // The index may still hold a culture that the content no longer has.
            logger.LogDebug("Skipping {Id} for {IndexAlias}: none of its cultures is in the index", id, indexAlias);
            await DeleteRemovedVariationsAsync(indexAlias, definition, id, all, rebuildWrite);
            return;
        }

        fieldList = await AddIndexerFieldsAsync(indexAlias, definition, id, objectType, variationList, fieldList);

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
            await WriteAsync(indexAlias, target.Key, definition.For(target.Key.Analyzer), id, objectType, target.ToArray(), fieldList, protection, rebuildWrite);
        }

        // A culture that is no longer published has documents in its own index, which got nothing to write.
        await DeleteRemovedVariationsAsync(indexAlias, definition, id, all.Except(targets.Select(t => t.Key)), rebuildWrite);
    }

    // The Azure indexes a write goes to, and whether documents of removed variations can be there. A zero-downtime
    // rebuild only fills the new slot: its own writes go there only, and the new slot starts empty, so they have nothing
    // to clean up. Other writes during a rebuild go to the old index and the new slot, so the old index stays current
    // and the new slot misses nothing.
    private async Task<(string Name, bool Cleanup)[]> TargetsAsync(string name, IndexDefinition definition, bool rebuildWrite)
    {
        var slots = serviceProvider.GetRequiredService<IndexSlots>();
        if (rebuildWrite && slots.NewSlot(name, definition) is { } newSlot)
            return [(newSlot, false)];

        return (await slots.WriteTargetsAsync(name, definition)).Select(target => (target, true)).ToArray();
    }

    // Deletes every document of the content from indexes that got nothing to write.
    private async Task DeleteRemovedVariationsAsync(
        string indexAlias, IndexDefinition definition, Guid id, IEnumerable<PhysicalIndex> physicalIndexes, bool rebuildWrite)
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
                    await DeleteRemovedVariationsAsync(searchClient, indexAlias, id, []);
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
        bool rebuildWrite)
    {
        foreach (var (target, cleanup) in await TargetsAsync(physicalIndex.Name, definition, rebuildWrite))
        {
            await WriteToIndexAsync(indexAlias, physicalIndex with { Name = target }, definition, id, objectType, variations, fields, protection, cleanup);
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
        bool cleanup)
    {
        var searchClient = azureSearchClientFactory.GetSearchIndexClient().GetSearchClient(physicalIndex.Name);

        var mappingResult = documentMapper.MapToDocuments(id, objectType, variations, fields, protection, definition);

        var (indexFields, known) = await EnsureFieldsExist(physicalIndex, indexAlias, mappingResult.FieldMappings, mappingResult.Documents, definition);
        DropMissingSuggestionCopies(physicalIndex.Name, mappingResult.Documents, indexFields);
        // Upload replaces the whole document. A merge would keep fields that the content no longer has,
        // so a cleared value would stay searchable.
        var batch = IndexDocumentsBatch.Upload(mappingResult.Documents);
        logger.LogDebug("Indexing {DocumentCount} document(s) for {Id} into {IndexAlias} ({IndexName})", mappingResult.Documents.Count, id, indexAlias, physicalIndex.Name);
        Response<IndexDocumentsResult> result;
        try
        {
            result = await searchClient.IndexDocumentsAsync(batch, new IndexDocumentsOptions { ThrowOnAnyError = false });
        }
        catch (RequestFailedException ex) when (ex.Status == 400 && known)
        {
            // The known field names were out of date: another server created the index again without a field. Read the
            // schema, add the field, and write once more.
            logger.LogDebug(ex, "Azure rejected the documents of {Id} for {IndexName}. Reading the schema again", id, physicalIndex.Name);
            serviceProvider.GetRequiredService<IndexSchemaUpdates>().Forget(physicalIndex.Name);
            await EnsureFieldsExist(physicalIndex, indexAlias, mappingResult.FieldMappings, mappingResult.Documents, definition);
            result = await searchClient.IndexDocumentsAsync(batch, new IndexDocumentsOptions { ThrowOnAnyError = false });
        }
        var failed = result.Value.Results.Where(r => !r.Succeeded).ToList();
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
            if (!cleanup)
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
        var deleteResult = await searchClient.DeleteDocumentsAsync(IndexConstants.FieldNames.Id, removedIds, new IndexDocumentsOptions { ThrowOnAnyError = false });
        foreach (var failure in deleteResult.Value.Results.Where(r => !r.Succeeded))
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

        var contentTypeId = fields
            .FirstOrDefault(f => f.FieldName == Umbraco.Cms.Search.Core.Constants.FieldNames.ContentTypeId)?
            .Value.Keywords?.FirstOrDefault();
        if (!Guid.TryParse(contentTypeId, out var contentTypeKey))
            return false;

        var contentSource = serviceProvider.GetRequiredService<IUmbracoContentSource>();
        return contentTypes.Any(alias => contentSource.GetContentTypeKey(alias, objectType) == contentTypeKey);
    }

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

    // Adds the fields the documents need and the index lacks. Returns the field names of the index, and true when they
    // came from memory without a read.
    private async Task<(IReadOnlySet<string> Fields, bool Known)> EnsureFieldsExist(
        PhysicalIndex physicalIndex, string indexAlias, List<IndexFieldMapping> fieldMappings, IEnumerable<SearchDocument> documents, IndexDefinition definition)
    {
        var indexName = physicalIndex.Name;
        var schema = serviceProvider.GetRequiredService<IndexSchemaUpdates>();

        // A suggestion copy the index lacks is dropped from the documents, not added (see DropMissingSuggestionCopies).
        var needed = documents.SelectMany(d => d.Keys).Concat(fieldMappings.Select(m => m.FieldName))
            .Where(name => !name.StartsWith($"{IndexConstants.FieldNames.Suggestion}_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        bool HasAll(IReadOnlySet<string> fields) => needed.All(fields.Contains);
        if (schema.KnownFields(indexName) is { } known && HasAll(known))
            return (known, true);

        var fields = await schema.UpdateAsync(
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

    public async Task DeleteAsync(string indexAlias, IEnumerable<Guid> ids)
    {
        if (ShouldNotManipulateIndexes())
            return;

        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
        var idList = ids.ToArray();
        var rebuildWrite = serviceProvider.GetRequiredService<RebuildWrites>().Current(indexAlias) is not null;
        foreach (var physicalIndex in await serviceProvider.GetRequiredService<PhysicalIndexes>().AllAsync(indexAlias, definition))
        {
            foreach (var (target, _) in await TargetsAsync(physicalIndex.Name, definition, rebuildWrite))
            {
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
            var deleteResult = await searchClient.DeleteDocumentsAsync(IndexConstants.FieldNames.Id, documentIds, new IndexDocumentsOptions { ThrowOnAnyError = false });
            var failedDeletes = deleteResult.Value.Results.Where(r => !r.Succeeded).ToList();
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

        serviceProvider.GetRequiredService<RebuildWrites>().Begin(indexAlias);
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
