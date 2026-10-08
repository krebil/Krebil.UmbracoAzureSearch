using Umbraco.Cms.Search.Core.Models.Searching.Filtering;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherRangeFilterTests
{
    private const string IndexAlias = "unit-test-index";

    [Test]
    public async Task CanFilterDecimalRangeWithBothBounds()
    {
        var filter = await SearchWith(DecimalRangeFilter.Single("price", 1.5m, 10m, false));

        Assert.That(filter, Does.Contain("(price_decimals/any(f: f ge 1.5 and f lt 10.0))"));
    }

    [Test]
    public async Task CanFilterDecimalRangeWithoutMax()
    {
        var filter = await SearchWith(DecimalRangeFilter.Single("price", 1.001m, null, false));

        Assert.That(filter, Does.Contain("(price_decimals/any(f: f ge 1.001))"));
    }

    [Test]
    public async Task CanFilterDecimalRangeWithoutMin()
    {
        var filter = await SearchWith(DecimalRangeFilter.Single("price", null, 4.999m, false));

        Assert.That(filter, Does.Contain("(price_decimals/any(f: f lt 4.999))"));
    }

    [Test]
    public async Task CanFilterIntegerRangeWithoutMin()
    {
        var filter = await SearchWith(new IntegerRangeFilter("level", [new IntegerRangeFilterRange(null, 3)], false));

        Assert.That(filter, Does.Contain("(level_integers/any(f: f lt 3))"));
    }

    [Test]
    public async Task CanFilterRangeWithoutBounds()
    {
        var filter = await SearchWith(new IntegerRangeFilter("level", [new IntegerRangeFilterRange(null, null)], false));

        Assert.That(filter, Does.Contain("(level_integers/any())"));
    }

    [Test]
    public async Task CanFilterDateTimeOffsetRangeWithoutMax()
    {
        var from = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var filter = await SearchWith(DateTimeOffsetRangeFilter.Single("createDate", from, null, false));

        Assert.That(filter, Does.Contain("(createDate_datetimeoffsets/any(f: f ge 2026-01-02T03:04:05.0000000+00:00))"));
    }

    [Test]
    public async Task CanFilterDateTimeOffsetRangeWithoutMin()
    {
        var to = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var filter = await SearchWith(DateTimeOffsetRangeFilter.Single("updateDate", null, to, false));

        Assert.That(filter, Does.Contain("(updateDate_datetimeoffsets/any(f: f lt 2026-01-02T03:04:05.0000000+00:00))"));
    }

    [Test]
    public async Task CanFilterNegatedOpenRange()
    {
        var filter = await SearchWith(new IntegerRangeFilter("level",
            [new IntegerRangeFilterRange(null, 2), new IntegerRangeFilterRange(5, null)], true));

        Assert.That(filter, Does.Contain("not (level_integers/any(f: f lt 2) or level_integers/any(f: f ge 5))"));
    }

    [Test]
    public async Task IgnoresDateTimeOffsetRangeFilterWithoutRanges()
    {
        var filter = await SearchWith(new DateTimeOffsetRangeFilter("createDate", [], false));

        Assert.That(filter, Does.Not.Contain("createDate"));
    }

    private static async Task<string?> SearchWith(Filter filter)
    {
        var service = new FakeSearchService();
        await service.CreateSearcher().SearchAsync(IndexAlias, filters: [filter]);
        return service.LastFilter();
    }
}
