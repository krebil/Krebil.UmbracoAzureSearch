using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
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
}
