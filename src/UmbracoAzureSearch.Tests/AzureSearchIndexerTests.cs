using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using UmbracoAzureSearch.Services;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests;

public class AzureSearchIndexerTests : AzureSearchTestBase
{
    private const string IndexAlias = "someindex";

    protected override async Task PerformOneTimeSetUpAsync()
        => await DeleteIndex(IndexAlias);

    protected override async Task PerformOneTimeTearDownAsync()
        => await DeleteIndex(IndexAlias);

    [Test]
    public async Task CanCreateAndResetIndex()
    {
        await IndexManager.EnsureAsync(IndexAlias);

        var client = GetRequiredService<IAzureSearchClientFactory>().GetSearchIndexClient();
        Dictionary<string, Guid> ids = await CreateIndexStructure();

        SearchResult result = await SearchAsync(
            filters:
            [
                new KeywordFilter(
                    Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                    [ids.First().Value.AsKeyword()],
                    false
                )
            ]
        );
        Assert.That(result.Total, Is.Not.Zero);

        var existsResponse = await client.GetIndexAsync(IndexAlias);
        Assert.That(existsResponse.HasValue, Is.True);

        await Indexer.ResetAsync(IndexAlias);

        existsResponse = await client.GetIndexAsync(IndexAlias);
        Assert.That(existsResponse.HasValue, Is.True);

        result = await SearchAsync(
            filters:
            [
                new KeywordFilter(
                    Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                    [ids.First().Value.AsKeyword()],
                    false
                )
            ]
        );
        Assert.That(result.Total, Is.Zero);
    }

    [Test]
    public async Task CanDeleteRootDocuments()
    {
        await IndexManager.EnsureAsync(IndexAlias);

        Dictionary<string, Guid> ids = await CreateIndexStructure();

        await Indexer.DeleteAsync(IndexAlias, [ids["0:root"], ids["2:root"]]);

        await WaitForIndexingOperationsToCompleteAsync();

        for (var i = 0; i < 3; i++)
        {
            SearchResult result = await SearchAsync(
                filters:
                [
                    new KeywordFilter(
                        Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                        [ids[$"{i}:root"].AsKeyword()],
                        false
                    )
                ]
            );

            Assert.That(result.Total, Is.EqualTo(i == 1 ? 3 : 0));
        }

        await Indexer.ResetAsync(IndexAlias);
    }

    [Test]
    public async Task CanDeleteDescendantDocuments()
    {
        await IndexManager.EnsureAsync(IndexAlias);

        Dictionary<string, Guid> ids = await CreateIndexStructure();


        await Indexer.DeleteAsync(IndexAlias, [ids["1:child"], ids["2:grandchild"]]);

        await WaitForIndexingOperationsToCompleteAsync();

        for (var i = 0; i < 3; i++)
        {
            SearchResult result = await SearchAsync(
                filters:
                [
                    new KeywordFilter(
                        Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                        [ids[$"{i}:root"].AsKeyword()],
                        false
                    )
                ]
            );

            Assert.That(
                result.Total,
                Is.EqualTo(
                    i switch
                    {
                        0 => 3, // all documents should still be there
                        1 => 1, // child and grandchild should be deleted
                        2 => 2 // grandchild should be deleted
                    }
                )
            );
        }

        await Indexer.ResetAsync(IndexAlias);
    }

    [Test]
    public async Task ReindexingRemovesFieldsTheContentNoLongerHas()
    {
        await IndexManager.EnsureAsync(IndexAlias);

        var id = Guid.NewGuid();
        var universe = Guid.NewGuid().AsKeyword();
        IndexField pathIds = new(
            Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
            new IndexValue { Keywords = [id.AsKeyword()] },
            Culture: null,
            Segment: null);

        await Indexer.AddOrUpdateAsync(
            IndexAlias,
            id,
            UmbracoObjectTypes.Unknown,
            [new Variation(Culture: null, Segment: null)],
            [pathIds, new IndexField("universe", new IndexValue { Keywords = [universe] }, Culture: null, Segment: null)],
            null);

        await WaitForIndexingOperationsToCompleteAsync();

        SearchResult result = await SearchAsync(filters: [new KeywordFilter("universe", [universe], false)]);
        Assert.That(result.Total, Is.EqualTo(1));

        // The editor clears the value, so the field is no longer part of the indexed fields.
        await Indexer.AddOrUpdateAsync(
            IndexAlias,
            id,
            UmbracoObjectTypes.Unknown,
            [new Variation(Culture: null, Segment: null)],
            [pathIds],
            null);

        await WaitForIndexingOperationsToCompleteAsync();

        result = await SearchAsync(filters: [new KeywordFilter("universe", [universe], false)]);
        Assert.That(result.Total, Is.Zero);

        await Indexer.ResetAsync(IndexAlias);
    }

    [Test]
    public async Task ReindexingRemovesVariationsTheContentNoLongerHas()
    {
        await IndexManager.EnsureAsync(IndexAlias);

        var id = Guid.NewGuid();
        Filter[] byId =
        [
            new KeywordFilter(Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds, [id.AsKeyword()], false)
        ];

        await IndexCulturesAsync(id, "en-US", "da-DK");
        await WaitForIndexingOperationsToCompleteAsync();

        Assert.That((await SearchAsync(filters: byId, culture: "da-DK")).Total, Is.EqualTo(1));

        // The da-DK culture is unpublished, so the next reindex only holds en-US.
        await IndexCulturesAsync(id, "en-US");
        await WaitForIndexingOperationsToCompleteAsync();

        SearchResult danish = await SearchAsync(filters: byId, culture: "da-DK");
        SearchResult english = await SearchAsync(filters: byId, culture: "en-US");
        Assert.Multiple(() =>
        {
            Assert.That(danish.Total, Is.Zero);
            Assert.That(english.Total, Is.EqualTo(1));
        });

        await Indexer.ResetAsync(IndexAlias);
    }

    // A write deletes the documents of the variations the content does not have, whether they exist or not. Azure
    // accepts the delete of a missing document as a success.
    [Test]
    public async Task DeletingADocumentThatDoesNotExistSucceeds()
    {
        await IndexManager.EnsureAsync(IndexAlias);
        var searchClient = GetRequiredService<IAzureSearchClientFactory>().GetSearchIndexClient()
            .GetSearchClient(GetRequiredService<UmbracoAzureSearch.Services.IndexAliasResolver.IIndexAliasResolver>().Resolve(IndexAlias));

        var results = await GetRequiredService<DocumentWriter>().DeleteAsync(searchClient, [$"{Guid.NewGuid():N}_inv_def"]);

        Assert.Multiple(() =>
        {
            Assert.That(results.Single().Succeeded, Is.True);
            Assert.That(results.Single().Status, Is.EqualTo(200));
        });
    }

    private async Task IndexCulturesAsync(Guid id, params string[] cultures)
        => await Indexer.AddOrUpdateAsync(
            IndexAlias,
            id,
            UmbracoObjectTypes.Unknown,
            cultures.Select(culture => new Variation(Culture: culture, Segment: null)).ToArray(),
            [
                new IndexField(
                    Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                    new IndexValue { Keywords = [id.AsKeyword()] },
                    Culture: null,
                    Segment: null)
            ],
            null);

    private async Task<Dictionary<string, Guid>> CreateIndexStructure()
    {
        var ids = new Dictionary<string, Guid>();
        for (var i = 0; i < 3; i++)
        {
            var rootId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var grandchildId = Guid.NewGuid();
            ids.Add($"{i}:root", rootId);
            ids.Add($"{i}:child", childId);
            ids.Add($"{i}:grandchild", grandchildId);

            await Indexer.AddOrUpdateAsync(
                IndexAlias,
                rootId,
                UmbracoObjectTypes.Unknown,
                [new Variation(Culture: null, Segment: null)],
                [
                    new IndexField(
                        Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                        new IndexValue { Keywords = [rootId.AsKeyword()] },
                        Culture: null,
                        Segment: null
                    )
                ],
                null
            );

            await Indexer.AddOrUpdateAsync(
                IndexAlias,
                childId,
                UmbracoObjectTypes.Unknown,
                [new Variation(Culture: null, Segment: null)],
                [
                    new IndexField(
                        Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                        new IndexValue { Keywords = [rootId.AsKeyword(), childId.AsKeyword()] },
                        Culture: null,
                        Segment: null
                    )
                ],
                null
            );

            await Indexer.AddOrUpdateAsync(
                IndexAlias,
                grandchildId,
                UmbracoObjectTypes.Unknown,
                [new Variation(Culture: null, Segment: null)],
                [
                    new IndexField(
                        Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                        new IndexValue
                            { Keywords = [rootId.AsKeyword(), childId.AsKeyword(), grandchildId.AsKeyword()] },
                        Culture: null,
                        Segment: null
                    )
                ],
                null
            );
        }

        await WaitForIndexingOperationsToCompleteAsync();

        for (var i = 0; i < 3; i++)
        {
            SearchResult result = await SearchAsync(
                filters:
                [
                    new KeywordFilter(
                        Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds,
                        [ids[$"{i}:root"].AsKeyword()],
                        false
                    )
                ]
            );

            Assert.That(result.Total, Is.EqualTo(3));
        }

        return ids;
    }


    private IAzureSearchSearcher Searcher => GetRequiredService<IAzureSearchSearcher>();

    private IAzureSearchIndexer Indexer => GetRequiredService<IAzureSearchIndexer>();
    
    private IAzureSearchIndexManager IndexManager => GetRequiredService<IAzureSearchIndexManager>();


    private async Task<SearchResult> SearchAsync(
        string? query = null,
        IEnumerable<Filter>? filters = null,
        IEnumerable<Facet>? facets = null,
        IEnumerable<Sorter>? sorters = null,
        string? culture = null,
        string? segment = null,
        AccessContext? accessContext = null,
        int skip = 0,
        int take = 100)
    {
        var result = await Searcher.SearchAsync(IndexAlias,query,filters, facets, sorters, culture, segment, accessContext, skip, take);
        Assert.That(result, Is.Not.Null);
        return result;
    }
}