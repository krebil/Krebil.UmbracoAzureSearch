using Azure.Search.Documents.Indexes.Models;
using Throw;
using Umbraco.Cms.Core.Sync;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;

namespace UmbracoAzureSearch.Services.IndexManager;

public class AzureSearchIndexManager(
    IServerRoleAccessor serverRoleAccessor,
    IIndexAliasResolver indexAliasResolver,
    IAzureSearchClientFactory azureSearchClientFactory)
    : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchIndexManager
{
    public async Task EnsureAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var searchIndexClient = azureSearchClientFactory.GetSearchIndexClient();
        var indexNames = await searchIndexClient.GetIndexNamesAsync().ToHashSetAsync(CancellationToken.None);
        if (indexNames.Contains(indexAlias))
            return;
        var newIndex = new SearchIndex(indexAlias, [
            new SearchField(IndexConstants.FieldNames.Id,  SearchFieldDataType.String)
            {
                IsKey = true
            },
            new SearchField(IndexConstants.FieldNames.Key, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.ObjectType, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.Culture, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.Segment, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AccessKeys, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AllTexts, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR1, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR2, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR3, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = true
            }
        ]);
        await searchIndexClient.CreateIndexAsync(newIndex);
    }

    public async Task ResetAsync(string indexAlias)
    {
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        var index = await indexClient.GetIndexAsync(indexAlias);
        index.ThrowIfNull();
        await indexClient.DeleteIndexAsync(index);
        await indexClient.CreateIndexAsync(index);
    }
}