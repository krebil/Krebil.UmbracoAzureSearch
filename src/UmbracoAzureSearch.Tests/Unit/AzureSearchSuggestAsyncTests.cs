using System.Net;
using System.Text.Json;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSuggestAsyncTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private static readonly Guid Key = Guid.NewGuid();

    private static FakeSearchService Service(Action<AzureSearchIndexBuilder>? configure = null, string? suggestResponse = null, string indexFields = "")
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services =>
            {
                if (configure is not null)
                {
                    services.ConfigureAzureSearchIndex(IndexAlias, configure);
                }
            },
        };
        service.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("search.post.suggest"))
                return FakeSearchService.Json(suggestResponse ?? """{"value":[]}""");
            if (request.Method == HttpMethod.Get && path.EndsWith("')"))
                return FakeSearchService.Json($$"""{"name":"umb-publishedcontent","fields":[{"name":"id","type":"Edm.String","key":true}{{indexFields}}]}""");
            return null;
        };
        return service;
    }

    private static IAzureSearchSearcher Searcher(FakeSearchService service) => service.CreateSearcher();

    private static JsonElement[] SuggestBodies(FakeSearchService service)
        => service.Requests.Where(r => r.Path.Contains("search.post.suggest")).Select(r => JsonDocument.Parse(r.Body!).RootElement).ToArray();

    private static JsonElement SuggestBody(FakeSearchService service) => SuggestBodies(service).Last();

    private static string? String(JsonElement body, string property)
        => body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Test]
    public async Task SendsTheQueryWithTheDefaults()
    {
        var service = Service();

        await Searcher(service).SuggestAsync(IndexAlias, "  red   bo ");

        var body = SuggestBody(service);
        Assert.Multiple(() =>
        {
            Assert.That(String(body, "search"), Is.EqualTo("red bo"));
            Assert.That(String(body, "suggesterName"), Is.EqualTo("sg"));
            Assert.That(body.GetProperty("top").GetInt32(), Is.EqualTo(5));
            Assert.That(body.TryGetProperty("fuzzy", out var fuzzy) && fuzzy.GetBoolean(), Is.False);
            Assert.That(String(body, "highlightPreTag"), Is.EqualTo("<em>"));
            Assert.That(String(body, "highlightPostTag"), Is.EqualTo("</em>"));
            Assert.That(String(body, "select"), Is.EqualTo("key,objectType"));
        });
    }

    [Test]
    public async Task AppliesTheFiltersCultureAndAccessOfASearch()
    {
        var service = Service();
        var root = Guid.NewGuid();

        await Searcher(service).SuggestAsync(IndexAlias, "red",
            filters: [new KeywordFilter("Umb_PathIds", [root.ToString()], false), new TextFilter("title", ["boat"], false)],
            culture: "en-US",
            accessContext: new AccessContext(Guid.NewGuid(), null));

        var filter = String(SuggestBody(service), "filter")!;
        Assert.Multiple(() =>
        {
            Assert.That(filter, Does.Contain("culture eq 'en-us' or culture eq 'inv'"));
            Assert.That(filter, Does.Contain("segment eq 'def'"));
            Assert.That(filter, Does.Contain("accessKeys"));
            Assert.That(filter, Does.Contain($"Umb_PathIds_keywords/any(x: x eq '{root}')").Or.Contain(root.ToString()));
            Assert.That(filter, Does.Not.Contain("ismatch"), "Text filters are left out.");
        });
    }

    [Test]
    public async Task LimitsTopAndCutsALongQuery()
    {
        var service = Service();

        await Searcher(service).SuggestAsync(IndexAlias, new string('a', 150), top: 500, fuzzy: true);

        var body = SuggestBody(service);
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("top").GetInt32(), Is.EqualTo(100));
            Assert.That(String(body, "search")!.Length, Is.EqualTo(100));
            Assert.That(body.GetProperty("fuzzy").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task UsesTheHighlightTagsOfTheIndex()
    {
        var service = Service();
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:HighlightPreTag"] = "<mark>";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:HighlightPostTag"] = "</mark>";

        await Searcher(service).SuggestAsync(IndexAlias, "red");

        Assert.That(String(SuggestBody(service), "highlightPreTag"), Is.EqualTo("<mark>"));
    }

    [Test]
    public async Task UsesTheSuggestionCopiesOfAMappedCulture()
    {
        var service = Service(index => index.Analyzers(a => a.FieldPerCulture().Culture("fr", "fr.microsoft")));

        await Searcher(service).SuggestAsync(IndexAlias, "bat", culture: "fr-FR");
        await Searcher(service).SuggestAsync(IndexAlias, "bat", culture: "da-DK");

        var bodies = SuggestBodies(service);
        Assert.Multiple(() =>
        {
            Assert.That(String(bodies[0], "searchFields"), Is.EqualTo("suggestion_fr,suggestion_inv"));
            Assert.That(String(bodies[1], "searchFields"), Is.Null, "An unmapped culture uses the shared field.");
        });
    }

    [Test]
    public async Task UsesTheSharedSuggestionFieldWithAFixedAnalyzer()
    {
        var service = Service(index => index.Analyzers(a => a.FieldPerCulture().Culture("fr", "fr.microsoft").SuggestionAnalyzer("standard.lucene")));

        await Searcher(service).SuggestAsync(IndexAlias, "bat", culture: "fr-FR");

        Assert.That(String(SuggestBody(service), "searchFields"), Is.Null);
    }

    [Test]
    public async Task ReturnsTheDocumentsWithTheSelectedFields()
    {
        var service = Service(
            suggestResponse: $$"""{"value":[{"@search.text":"<em>Red</em> boat","key":"{{Key}}","objectType":"Document","title_texts":["Red boat"],"category_keywords":["news"]}]}""",
            indexFields: """,{"name":"title_texts","type":"Collection(Edm.String)"},{"name":"category_keywords","type":"Collection(Edm.String)"}""");

        var result = await Searcher(service).SuggestAsync(IndexAlias, "red", select: ["title", "category", "missing"]);

        var document = result.Documents.Single();
        Assert.Multiple(() =>
        {
            Assert.That(String(SuggestBody(service), "select"), Is.EqualTo("key,objectType,title_texts,category_keywords"));
            Assert.That(document.Id, Is.EqualTo(Key));
            Assert.That(document.ObjectType, Is.EqualTo(UmbracoObjectTypes.Document));
            Assert.That(document.Text, Is.EqualTo("<em>Red</em> boat"));
            Assert.That(document.Fields["title"], Is.EqualTo(new object[] { "Red boat" }));
            Assert.That(document.Fields["category"], Is.EqualTo(new object[] { "news" }));
            Assert.That(document.Fields["missing"], Is.Null);
        });
    }

    [Test]
    public async Task ReturnsNothingForABlankQuery()
    {
        var service = Service();

        var result = await Searcher(service).SuggestAsync(IndexAlias, "   ");

        Assert.Multiple(() =>
        {
            Assert.That(result.Documents, Is.Empty);
            Assert.That(SuggestBodies(service), Is.Empty);
        });
    }

    [Test]
    public void ThrowsWhenSuggestionsAreOff()
    {
        var service = Service(index => index.Suggestions(false));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => Searcher(service).SuggestAsync(IndexAlias, "red"));

        Assert.That(exception!.Message, Does.Contain("Suggestions are off").And.Contains("Suggestions:Enabled"));
    }

    [Test]
    public void ThrowsAzureErrors()
    {
        var service = Service();
        service.Respond = request => request.RequestUri!.AbsolutePath.Contains("search.post.suggest")
            ? FakeSearchService.Json("""{"error":{"code":"","message":"The request is invalid."}}""", HttpStatusCode.BadRequest)
            : null;

        Assert.ThrowsAsync<RequestFailedException>(() => Searcher(service).SuggestAsync(IndexAlias, "red"));
    }

    [Test]
    public async Task FindsNothingForAFilterOnAFieldNoDocumentHas()
    {
        var service = Service();
        service.UnknownField = "author_keywords";
        service.Respond = request =>
        {
            var body = request.Content?.ReadAsStringAsync().Result;
            return request.RequestUri!.AbsolutePath.Contains("search.post.suggest") && body?.Contains("author_keywords") == true
                ? FakeSearchService.Json("""{"error":{"code":"","message":"Invalid expression: Could not find a property named 'author_keywords' on type 'search.document'."}}""", HttpStatusCode.BadRequest)
                : request.RequestUri.AbsolutePath.Contains("search.post.suggest") ? FakeSearchService.Json("""{"value":[]}""") : null;
        };

        var found = await Searcher(service).SuggestAsync(IndexAlias, "red", filters: [new KeywordFilter("author", ["ann"], false)]);
        var negated = await Searcher(service).SuggestAsync(IndexAlias, "red", filters: [new KeywordFilter("author", ["ann"], true)]);

        Assert.Multiple(() =>
        {
            Assert.That(found.Documents, Is.Empty);
            Assert.That(negated.Documents, Is.Empty);
            Assert.That(String(SuggestBody(service), "filter"), Does.Not.Contain("author_keywords"), "A negated filter on the field is dropped.");
        });
    }

    [Test]
    public void RejectsASelectNameThatIsNotAnIdentifier()
    {
        var service = Service();

        Assert.ThrowsAsync<ArgumentException>(() => Searcher(service).SuggestAsync(IndexAlias, "red", select: ["title,key"]));
    }
}
