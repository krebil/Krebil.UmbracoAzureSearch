using Umbraco.Cms.Core;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherOrderingTests
{
    private const string IndexAlias = "unit-test-index";

    [Test]
    public async Task OrdersByScoreThenIdWithoutSorters()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test");

        Assert.That(service.LastOrderBy(), Is.EqualTo("search.score() desc,id asc"));
    }

    [Test]
    public async Task OrdersByIdAfterRequestedSorters()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test",
            sorters: [new KeywordSorter("name", Direction.Ascending), new IntegerSorter("level", Direction.Descending)]);

        Assert.That(service.LastOrderBy(), Is.EqualTo("name_keywords_sort asc,level_integers_sort desc,id asc"));
    }

    [Test]
    public async Task CanFindAllWithOnlyCulture()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, culture: "en-US");

        Assert.That(service.LastSearchBody().GetProperty("search").GetString(), Is.EqualTo("*"));
    }

    [Test]
    public async Task CanFindAllWithEmptyCriteria()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, filters: [], sorters: []);

        Assert.That(service.LastSearchBody().GetProperty("search").GetString(), Is.EqualTo("*"));
    }

    [Test]
    public async Task FindsNothingWithoutAnyCriteria()
    {
        var service = new FakeSearchService();

        var result = await service.CreateSearcher().SearchAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(result.Total, Is.Zero);
            Assert.That(service.Requests, Is.Empty);
        });
    }
}
