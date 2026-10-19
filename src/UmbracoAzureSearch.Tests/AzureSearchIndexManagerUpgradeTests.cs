using Azure.Search.Documents.Indexes.Models;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests;

public class AzureSearchIndexManagerUpgradeTests : AzureSearchTestBase
{
    // Unique per run: Azure deletes indexes asynchronously, so a name from the previous run may still be in use.
    private static readonly string IndexAlias = $"upgradeindex{Guid.NewGuid():N}"[..24];

    private string IndexName => GetRequiredService<IIndexAliasResolver>().Resolve(IndexAlias);

    protected override async Task PerformOneTimeTearDownAsync()
        => await DeleteIndex(IndexName);

    [Test]
    public async Task EnsureAddsSuggestionsToAnIndexCreatedWithoutThem()
    {
        var indexClient = GetRequiredService<IAzureSearchClientFactory>().GetSearchIndexClient();
        await DeleteIndex(IndexName);
        await indexClient.CreateIndexAsync(new SearchIndex(IndexName, [
            new SimpleField(IndexConstants.FieldNames.Id, SearchFieldDataType.String) { IsKey = true },
            new SearchField(IndexConstants.FieldNames.AllTextsR1, SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsSearchable = true }
        ]));

        await GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        SearchIndex index = await indexClient.GetIndexAsync(IndexName);
        Assert.Multiple(() =>
        {
            Assert.That(index.Fields.Select(f => f.Name), Does.Contain(IndexConstants.FieldNames.Suggestion));
            Assert.That(index.Suggesters.Single().Name, Is.EqualTo(IndexConstants.SuggesterName));
            Assert.That(index.Suggesters.Single().SourceFields, Is.EqualTo(new[] { IndexConstants.FieldNames.Suggestion }));
        });
    }
}
