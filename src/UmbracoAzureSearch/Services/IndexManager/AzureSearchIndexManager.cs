using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Throw;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
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
    private readonly IndexSlots slots = serviceProvider.GetRequiredService<IndexSlots>();

    public async Task EnsureAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
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
        var definition = new IndexDefinition(azureSearchIndexOptions.Get(indexAlias));
        var indexes = await physicalIndexes.AllAsync(indexAlias, definition);
        var slotted = slots.Enabled(definition);
        foreach (var physicalIndex in indexes)
        {
            var physicalDefinition = definition.For(physicalIndex.Analyzer);
            var name = physicalIndex.Name;
            var aliasTarget = slotted ? await slots.AliasTargetAsync(name) : null;
            if (slotted)
            {
                await DeleteHalfBuiltSlotsAsync(searchIndexClient, indexAlias, name, aliasTarget, indexNames);
            }

            var live = aliasTarget ?? (indexNames.Contains(name) ? name : null);
            if (live is not null)
            {
                await UpdateExistingIndexAsync(searchIndexClient, physicalIndex with { Name = live }, indexAlias, physicalDefinition);
                continue;
            }

            // A new index. With zero-downtime rebuild it is slot a behind an alias with the name.
            var newName = slotted ? IndexSlots.Slot(name, 'a') : name;
            await searchIndexClient.CreateIndexAsync(IndexSchema.Build(physicalIndex with { Name = newName }, indexAlias, physicalDefinition));
            if (slotted)
            {
                await searchIndexClient.CreateOrUpdateAliasAsync(new SearchAlias(name, newName));
                slots.Forget(name);
            }

            // A culture index added next to existing indexes of the alias is new because of a new culture in the map, or
            // (the fallback index) a new Umbraco language outside the map. Existing content is not in it yet.
            if (definition.IndexPerCulture && indexes.Any(other => other != physicalIndex && indexNames.Any(n => n == other.Name || n.StartsWith($"{other.Name}-", StringComparison.Ordinal))))
            {
                logger.LogWarning(
                    "Rebuild required for index {IndexAlias}: {Change}",
                    indexAlias,
                    physicalIndex.IsFallback
                        ? $"Umbraco has a language outside the culture map, so the fallback index {name} was created. Rebuild the index to fill it."
                        : $"the culture {physicalIndex.Culture} is new in the culture map, so the index {name} was created. Rebuild the index to fill it.");
            }
        }
    }

    // A crash during a rebuild leaves the new slot behind. The live slot is the one the alias points to; without an alias
    // the plain index is live and both slots are leftovers of a first migration.
    private async Task DeleteHalfBuiltSlotsAsync(
        SearchIndexClient searchIndexClient, string indexAlias, string name, string? aliasTarget, HashSet<string> indexNames)
    {
        foreach (var slot in new[] { IndexSlots.Slot(name, 'a'), IndexSlots.Slot(name, 'b') })
        {
            if (slot == aliasTarget || !indexNames.Contains(slot))
                continue;

            await searchIndexClient.DeleteIndexAsync(slot, CancellationToken.None);
            indexNames.Remove(slot);
            logger.LogWarning("Deleted the half-built index {NewIndex} of {IndexAlias}, left by a rebuild that did not finish", slot, indexAlias);
        }
    }

    public async Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
            return;
        var indexClient = azureSearchClientFactory.GetSearchIndexClient();
        var options = azureSearchIndexOptions.Get(indexAlias);
        var definition = new IndexDefinition(options);
        await ClearDocumentCacheAsync(indexAlias, options);

        // A rebuild that failed earlier left its state behind; a new rebuild replaces it.
        slots.Take(indexAlias);

        // All indexes of one alias rebuild together.
        var indexes = await physicalIndexes.AllAsync(indexAlias, definition);
        if (slots.Enabled(definition))
        {
            var rebuilds = new List<SlotRebuild>();
            foreach (var physicalIndex in indexes)
            {
                rebuilds.Add(await StartSlotRebuildAsync(indexClient, indexAlias, physicalIndex, definition.For(physicalIndex.Analyzer)));
            }

            slots.Start(indexAlias, rebuilds.ToArray());
            return;
        }

        foreach (var physicalIndex in indexes)
        {
            // An index that used zero-downtime rebuild before has an alias and slots; the plain index replaces them.
            if (await slots.AliasTargetAsync(physicalIndex.Name) is not null)
            {
                await DeleteQuietlyAsync(() => indexClient.DeleteAliasAsync(physicalIndex.Name));
                await DeleteQuietlyAsync(() => indexClient.DeleteIndexAsync(IndexSlots.Slot(physicalIndex.Name, 'a'), CancellationToken.None));
                await DeleteQuietlyAsync(() => indexClient.DeleteIndexAsync(IndexSlots.Slot(physicalIndex.Name, 'b'), CancellationToken.None));
                slots.Forget(physicalIndex.Name);
            }

            await DeleteQuietlyAsync(() => indexClient.DeleteIndexAsync(physicalIndex.Name, CancellationToken.None));

            // Recreate from code, not from the old definition, so a rebuild also applies schema changes such as
            // suggestions. Property fields come back as the rebuild indexes documents (see EnsureFieldsExist).
            await indexClient.CreateIndexAsync(IndexSchema.Build(physicalIndex, indexAlias, definition.For(physicalIndex.Analyzer)));
        }
    }

    // Zero-downtime rebuild: create the inactive slot from the current definition. Searches keep using the live index
    // until the swap (see ZeroDowntimeRebuildHandler).
    private async Task<SlotRebuild> StartSlotRebuildAsync(
        SearchIndexClient indexClient, string indexAlias, PhysicalIndex physicalIndex, IndexDefinition definition)
    {
        var name = physicalIndex.Name;
        var aliasTarget = await slots.AliasTargetAsync(name);
        var old = aliasTarget;
        var oldIsPlainIndex = false;
        if (old is null && await ExistsAsync(indexClient, name))
        {
            old = name;
            oldIsPlainIndex = true;
        }

        var newName = IndexSlots.Inactive(name, aliasTarget);
        await DeleteQuietlyAsync(() => indexClient.DeleteIndexAsync(newName, CancellationToken.None));
        await indexClient.CreateIndexAsync(IndexSchema.Build(physicalIndex with { Name = newName }, indexAlias, definition));

        var started = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "Rebuild started for {IndexAlias}: filling {NewIndex} while searches use {OldIndex}, at {Started}",
            indexAlias, newName, old ?? "(none)", started);
        return new SlotRebuild(name, old, newName, oldIsPlainIndex, started);
    }

    private async Task ClearDocumentCacheAsync(string indexAlias, AzureSearchIndexOptions options)
    {
        if (!options.ClearDocumentCacheOnRebuild || serviceProvider.GetService<IIndexDocumentService>() is not { } indexDocumentService)
            return;

        await indexDocumentService.DeleteAllAsync();
        logger.LogInformation("Cleared the cached index documents for the rebuild of {IndexAlias}", indexAlias);
    }

    private static async Task<bool> ExistsAsync(SearchIndexClient indexClient, string indexName)
    {
        try
        {
            await indexClient.GetIndexAsync(indexName);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private static async Task DeleteQuietlyAsync(Func<Task> delete)
    {
        try
        {
            await delete();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Nothing to delete.
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

        // Definition and ConfigureIndex changes that Azure applies to a live index: fields, scoring profiles, the default profile.
        var desired = IndexSchema.Build(physicalIndex, indexAlias, definition);
        var appliedChanges = IndexSchema.ApplyLiveChanges(index, desired);

        // Changes Azure cannot apply to a live index wait for a rebuild.
        foreach (var change in IndexSchema.RebuildRequired(index, desired, definition))
        {
            logger.LogWarning("Rebuild required for index {IndexAlias}: {Change}", indexAlias, $"{change} ({indexName})");
        }

        if (!addedSuggestions && !addedCopies && !appliedChanges)
            return;

        await searchIndexClient.CreateOrUpdateIndexAsync(index);
        if (addedSuggestions || addedCopies)
        {
            logger.LogWarning(
                "Added {Fields} to index {IndexName}. Rebuild the index to fill them for existing content.",
                addedSuggestions ? "suggestions" : "per-culture text fields", indexName);
        }

        if (appliedChanges)
        {
            logger.LogInformation("Applied the definition changes of {IndexAlias} to index {IndexName}", indexAlias, indexName);
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
