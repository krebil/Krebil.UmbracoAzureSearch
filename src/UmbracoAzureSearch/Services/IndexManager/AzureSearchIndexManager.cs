using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.DependencyInjection;
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
    ILogger<AzureSearchIndexManager> logger,
    IServiceProvider serviceProvider)
    : UmbracoAzureServiceBase(serverRoleAccessor), IAzureSearchIndexManager
{
    private readonly PhysicalIndexes physicalIndexes = serviceProvider.GetRequiredService<PhysicalIndexes>();

    public async Task EnsureAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
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
        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
        foreach (var physicalIndex in await physicalIndexes.AllAsync(indexAlias, definition))
        {
            var physicalDefinition = definition.For(physicalIndex.Analyzer);
            if (indexNames.Contains(physicalIndex.Name))
            {
                await UpdateExistingIndexAsync(searchIndexClient, physicalIndex, indexAlias, physicalDefinition);
                continue;
            }

            await searchIndexClient.CreateIndexAsync(IndexSchema.Build(physicalIndex, indexAlias, physicalDefinition));

            // A fallback index added next to existing culture indexes is new because Umbraco got a language outside the map.
            if (definition.IndexPerCulture && physicalIndex == physicalIndexes.Fallback(indexAlias)
                && indexNames.Any(name => name.StartsWith($"{indexAliasResolver.Resolve(indexAlias)}-", StringComparison.Ordinal)))
            {
                logger.LogWarning(
                    "Rebuild required for index {IndexAlias}: Umbraco has a language outside the culture map, so the fallback index {IndexName} was created. Rebuild the index to fill it.",
                    indexAlias, physicalIndex.Name);
            }
        }
    }

    public async Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));

        // All indexes of one alias rebuild together.
        foreach (var physicalIndex in await physicalIndexes.AllAsync(indexAlias, definition))
        {
            try
            {
                await indexClient.DeleteIndexAsync(physicalIndex.Name, CancellationToken.None);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // A fallback index that did not exist yet.
            }

            // Recreate from code, not from the old definition, so a rebuild also applies schema changes such as
            // suggestions. Property fields come back as the rebuild indexes documents (see EnsureFieldsExist).
            await indexClient.CreateIndexAsync(IndexSchema.Build(physicalIndex, indexAlias, definition.For(physicalIndex.Analyzer)));
        }
    }

    // An existing index gets the fields its definition adds without a rebuild: suggestions on indexes created before
    // suggestions existed, and the aggregate copies of a new culture. Their documents fill on rebuild.
    private async Task UpdateExistingIndexAsync(
        SearchIndexClient searchIndexClient, PhysicalIndex physicalIndex, string indexAlias, IndexDefinition definition)
    {
        var indexName = physicalIndex.Name;
        SearchIndex index = await searchIndexClient.GetIndexAsync(indexName);
        var addedSuggestions = definition.Options.Suggestions.Enabled && TryAddSuggestions(index, indexName, definition);
        var addedCopies = IndexSchema.AddMissingCopies(index, definition);

        // ConfigureIndex changes that Azure applies to a live index: fields, scoring profiles, the default profile.
        var appliedCallbacks = definition.Options.IndexConfigurations.Count > 0
            && IndexSchema.ApplyLiveChanges(index, IndexSchema.Build(physicalIndex, indexAlias, definition));
        if (!addedSuggestions && !addedCopies && !appliedCallbacks)
            return;

        await searchIndexClient.CreateOrUpdateIndexAsync(index);
        if (addedSuggestions || addedCopies)
        {
            logger.LogWarning(
                "Added {Fields} to index {IndexName}. Rebuild the index to fill them for existing content.",
                addedSuggestions ? "suggestions" : "per-culture text fields", indexName);
        }

        if (appliedCallbacks)
        {
            logger.LogInformation("Applied the ConfigureIndex changes of {IndexAlias} to index {IndexName}", indexAlias, indexName);
        }
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
}
