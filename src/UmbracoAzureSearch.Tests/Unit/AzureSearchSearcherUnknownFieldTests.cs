using Azure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;

namespace UmbracoAzureSearch.Tests.Unit;

// Azure has no field for a value no document ever had (a fresh index, a property no page has filled in yet), where
// Examine has a field with no values. The searcher must answer such a query as Examine does, not with a 400.
[TestFixture]
public class AzureSearchSearcherUnknownFieldTests
{
    private const string IndexAlias = "unit-test-index";

    [Test]
    public async Task ASortOnAFieldNoDocumentHasIsDropped()
    {
        var service = new FakeSearchService { UnknownField = "publishDate_datetimeoffsets_sort" };

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "test",
            sorters: [new DateTimeOffsetSorter("publishDate", Direction.Descending), new KeywordSorter("name", Direction.Ascending)]);

        Assert.That(result.Total, Is.EqualTo(0));
        Assert.That(SearchRequests(service), Is.EqualTo(2));
        Assert.That(service.LastOrderBy(), Does.Not.Contain("publishDate"));
        Assert.That(service.LastOrderBy(), Does.Contain("name_keywords_sort asc"));
    }

    [Test]
    public async Task AFilterOnAFieldNoDocumentHasFindsNothing()
    {
        var service = new FakeSearchService { UnknownField = "topic_keywords" };

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "test",
            filters: [new KeywordFilter("topic", ["mna:can"], false)]);

        Assert.That(result.Total, Is.EqualTo(0));
        Assert.That(result.Documents, Is.Empty);
        // Nothing can match, so there is no second request.
        Assert.That(SearchRequests(service), Is.EqualTo(1));
    }

    [Test]
    public async Task ANegatedFilterOnAFieldNoDocumentHasIsDropped()
    {
        var service = new FakeSearchService { UnknownField = "topic_keywords" };

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test",
            filters: [new KeywordFilter("topic", ["mna:can"], true), new KeywordFilter("genre", ["rock"], false)]);

        Assert.That(SearchRequests(service), Is.EqualTo(2));
        Assert.That(service.LastFilter(), Does.Not.Contain("topic_keywords"));
        Assert.That(service.LastFilter(), Does.Contain("genre_keywords"));
    }

    [Test]
    public async Task AFacetOnAFieldNoDocumentHasIsDropped()
    {
        var service = new FakeSearchService { UnknownField = "genre_keywords" };

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test",
            facets: [new KeywordFacet("genre"), new KeywordFacet("year")]);

        Assert.That(SearchRequests(service), Is.EqualTo(2));
        var lastBody = service.Requests.Last(r => r.Path.Contains("search.post.search")).Body!;
        Assert.That(lastBody, Does.Not.Contain("genre_keywords"));
        Assert.That(lastBody, Does.Contain("year_keywords"));
    }

    [Test]
    public void AnErrorAboutAFieldTheSearchDidNotAskForIsNotSwallowed()
    {
        var service = new FakeSearchService { UnknownField = "other_keywords", UnknownFieldAlways = true };

        Assert.ThrowsAsync<RequestFailedException>(() => service.CreateSearcher().SearchAsync(IndexAlias, query: "test",
            filters: [new KeywordFilter("genre", ["rock"], false)]));
        // Once more without a field it never used, then given up.
        Assert.That(SearchRequests(service), Is.EqualTo(2));
    }

    private static int SearchRequests(FakeSearchService service) => service.Requests.Count(r => r.Path.Contains("search.post.search"));
}
