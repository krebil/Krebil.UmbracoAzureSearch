using System.Text;
using Umbraco.Cms.Core.Models;

namespace UmbracoAzureSearch.Tests.Unit;

// Azure.Search.Documents 12.1.0-beta.1 failed to read a result whose JSON fills its read buffer exactly
// (16 KB * 2^n bytes): https://github.com/Azure/azure-sdk-for-net/issues/40768
[TestFixture]
public class AzureSearchSearcherLargeDocumentTests
{
    private const string IndexAlias = "unit-test-index";

    [TestCase(16 * 1024 - 1)]
    [TestCase(16 * 1024)]
    [TestCase(16 * 1024 + 1)]
    [TestCase(32 * 1024)]
    [TestCase(64 * 1024)]
    public async Task ReadsResultDocumentsOfAnySize(int documentBytes)
    {
        var key = Guid.NewGuid();
        var document = Document(key, documentBytes);
        var service = new FakeSearchService
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("search.post.search")
                ? FakeSearchService.Json($$"""{"@odata.count":1,"value":[{{document}}]}""")
                : null,
        };

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "test");

        Assert.That(result.Documents.Select(d => d.Id), Is.EqualTo(new[] { key }));
    }

    // A search result document of exactly the given number of bytes, padded with a text field.
    private static string Document(Guid key, int bytes)
    {
        var start = $$"""{"@search.score":1.0,"id":"{{key}}_invariant","key":"{{key}}","objectType":"{{UmbracoObjectTypes.Document}}","padding":"x""";
        const string end = "\"}";
        var json = start + new string('x', bytes - start.Length - end.Length) + end;
        Assert.That(Encoding.UTF8.GetByteCount(json), Is.EqualTo(bytes));
        return json;
    }
}
