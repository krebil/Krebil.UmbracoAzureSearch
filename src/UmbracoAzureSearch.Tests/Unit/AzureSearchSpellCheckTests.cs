using System.Net;
using System.Text.Json;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSpellCheckTests
{
    private const string IndexAlias = "Umb_PublishedContent";

    private static readonly Action<AzureSearchIndexBuilder> SpellCheck = index => index
        .SpellCheck(s => s.Culture("en", QueryLanguage.EnUs).Culture("fr", QueryLanguage.FrFr));

    private static FakeSearchService Service(Action<AzureSearchIndexBuilder>? configure = null)
        => new()
        {
            ConfigureServices = services =>
            {
                if (configure is not null)
                {
                    services.ConfigureAzureSearchIndex(IndexAlias, configure);
                }
            },
        };

    private static JsonElement[] SearchBodies(FakeSearchService service)
        => service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => JsonDocument.Parse(r.Body!).RootElement).ToArray();

    private static string? String(JsonElement body, string property)
        => body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [TestCase("en-US", "en-us")]
    [TestCase("en-GB", "en-us")]
    [TestCase("FR-ca", "fr-fr")]
    public async Task SendsTheSpellerForAMappedCulture(string culture, string queryLanguage)
    {
        var service = Service(SpellCheck);

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "famly acitvites", culture: culture);

        var body = service.LastSearchBody();
        Assert.Multiple(() =>
        {
            Assert.That(String(body, "speller"), Is.EqualTo("lexicon"));
            Assert.That(String(body, "queryLanguage"), Is.EqualTo(queryLanguage));
            Assert.That(String(body, "queryType"), Is.EqualTo("simple"), "Spell check does not need the semantic ranker.");
            Assert.That(String(body, "semanticConfiguration"), Is.Null);
        });
    }

    [Test]
    public async Task SendsNoSpellerForAnUnmappedCultureOrWithoutSpellCheck()
    {
        var mapped = Service(SpellCheck);
        var off = Service();

        await mapped.CreateSearcher().SearchAsync(IndexAlias, query: "famly", culture: "da-DK");
        await off.CreateSearcher().SearchAsync(IndexAlias, query: "famly", culture: "en-US");

        Assert.Multiple(() =>
        {
            Assert.That(String(mapped.LastSearchBody(), "speller"), Is.Null);
            Assert.That(String(off.LastSearchBody(), "speller"), Is.Null);
        });
    }

    [Test]
    public async Task ReadsTheCulturesFromAppSettings()
    {
        var service = Service();
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:SpellCheck:Cultures:de"] = "de-de";

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "hauss garten", culture: "de-AT");

        Assert.That(String(service.LastSearchBody(), "queryLanguage"), Is.EqualTo("de-de"));
    }

    [Test]
    public async Task SendsTheSpellerWithTheSameFieldFacetQuery()
    {
        var service = Service(SpellCheck);

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "famly acitvites", culture: "en-US",
            filters: [new KeywordFilter("genre", ["rock"], false)],
            facets: [new KeywordFacet("genre")]);

        Assert.That(SearchBodies(service).Select(b => String(b, "speller")), Is.EqualTo(new[] { "lexicon", "lexicon" }));
    }

    [Test]
    public async Task SearchesWithoutSpellCheckWhenTheServiceRejectsIt()
    {
        var service = Service(SpellCheck);
        service.Respond = request => request.RequestUri!.AbsolutePath.Contains("search.post.search")
                                     && request.Content!.ReadAsStringAsync().Result.Contains("\"speller\"")
            ? FakeSearchService.Json("""{"error":{"code":"InvalidRequestParameter","message":"The parameter 'speller' is not supported for this service tier."}}""", HttpStatusCode.BadRequest)
            : null;
        var searcher = service.CreateServices().GetRequiredService<IAzureSearchSearcher>();

        var first = await searcher.SearchAsync(IndexAlias, query: "famly acitvites", culture: "en-US");
        var second = await searcher.SearchAsync(IndexAlias, query: "famly acitvites", culture: "en-US");

        var bodies = SearchBodies(service);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(bodies.Select(b => String(b, "speller")), Is.EqualTo(new[] { "lexicon", null, null }), "Rejected once, then skipped.");
            Assert.That(service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message), Has.One.Contains("rejected spell check"));
        });
    }

    [Test]
    public void StopsStartupForALanguageTheSpellerDoesNotSupport()
    {
        var service = Service(index => index.SpellCheck(s => s.Culture("pt", QueryLanguage.PtBr)));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        });

        Assert.That(exception!.Message, Does.Contain("SpellCheck:Cultures:pt is \"pt-br\"").And.Contains("en-us"));
    }
}
