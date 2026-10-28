using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexerUpsertTests
{
    private const string IndexAlias = "unit-test-index";

    private static readonly Variation[] Invariant = [new(Culture: null, Segment: null)];

    private static async Task<JsonElement[]> IndexAsync(FakeSearchService service, params IndexField[] fields)
    {
        var indexer = service.CreateServices().GetRequiredService<IAzureSearchIndexer>();
        await indexer.AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, Invariant, fields, null);

        var body = service.Requests.Last(r => r.Path.Contains("search.index")).Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().ToArray();
    }

    // A merge keeps fields the new document leaves out, so a value removed from the content
    // would stay searchable. Uploading replaces the whole document.
    [Test]
    public async Task ReplacesTheWholeDocument()
    {
        var service = new FakeSearchService();

        var documents = await IndexAsync(
            service,
            new IndexField("universe", new IndexValue { Keywords = ["histoire"] }, null, null));

        Assert.That(documents.Single().GetProperty("@search.action").GetString(), Is.EqualTo("upload"));
    }

    // Each variation is a document of its own. When a culture is unpublished, the next reindex no
    // longer includes that variation, so its document must be deleted.
    [Test]
    public async Task DeletesDocumentsOfVariationsThatAreNoLongerIndexed()
    {
        var id = Guid.NewGuid();
        var service = ServiceWithExistingDocuments($"{id.AsKeyword()}_en-us_def", $"{id.AsKeyword()}_da-dk_def");

        await IndexCulturesAsync(service, id, "en-US");

        var batches = IndexBatches(service);
        Assert.Multiple(() =>
        {
            Assert.That(batches, Has.Length.EqualTo(2));
            Assert.That(Actions(batches[0]), Is.EqualTo(new[] { ("upload", $"{id.AsKeyword()}_en-us_def") }));
            Assert.That(Actions(batches[1]), Is.EqualTo(new[] { ("delete", $"{id.AsKeyword()}_da-dk_def") }));
        });
    }

    [Test]
    public async Task DeletesNothingWhenAllVariationsAreStillIndexed()
    {
        var id = Guid.NewGuid();
        var service = ServiceWithExistingDocuments($"{id.AsKeyword()}_en-us_def", $"{id.AsKeyword()}_da-dk_def");

        await IndexCulturesAsync(service, id, "en-US", "da-DK");

        Assert.That(IndexBatches(service), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task LooksUpExistingDocumentsByContentKey()
    {
        var id = Guid.NewGuid();
        var service = ServiceWithExistingDocuments();

        await IndexCulturesAsync(service, id, "en-US");

        Assert.That(service.LastFilter(), Is.EqualTo($"key eq '{id.AsKeyword()}'"));
    }

    private static FakeSearchService ServiceWithExistingDocuments(params string[] documentIds)
    {
        var value = string.Join(",", documentIds.Select(documentId => $$"""{"id":"{{documentId}}"}"""));
        return new FakeSearchService
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("search.post.search")
                ? FakeSearchService.Json($$"""{"value":[{{value}}]}""")
                : null
        };
    }

    private static async Task IndexCulturesAsync(FakeSearchService service, Guid id, params string[] cultures)
    {
        var indexer = service.CreateServices().GetRequiredService<IAzureSearchIndexer>();
        var variations = cultures.Select(culture => new Variation(culture, Segment: null)).ToArray();
        IndexField[] fields = cultures
            .Select(culture => new IndexField("name", new IndexValue { Texts = [culture] }, culture, null))
            .ToArray();

        await indexer.AddOrUpdateAsync(IndexAlias, id, UmbracoObjectTypes.Document, variations, fields, null);
    }

    private static JsonElement[] IndexBatches(FakeSearchService service)
        => service.Requests
            .Where(r => r.Path.Contains("search.index"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .ToArray();

    private static (string Action, string Id)[] Actions(JsonElement batch)
        => batch.GetProperty("value").EnumerateArray()
            .Select(d => (d.GetProperty("@search.action").GetString()!, d.GetProperty("id").GetString()!))
            .ToArray();
}
