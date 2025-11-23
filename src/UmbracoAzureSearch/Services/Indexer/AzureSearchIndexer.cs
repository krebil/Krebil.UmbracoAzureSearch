using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Throw;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Models.Indexing;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Services.Indexer;

public class AzureSearchIndexer(
    IAzureSearchClientFactory azureSearchClientFactory,
    IAzureSearchIndexManager azureSearchIndexManager,
    IServerRoleAccessor serverRoleAccessor,
    DocumentMapper documentMapper) : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchIndexer
{
    public async Task AddOrUpdateAsync(string indexAlias, Guid id, UmbracoObjectTypes objectType,
        IEnumerable<Variation> variations,
        IEnumerable<IndexField> fields, ContentProtection? protection)
    {
        if (ShouldNotManipulateIndexes())
            return;

        var searchClient = azureSearchClientFactory.GetSearchClient(indexAlias);

        var mappingResult = documentMapper.MapToDocuments(id, objectType, variations, fields, protection);

        await EnsureFieldsExist(indexAlias, mappingResult.FieldMappings);
        var batch = IndexDocumentsBatch.MergeOrUpload(mappingResult.Documents);
        await searchClient.IndexDocumentsAsync(batch);
    }

    private async Task EnsureFieldsExist(string indexAlias, List<IndexFieldMapping> fieldMappings)
    {
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
            IsFilterable = !mapping.IsSortable,
            IsSortable = mapping.IsSortable || (!mapping.IsCollection && mapping.FieldType != SearchFieldDataType.String),
            IsFacetable = !mapping.IsSortable && mapping.FieldType != SearchFieldDataType.String,
            IsSearchable = mapping.IsSearchable,
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
            await searchClient.DeleteDocumentsAsync(IndexConstants.FieldNames.Id, documentIds);
        }
    }

    public async Task ResetAsync(string indexAlias)
    {
        await azureSearchIndexManager.ResetAsync(indexAlias);
    }
}