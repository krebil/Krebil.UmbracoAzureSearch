using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexerSuggestionTests
{
    private const string IndexAlias = "unit-test-index";

    private static async Task<JsonElement[]> IndexAsync(FakeSearchService service, Variation[] variations, params IndexField[] fields)
    {
        var indexer = service.CreateServices().GetRequiredService<IAzureSearchIndexer>();
        await indexer.AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, variations, fields, null);

        var body = service.Requests.Last(r => r.Path.Contains("search.index")).Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().ToArray();
    }

    private static readonly Variation[] Invariant = [new(Culture: null, Segment: null)];

    private static string? Suggestion(JsonElement document)
        => document.TryGetProperty("suggestion", out var value) ? value.GetString() : "<absent>";

    [Test]
    public async Task FillsSuggestionsFromTheHighestRelevanceTexts()
    {
        var service = new FakeSearchService();

        var documents = await IndexAsync(
            service,
            Invariant,
            new IndexField("name", new IndexValue { TextsR1 = ["Red boat"], Texts = ["body text"] }, null, null),
            new IndexField("heading", new IndexValue { TextsR1 = ["<b>Read more</b>"], TextsR2 = ["lower"] }, null, null));

        Assert.That(Suggestion(documents.Single()), Is.EqualTo("Red boat Read more"));
    }

    [Test]
    public async Task FillsSuggestionsFromAllTextsOfTheConfiguredFields()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Fields:0"] = "title";

        var documents = await IndexAsync(
            service,
            Invariant,
            new IndexField("name", new IndexValue { TextsR1 = ["Ignored name"] }, null, null),
            new IndexField("title", new IndexValue { Texts = ["plain"], TextsR2 = ["second"] }, null, null));

        Assert.That(Suggestion(documents.Single()), Is.EqualTo("plain second"));
    }

    [Test]
    public async Task FillsEachVariationWithItsOwnTexts()
    {
        var service = new FakeSearchService();

        var documents = await IndexAsync(
            service,
            [new Variation(Culture: "en-US", Segment: null), new Variation(Culture: "da-DK", Segment: null)],
            new IndexField("name", new IndexValue { TextsR1 = ["Umbraco"] }, "en-US", null),
            new IndexField("name", new IndexValue { TextsR1 = ["Paraply"] }, "da-DK", null));

        Assert.That(documents.Select(Suggestion), Is.EqualTo(new[] { "Umbraco", "Paraply" }));
    }

    [Test]
    public async Task LeavesSuggestionsEmptyWithoutTexts()
    {
        var service = new FakeSearchService();

        var documents = await IndexAsync(
            service,
            Invariant,
            new IndexField("count", new IndexValue { Integers = [3] }, null, null));

        Assert.That(Suggestion(documents.Single()), Is.Null);
    }

    [Test]
    public async Task DoesNotFillSuggestionsWhenTheyAreDisabledForTheIndex()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";

        var documents = await IndexAsync(
            service,
            Invariant,
            new IndexField("name", new IndexValue { TextsR1 = ["Red boat"] }, null, null));

        Assert.That(Suggestion(documents.Single()), Is.EqualTo("<absent>"));
    }
}
