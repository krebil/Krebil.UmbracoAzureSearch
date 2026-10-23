using System.Collections.Concurrent;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Throw;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.IndexManager;

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

        var variationList = variations.Where(v => definition.HoldsCulture(v.Culture)).ToArray();
        if (variationList.Length == 0)
        {
            logger.LogDebug("Skipping {Id} for {IndexAlias}: none of its cultures is in the index", id, indexAlias);
            return;
        }

        fieldList = await AddIndexerFieldsAsync(indexAlias, definition, id, objectType, variationList, fieldList);

        var searchClient = azureSearchClientFactory.GetSearchClient(indexAlias);

        var mappingResult = documentMapper.MapToDocuments(id, objectType, variationList, fieldList, protection, definition);

        await EnsureFieldsExist(indexAlias, mappingResult.FieldMappings);
        var batch = IndexDocumentsBatch.MergeOrUpload(mappingResult.Documents);
        logger.LogDebug("Indexing {DocumentCount} document(s) for {Id} into {IndexAlias}", mappingResult.Documents.Count, id, indexAlias);
        var result = await searchClient.IndexDocumentsAsync(batch, new IndexDocumentsOptions { ThrowOnAnyError = false });
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

    private async Task EnsureFieldsExist(string indexAlias, List<IndexFieldMapping> fieldMappings)
    {
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        var index = await indexClient.GetIndexAsync(indexAlias);
        index.ThrowIfNull();

        var currentFieldNames = index.Value.Fields.Select(f => f.Name).ToHashSet();

        // Get all field names from field mappings
        var mappedFieldNames = fieldMappings.Select(m => m.FieldName).Distinct().ToHashSet();

        // Find missing fields
        var missingFields = mappedFieldNames.Except(currentFieldNames).ToList();

        if (missingFields.Count == 0)
            return;

        // Add missing fields to the index schema using metadata from field mappings
        foreach (var fieldName in missingFields)
        {
            var mapping = fieldMappings.First(m => m.FieldName == fieldName);
            var field = CreateFieldDefinition(mapping);
            index.Value.Fields.Add(field);
        }

        // Update the index with new fields
        await indexClient.CreateOrUpdateIndexAsync(index.Value);
    }

    private SearchField CreateFieldDefinition(IndexFieldMapping mapping)
    {
        // Create the appropriate SearchFieldDataType
        var searchFieldDataType = mapping.IsCollection
            ? SearchFieldDataType.Collection(mapping.FieldType)
            : mapping.FieldType;

        var field = new SearchField(mapping.FieldName, searchFieldDataType)
        {
            IsFilterable = mapping.IsFilterable ?? !mapping.IsSortable,
            IsSortable = mapping.IsSortable,
            IsFacetable = mapping.IsFacetable,
            IsSearchable = mapping.IsSearchable,
            AnalyzerName = mapping.AnalyzerName is null ? (LexicalAnalyzerName?)null : new LexicalAnalyzerName(mapping.AnalyzerName),
        };

        return field;
    }

    public async Task DeleteAsync(string indexAlias, IEnumerable<Guid> ids)
    {
        if (ShouldNotManipulateIndexes())
            return;

        var searchClient = azureSearchClientFactory.GetSearchClient(indexAlias);

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

    public async Task ResetAsync(string indexAlias)
    {
        await azureSearchIndexManager.ResetAsync(indexAlias);
    }

    public async Task<IndexMetadata> GetMetadataAsync(string indexAlias)
    {
        // use GetSearchClient to get the resolved index name (alias resolution happens inside the factory)
        var resolvedIndexName = azureSearchClientFactory.GetSearchClient(indexAlias).IndexName;
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();

        try
        {
            var response = await indexClient.GetIndexStatisticsAsync(resolvedIndexName);
            var documentCount = response.Value.DocumentCount;
            var healthStatus = documentCount > 0 ? HealthStatus.Healthy : HealthStatus.Empty;
            return new IndexMetadata(documentCount, healthStatus, IndexConstants.ProviderIdentifier);
        }
        catch
        {
            return new IndexMetadata(0, HealthStatus.Unknown, IndexConstants.ProviderIdentifier);
        }
    }
}