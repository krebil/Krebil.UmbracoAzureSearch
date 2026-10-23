using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Throw;
using Umbraco.Cms.Core.Sync;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Services.IndexManager;

public class AzureSearchIndexManager(
    IServerRoleAccessor serverRoleAccessor,
    IIndexAliasResolver indexAliasResolver,
    IAzureSearchClientFactory azureSearchClientFactory,
    IOptions<UmbracoAzureSearchOptions> azureSearchOptions,
    IOptionsMonitor<AzureSearchIndexOptions> azureSearchIndexOptions,
    ILogger<AzureSearchIndexManager> logger)
    : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchIndexManager
{
    public async Task EnsureAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        var umbracoIndexAlias = indexAlias;
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
        var definition = new IndexDefinition(azureSearchIndexOptions.Get(umbracoIndexAlias));
        if (indexNames.Contains(indexAlias))
        {
            await UpdateExistingIndexAsync(searchIndexClient, indexAlias, definition);
            return;
        }
        var newIndex = BuildIndex(indexAlias, definition);
        await searchIndexClient.CreateIndexAsync(newIndex);
    }

    public async Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var index = await indexClient.GetIndexAsync(indexAlias);
        index.ThrowIfNull();
        await indexClient.DeleteIndexAsync(index);
        // Recreate from code, not from the old definition, so a rebuild also applies schema changes such as
        // suggestions. Property fields come back as the rebuild indexes documents (see EnsureFieldsExist).
        await indexClient.CreateIndexAsync(BuildIndex(indexAlias, definition));
    }

    // An existing index gets the fields its definition adds without a rebuild: suggestions on indexes created before
    // suggestions existed, and the aggregate copies of a new culture. Their documents fill on rebuild.
    private async Task UpdateExistingIndexAsync(SearchIndexClient searchIndexClient, string indexName, IndexDefinition definition)
    {
        SearchIndex index = await searchIndexClient.GetIndexAsync(indexName);
        var addedSuggestions = definition.Options.Suggestions.Enabled && TryAddSuggestions(index, indexName, definition);
        var addedCopies = IndexSchema.AddMissingCopies(index, definition);
        if (!addedSuggestions && !addedCopies)
            return;

        await searchIndexClient.CreateOrUpdateIndexAsync(index);
        logger.LogWarning(
            "Added {Fields} to index {IndexName}. Rebuild the index to fill them for existing content.",
            addedSuggestions ? "suggestions" : "per-culture text fields", indexName);
    }

    private bool TryAddSuggestions(SearchIndex index, string indexName, IndexDefinition definition)
    {
        if (index.Fields.Any(f => f.Name == IndexConstants.FieldNames.Suggestion))
            return false;

        // Azure allows one suggester per index; never replace one we did not create.
        if (index.Suggesters.Count > 0)
        {
            logger.LogWarning(
                "Index {IndexName} already has a suggester, so suggestions cannot be added. Remove it or turn suggestions off for this index.",
                indexName);
            return false;
        }

        IndexSchema.AddSuggestions(index, definition);
        return true;
    }

    // The index definition in code; EnsureAsync and ResetAsync both create indexes from it.
    private static SearchIndex BuildIndex(string indexName, IndexDefinition definition)
    {
        var newIndex = new SearchIndex(indexName, [
            new SearchField(IndexConstants.FieldNames.Id,  SearchFieldDataType.String)
            {
                IsKey = true,
                IsSortable = true
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

        foreach (var copy in IndexSchema.CopyFields(definition))
        {
            newIndex.Fields.Add(copy);
        }

        if (definition.Options.Suggestions.Enabled)
        {
            IndexSchema.AddSuggestions(newIndex, definition);
        }

        // Scoring profile to boost text relevance fields (R1 > R2 > R3 > base), copies included
        newIndex.ScoringProfiles.Add(IndexSchema.RelevanceProfile(newIndex.Fields.Select(f => f.Name)));
        newIndex.DefaultScoringProfile = IndexSchema.RelevanceProfileName;

        return newIndex;
    }
}
