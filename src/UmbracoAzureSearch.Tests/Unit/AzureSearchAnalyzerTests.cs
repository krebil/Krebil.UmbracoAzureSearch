using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchAnalyzerTests
{
    private const string IndexAlias = "Umb_PublishedContent";

    private static FakeSearchService Service(Action<AzureSearchIndexBuilder>? configure = null, string? existingIndex = null)
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
            Respond = request =>
            {
                if (request.Method != HttpMethod.Get)
                    return null;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/indexes"))
                    return FakeSearchService.Json(existingIndex is null ? """{"value":[]}""" : $$"""{"value":[{{existingIndex}}]}""");
                if (path.Contains("/indexes('") && existingIndex is not null)
                    return FakeSearchService.Json(existingIndex);
                return null;
            },
        };
        return service;
    }

    private static Action<AzureSearchIndexBuilder> FieldPerCulture => index => index
        .Analyzers(a => a
            .FieldPerCulture()
            .Culture("fr", "fr.microsoft")
            .Culture("pt", "pt-Pt.microsoft")
            .Culture("pt-BR", "pt-Br.microsoft"));

    private static async Task<JsonElement> CreatedIndexAsync(FakeSearchService service)
    {
        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);
        return JsonDocument.Parse(service.Requests.Single(r => r.Method == HttpMethod.Post && !r.Path.Contains("/docs")).Body!).RootElement;
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement index)
        => index.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f);

    private static string? Analyzer(JsonElement field)
        => field.TryGetProperty("analyzer", out var analyzer) && analyzer.ValueKind == JsonValueKind.String ? analyzer.GetString() : null;

    private static string[] Suggester(JsonElement index)
        => index.GetProperty("suggesters")[0].GetProperty("sourceFields").EnumerateArray().Select(f => f.GetString()!).ToArray();

    private static async Task<JsonElement[]> IndexAsync(FakeSearchService service, Variation[] variations, params IndexField[] fields)
    {
        await service.CreateServices().GetRequiredService<IAzureSearchIndexer>()
            .AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, variations, fields, null);
        var body = service.Requests.Last(r => r.Path.Contains("search.index")).Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().ToArray();
    }

    private static string[] Strings(JsonElement document, string property)
        => document.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : [];

    private static IndexField Text(string name, string value, string? culture)
        => new(name, new IndexValue { TextsR1 = [value] }, culture, null);

    private static string? SearchFields(JsonElement body)
        => body.TryGetProperty("searchFields", out var fields) ? fields.GetString() : null;

    [Test]
    public async Task ModeNoneKeepsTheSharedFields()
    {
        var index = await CreatedIndexAsync(Service());

        var fields = Fields(index);
        Assert.Multiple(() =>
        {
            Assert.That(fields.Keys, Has.None.Contains("_inv"));
            Assert.That(Analyzer(fields["suggestion"]), Is.EqualTo("standard.lucene"));
            Assert.That(Suggester(index), Is.EqualTo(new[] { "suggestion" }));
            Assert.That(index.GetProperty("scoringProfiles")[0].GetProperty("text").GetProperty("weights").EnumerateObject().Count(), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task FieldPerCultureCreatesACopyPerCultureAndForInvariant()
    {
        var index = await CreatedIndexAsync(Service(FieldPerCulture));

        var fields = Fields(index);
        Assert.Multiple(() =>
        {
            Assert.That(Analyzer(fields["allTextsR1_fr"]), Is.EqualTo("fr.microsoft"));
            Assert.That(Analyzer(fields["allTexts_pt_br"]), Is.EqualTo("pt-Br.microsoft"));
            Assert.That(Analyzer(fields["allTextsR3_inv"]), Is.EqualTo("standard.lucene"));
            Assert.That(fields.Keys, Does.Contain("allTexts"), "Unmapped cultures use the shared fields.");
            Assert.That(Analyzer(fields["suggestion_fr"]), Is.EqualTo("fr.microsoft"));
            Assert.That(Suggester(index), Is.EquivalentTo(new[] { "suggestion", "suggestion_fr", "suggestion_pt", "suggestion_pt_br", "suggestion_inv" }));
            Assert.That(index.GetProperty("scoringProfiles")[0].GetProperty("text").GetProperty("weights").GetProperty("allTextsR1_fr").GetDouble(), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task AFixedSuggestionAnalyzerKeepsOneSharedSuggestionField()
    {
        var index = await CreatedIndexAsync(Service(index => FieldPerCulture(index.Analyzers(a => a.SuggestionAnalyzer("standard.lucene")))));

        Assert.That(Suggester(index), Is.EqualTo(new[] { "suggestion" }));
    }

    [Test]
    public async Task AFixedSuggestionAnalyzerAppliesToTheSharedField()
    {
        var service = Service(FieldPerCulture);
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Analyzers:SuggestionAnalyzer"] = "en.lucene";

        var index = await CreatedIndexAsync(service);

        Assert.That(Analyzer(Fields(index)["suggestion"]), Is.EqualTo("en.lucene"));
    }

    [Test]
    public async Task APerCultureFieldGetsCopiesOfItsTextFields()
    {
        var index = await CreatedIndexAsync(Service(index => FieldPerCulture(index.Field("title", f => f.Searchable().PerCulture()))));

        var fields = Fields(index);
        Assert.Multiple(() =>
        {
            Assert.That(Analyzer(fields["title_texts_r1_fr"]), Is.EqualTo("fr.microsoft"));
            Assert.That(Analyzer(fields["title_texts_inv"]), Is.EqualTo("standard.lucene"));
            Assert.That(fields.Keys, Has.None.StartsWith("body_"));
        });
    }

    [Test]
    public async Task AddsMissingCopiesToAnExistingIndex()
    {
        const string existing = """
            {"name":"umb-publishedcontent","fields":[{"name":"id","type":"Edm.String","key":true},
              {"name":"allTexts","type":"Collection(Edm.String)","searchable":true},
              {"name":"suggestion","type":"Edm.String","searchable":true,"analyzer":"standard.lucene"}],
             "suggesters":[{"name":"sg","searchMode":"analyzingInfixMatching","sourceFields":["suggestion"]}],
             "scoringProfiles":[{"name":"relevanceBoost","text":{"weights":{"allTexts":1}}}]}
            """;
        var service = Service(FieldPerCulture, existing);

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        var update = JsonDocument.Parse(service.Requests.Single(r => r.Method == HttpMethod.Put).Body!).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(Fields(update).Keys, Does.Contain("allTexts_fr").And.Contains("allTextsR1_inv"));
            Assert.That(Fields(update).Keys, Has.None.StartsWith("suggestion_"), "Azure adds suggester fields only with the suggester.");
            Assert.That(update.GetProperty("scoringProfiles")[0].GetProperty("text").GetProperty("weights").TryGetProperty("allTexts_fr", out _), Is.True);
        });
    }

    [Test]
    public async Task AMappedCultureWritesCultureAndInvariantCopies()
    {
        var service = Service(FieldPerCulture, """
            {"name":"umb-publishedcontent","fields":[{"name":"id","type":"Edm.String","key":true},
              {"name":"suggestion_fr","type":"Edm.String"},{"name":"suggestion_inv","type":"Edm.String"}]}
            """);

        var document = (await IndexAsync(service, [new Variation("fr-FR", null)],
            Text("title", "Bateau rouge", "fr-FR"),
            Text("code", "RB-42", null))).Single();

        Assert.Multiple(() =>
        {
            Assert.That(Strings(document, "allTextsR1_fr"), Is.EqualTo(new[] { "Bateau rouge" }));
            Assert.That(Strings(document, "allTextsR1_inv"), Is.EqualTo(new[] { "RB-42" }));
            Assert.That(Strings(document, "allTextsR1"), Is.Empty);
            Assert.That(document.GetProperty("suggestion_fr").GetString(), Is.EqualTo("Bateau rouge"));
            Assert.That(document.GetProperty("suggestion").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task AnInvariantDocumentWritesTheInvariantCopyAndTheSharedFields()
    {
        var service = Service(FieldPerCulture);

        var document = (await IndexAsync(service, [new Variation(null, null)], Text("title", "Red boat", null))).Single();

        Assert.Multiple(() =>
        {
            Assert.That(Strings(document, "allTextsR1_inv"), Is.EqualTo(new[] { "Red boat" }));
            Assert.That(Strings(document, "allTextsR1"), Is.EqualTo(new[] { "Red boat" }));
            Assert.That(document.GetProperty("suggestion").GetString(), Is.EqualTo("Red boat"));
        });
    }

    [Test]
    public async Task AnUnmappedCultureWritesTheSharedFields()
    {
        var service = Service(FieldPerCulture);

        var document = (await IndexAsync(service, [new Variation("da-DK", null)], Text("title", "Rød båd", "da-DK"))).Single();

        Assert.Multiple(() =>
        {
            Assert.That(Strings(document, "allTextsR1"), Is.EqualTo(new[] { "Rød båd" }));
            Assert.That(document.EnumerateObject().Select(p => p.Name), Has.None.EndsWith("_inv"));
        });
    }

    [TestCase("pt-BR", "allTextsR1_pt_br")]
    [TestCase("pt-PT", "allTextsR1_pt")]
    [TestCase("PT-br", "allTextsR1_pt_br")]
    public async Task MatchesTheMostSpecificCultureKey(string culture, string copy)
    {
        var service = Service(FieldPerCulture);

        var document = (await IndexAsync(service, [new Variation(culture, null)], Text("title", "Barco", culture))).Single();

        Assert.That(Strings(document, copy), Is.EqualTo(new[] { "Barco" }));
    }

    [Test]
    public async Task DropsSuggestionCopiesTheIndexDoesNotHave()
    {
        var service = Service(FieldPerCulture);

        var document = (await IndexAsync(service, [new Variation("fr-FR", null)], Text("title", "Bateau", "fr-FR"))).Single();

        Assert.Multiple(() =>
        {
            Assert.That(document.TryGetProperty("suggestion_fr", out _), Is.False);
            Assert.That(service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message),
                Has.One.Contains("Rebuild the index to add them"));
        });
    }

    [Test]
    public async Task AMappedCultureQuerySearchesItsCopiesAndTheInvariantCopies()
    {
        var service = Service(index => FieldPerCulture(index.Field("title", f => f.Searchable().PerCulture())));

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "bateau", culture: "fr-FR");

        var body = service.LastSearchBody();
        var fields = SearchFields(body)!.Split(',');
        Assert.Multiple(() =>
        {
            Assert.That(fields, Does.Contain("allTextsR1_fr").And.Contains("allTexts_inv").And.Contains("title_texts_r2_fr").And.Contains("title_texts_inv"));
            Assert.That(fields, Has.None.EqualTo("allTexts").And.None.Contains("_pt"));
            Assert.That(body.GetProperty("filter").GetString(), Does.Contain("culture eq 'fr-fr' or culture eq 'inv'"));
        });
    }

    [Test]
    public async Task AnUnmappedCultureQuerySearchesTheSharedFields()
    {
        var service = Service(FieldPerCulture);

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "båd", culture: "da-DK");

        Assert.That(SearchFields(service.LastSearchBody()), Is.EqualTo("allTextsR1,allTextsR2,allTextsR3,allTexts"));
    }

    [Test]
    public async Task ModeNoneSendsNoSearchFields()
    {
        var service = Service();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "boat", culture: "fr-FR", maxSuggestions: 5);

        Assert.Multiple(() =>
        {
            Assert.That(SearchFields(service.LastSearchBody()), Is.Null);
            Assert.That(SearchFields(service.LastAutocompleteBody()), Is.Null);
        });
    }

    [Test]
    public async Task TermAutocompleteUsesTheSuggestionCopiesOfTheCulture()
    {
        var service = Service(FieldPerCulture);

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "bat", culture: "fr-FR", maxSuggestions: 5);

        Assert.That(SearchFields(service.LastAutocompleteBody()), Is.EqualTo("suggestion_fr,suggestion_inv"));
    }

    [Test]
    public async Task TermAutocompleteWithAFixedSuggestionAnalyzerUsesTheSharedField()
    {
        var service = Service(index => FieldPerCulture(index.Analyzers(a => a.SuggestionAnalyzer("standard.lucene"))));

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "bat", culture: "fr-FR", maxSuggestions: 5);

        var body = service.LastAutocompleteBody();
        Assert.Multiple(() =>
        {
            Assert.That(SearchFields(body), Is.Null);
            Assert.That(body.GetProperty("filter").GetString(), Does.Contain("culture eq 'fr-fr'"));
        });
    }

    [Test]
    public void StopsStartupForAnUnknownCultureAnalyzer()
    {
        var service = Service(index => index.Analyzers(a => a.FieldPerCulture().Culture("pt", "pt-BR")));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("Analyzers:Cultures:pt is \"pt-BR\""));
    }

    [Test]
    public void StopsStartupForAnUnknownInvariantOrSuggestionAnalyzer()
    {
        var service = Service(index => FieldPerCulture(index.Analyzers(a => a.Invariant("standard").SuggestionAnalyzer("lucene"))));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("Analyzers:Invariant is \"standard\"").And.Contains("Analyzers:SuggestionAnalyzer is \"lucene\""));
    }

    [Test]
    public void StopsStartupForASuggestionAnalyzerAzureRejects()
    {
        var service = Service(index => index.Analyzers(a => a.FieldPerCulture().Culture("fr", "keyword")));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("The suggestion field \"suggestion_fr\" uses the analyzer \"keyword\""));
    }

    [Test]
    public void StopsStartupForFieldPerCultureWithoutCultures()
    {
        var service = Service(index => index.Analyzers(a => a.FieldPerCulture()));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("maps no culture"));
    }

    [Test]
    public void CountsTheCopiesAgainstTheFieldLimit()
    {
        var service = Service(FieldPerCulture);
        service.Settings["UmbracoAzureSearch:MaxFieldsPerIndex"] = "50";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        // 10 base fields, 25 system fields, 5 suggestion fields and 4 copies for each of 3 cultures and invariant.
        Assert.That(exception!.Message, Does.Contain("needs 56 Azure fields, but the service allows 50"));
    }

    [Test]
    public void AcceptsAValidFieldPerCultureDefinition()
    {
        var service = Service(index => FieldPerCulture(index.Field("title", f => f.Searchable().PerCulture())));

        Assert.DoesNotThrowAsync(() => StartAsync(service));
    }

    private static async Task StartAsync(FakeSearchService service)
    {
        foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }
}
