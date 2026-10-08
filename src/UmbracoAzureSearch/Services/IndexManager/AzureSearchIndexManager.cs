using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Options;
using Throw;
using Umbraco.Cms.Core.Sync;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;

namespace UmbracoAzureSearch.Services.IndexManager;

public class AzureSearchIndexManager(
    IServerRoleAccessor serverRoleAccessor,
    IIndexAliasResolver indexAliasResolver,
    IAzureSearchClientFactory azureSearchClientFactory,
    IOptions<UmbracoAzureSearchOptions> azureSearchOptions)
    : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchIndexManager
{
    public async Task EnsureAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var searchIndexClient = azureSearchClientFactory.GetSearchIndexClient();
        var indexNames = new HashSet<string>();
        if (azureSearchOptions.Value.IsServerless)
        {
            // Serverless requires the paged list call; see AzureSearchClientFactory.
            await foreach (var existingIndex in searchIndexClient.GetIndexesAsync(top: 1000))
            {
                indexNames.Add(existingIndex.Name);
            }
        }
        else
        {
            await foreach (var existingIndexName in searchIndexClient.GetIndexNamesAsync())
            {
                indexNames.Add(existingIndexName);
            }
        }
        if (indexNames.Contains(indexAlias))
            return;
        var newIndex = new SearchIndex(indexAlias, [
            new SearchField(IndexConstants.FieldNames.Id,  SearchFieldDataType.String)
            {
                IsKey = true
            },
            new SearchField(IndexConstants.FieldNames.Key, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsSortable = true
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
                IsFilterable = false,
                IsFacetable = false
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR1, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR2, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR3, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false
            }
        ]);

        // Add scoring profile to boost text relevance fields (R1 > R2 > R3 > base)
        var scoringProfile = new ScoringProfile("relevanceBoost")
        {
            TextWeights = new TextWeights(new Dictionary<string, double>
            {
                { IndexConstants.FieldNames.AllTextsR1, 4.0 },
                { IndexConstants.FieldNames.AllTextsR2, 3.0 },
                { IndexConstants.FieldNames.AllTextsR3, 2.0 },
                { IndexConstants.FieldNames.AllTexts, 1.0 }
            })
        };
        newIndex.ScoringProfiles.Add(scoringProfile);
        newIndex.DefaultScoringProfile = "relevanceBoost";

        await searchIndexClient.CreateIndexAsync(newIndex);
    }

    public async Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var index = await indexClient.GetIndexAsync(indexAlias);
        index.ThrowIfNull();
        await indexClient.DeleteIndexAsync(index);
        await indexClient.CreateIndexAsync(index);
    }
}