using System.Text.Json;
using Umbraco.Cms.Core;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherHardeningTests
{
    private const string IndexAlias = "unit-test-index";

    [Test]
    public async Task AQuoteInTheCultureCannotEndTheClause()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test", culture: "x') or true or ('a' eq 'a");

        Assert.That(service.LastFilter(), Does.Contain("x'') or true or (''a'' eq ''a"));
    }

    [Test]
    public async Task AQuoteInTheSegmentCannotEndTheClause()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test", segment: "x') or true or ('a' eq 'a");

        Assert.That(service.LastFilter(), Does.Contain("x'') or true or (''a'' eq ''a"));
    }

    // A filter on a faceted field moves to a second request for the facet counts; it must be protected too.
    [Test]
    public async Task TheFacetCountRequestIsAccessFilteredToo()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            filters: [new KeywordFilter("genre", ["rock"], false)],
            facets: [new KeywordFacet("genre")]);

        var filters = service.Requests
            .Where(r => r.Path.Contains("search.post.search"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .Select(body => body.TryGetProperty("filter", out var filter) ? filter.GetString() : null)
            .ToArray();
        Assert.That(filters, Has.Length.EqualTo(2));
        Assert.That(filters, Has.All.Contain("accessKeys/any("));
    }

    [Test]
    public async Task ADecimalBeyondInt64IsADoubleLiteral()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            filters: [new DecimalExactFilter("price", [12345678901234567890m], false), DecimalRangeFilter.Single("weight", null, 12345678901234567890m, false)]);

        Assert.That(service.LastFilter(), Does.Contain("price_decimals/any(f: f eq 12345678901234567890.0)"));
        Assert.That(service.LastFilter(), Does.Contain("weight_decimals/any(f: f lt 12345678901234567890.0)"));
    }

    [Test]
    public async Task SortersStopShortOfAzuresLimitToLeaveRoomForTheTieBreaker()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            sorters: Enumerable.Range(0, 40).Select(i => (Sorter)new IntegerSorter($"field{i}", Direction.Ascending)).ToArray());

        var clauses = service.LastOrderBy()!.Split(',');
        Assert.That(clauses, Has.Length.EqualTo(32));
        Assert.That(clauses.Last(), Is.EqualTo("id asc"));
    }
}
