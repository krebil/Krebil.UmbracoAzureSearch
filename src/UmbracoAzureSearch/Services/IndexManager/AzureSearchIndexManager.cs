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

namespace UmbracoAzureSearch.Services.IndexManager;

public class AzureSearchIndexManager(
    IServerRoleAccessor serverRoleAccessor,
    IIndexAliasResolver indexAliasResolver,
    IAzureSearchClientFactory azureSearchClientFactory,
    IOptions<UmbracoAzureSearchOptions> azureSearchOptions,
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
            await foreach (var existingIndex in searchIndexClient.GetIndexesAsync(pageSize: 1000))
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
        var suggestionOptions = azureSearchOptions.Value.GetSuggestionOptions(umbracoIndexAlias);
        if (indexNames.Contains(indexAlias))
        {
            if (suggestionOptions.Enabled)
            {
                await AddSuggestionsToExistingIndexAsync(searchIndexClient, indexAlias);
            }
            return;
        }
        var newIndex = BuildIndex(indexAlias, suggestionOptions);
        await searchIndexClient.CreateIndexAsync(newIndex);
    }

    public async Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        var suggestionOptions = azureSearchOptions.Value.GetSuggestionOptions(indexAlias);
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var index = await indexClient.GetIndexAsync(indexAlias);
        index.ThrowIfNull();
        await indexClient.DeleteIndexAsync(index);
        // Recreate from code, not from the old definition, so a rebuild also applies schema changes such as
        // suggestions. Property fields come back as the rebuild indexes documents (see EnsureFieldsExist).
        await indexClient.CreateIndexAsync(BuildIndex(indexAlias, suggestionOptions));
    }

    // Indexes created before suggestions existed get the field and suggester in place; their documents fill on rebuild.
    private async Task AddSuggestionsToExistingIndexAsync(SearchIndexClient searchIndexClient, string indexName)
    {
        SearchIndex index = await searchIndexClient.GetIndexAsync(indexName);
        if (index.Fields.Any(f => f.Name == IndexConstants.FieldNames.Suggestion))
            return;

        // Azure allows one suggester per index; never replace one we did not create.
        if (index.Suggesters.Count > 0)
        {
            logger.LogWarning(
                "Index {IndexName} already has a suggester, so suggestions cannot be added. Remove it or turn suggestions off for this index.",
                indexName);
            return;
        }

        AddSuggestions(index);
        await searchIndexClient.CreateOrUpdateIndexAsync(index);
        logger.LogWarning(
            "Added suggestions to index {IndexName}. Rebuild the index to fill them for existing content.",
            indexName);
    }

    // The index definition in code; EnsureAsync and ResetAsync both create indexes from it.
    private static SearchIndex BuildIndex(string indexName, SuggestionOptions suggestionOptions)
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

        if (suggestionOptions.Enabled)
        {
            AddSuggestions(newIndex);
        }

        return newIndex;
    }

    // Azure only allows a suggester on fields added in the same request, so the field and suggester always go together.
    private static void AddSuggestions(SearchIndex index)
    {
        index.Fields.Add(new SearchField(IndexConstants.FieldNames.Suggestion, SearchFieldDataType.String)
        {
            IsSearchable = true,
            IsFilterable = false,
            IsFacetable = false,
            AnalyzerName = LexicalAnalyzerName.StandardLucene
        });
        index.Suggesters.Add(new SearchSuggester(IndexConstants.SuggesterName, IndexConstants.FieldNames.Suggestion));
    }
}
