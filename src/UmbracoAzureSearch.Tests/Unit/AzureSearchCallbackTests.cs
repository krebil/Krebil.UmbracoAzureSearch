using System.Text.Json;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchCallbackTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string IndexName = "umb-publishedcontent";

    private static FakeSearchService Service(Action<AzureSearchIndexBuilder> configure, string? existingIndex = null, string[]? languages = null)
        => new()
        {
            ConfigureServices = services =>
            {
                services.ConfigureAzureSearchIndex(IndexAlias, configure);
                if (languages is not null)
                {
                    services.AddSingleton<IUmbracoContentSource>(new LanguageSource(languages));
                }
            },
            Respond = request =>
            {
                if (request.Method != HttpMethod.Get)
                    return null;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/indexes"))
                    return FakeSearchService.Json(existingIndex is null ? """{"value":[]}""" : $$"""{"value":[{{existingIndex}}]}""");
                if (path.Contains("/indexes('") && path.EndsWith("')") && existingIndex is not null)
                    return FakeSearchService.Json(existingIndex);
                return null;
            },
        };

    private static readonly Action<SearchIndex> AddAnalyzerAndProfile = searchIndex =>
    {
        searchIndex.Analyzers.Add(new CustomAnalyzer("names_folded", LexicalTokenizerName.Standard)
        {
            TokenFilters = { TokenFilterName.Lowercase, TokenFilterName.AsciiFolding }
        });
        searchIndex.ScoringProfiles.Add(new ScoringProfile("titleFirst")
        {
            TextWeights = new TextWeights(new Dictionary<string, double> { ["allTextsR1"] = 5 })
        });
    };

    private static JsonElement[] Writes(FakeSearchService service, HttpMethod method)
        => service.Requests
            .Where(r => r.Method == method && !r.Path.Contains("/docs"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .ToArray();

    private static string[] Names(JsonElement index, string collection)
        => index.TryGetProperty(collection, out var items)
            ? items.EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray()
            : [];

    private static Task EnsureAsync(FakeSearchService service)
        => service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

    [Test]
    public async Task ConfigureIndexChangesANewIndex()
    {
        AzureSearchIndexContext? context = null;
        var service = Service(index => index.ConfigureIndex((searchIndex, c) =>
        {
            context = c;
            AddAnalyzerAndProfile(searchIndex);
        }));

        await EnsureAsync(service);

        var created = Writes(service, HttpMethod.Post).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Names(created, "analyzers"), Is.EqualTo(new[] { "names_folded" }));
            Assert.That(Names(created, "scoringProfiles"), Does.Contain("titleFirst").And.Contains("relevanceBoost"));
            Assert.That(context, Is.EqualTo(new AzureSearchIndexContext(IndexAlias, IndexName, null, false)));
        });
    }

    [Test]
    public async Task ConfigureIndexRunsForEveryIndexOfIndexPerCulture()
    {
        var contexts = new List<AzureSearchIndexContext>();
        var service = Service(
            index => index
                .Analyzers(a => a.IndexPerCulture().Culture("fr", "fr.microsoft"))
                .ConfigureIndex((_, c) => contexts.Add(c)),
            languages: ["fr-FR", "da-DK"]);

        await EnsureAsync(service);

        Assert.That(contexts, Is.EquivalentTo(new[]
        {
            new AzureSearchIndexContext(IndexAlias, $"{IndexName}-fr", "fr", false),
            new AzureSearchIndexContext(IndexAlias, $"{IndexName}-fallback", null, true),
        }));
    }

    [Test]
    public async Task ConfigureIndexRunsWhenTheIndexIsReset()
    {
        var service = Service(index => index.ConfigureIndex(AddAnalyzerAndProfile), existingIndex: $$"""{"name":"{{IndexName}}","fields":[]}""");

        await service.CreateServices().GetRequiredService<IAzureSearchIndexer>().ResetAsync(IndexAlias);

        Assert.That(Names(Writes(service, HttpMethod.Post).Single(), "analyzers"), Is.EqualTo(new[] { "names_folded" }));
    }

    [Test]
    public async Task AppliesNewScoringProfilesToAnExistingIndexAtStartup()
    {
        const string existing = """
            {"name":"umb-publishedcontent","fields":[{"name":"id","type":"Edm.String","key":true},
              {"name":"suggestion","type":"Edm.String","searchable":true,"analyzer":"standard.lucene"}],
             "suggesters":[{"name":"sg","searchMode":"analyzingInfixMatching","sourceFields":["suggestion"]}]}
            """;
        var service = Service(index => index.ConfigureIndex(AddAnalyzerAndProfile), existing);

        await EnsureAsync(service);

        var update = Writes(service, HttpMethod.Put).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Names(update, "scoringProfiles"), Does.Contain("titleFirst"));
            Assert.That(Names(update, "analyzers"), Is.Empty, "A new analyzer needs a rebuild.");
        });
    }

    [Test]
    public async Task LeavesAnExistingIndexAloneWhenTheCallbacksChangeNothing()
    {
        var service = Service(index => index.ConfigureIndex(_ => { }));
        await EnsureAsync(service);
        var created = service.Requests.Single(r => r.Method == HttpMethod.Post && !r.Path.Contains("/docs")).Body!;

        var again = Service(index => index.ConfigureIndex(_ => { }), created);
        await EnsureAsync(again);

        Assert.That(Writes(again, HttpMethod.Put), Is.Empty);
    }

    [Test]
    public async Task ConfigureSearchChangesTheQueryAfterThePackage()
    {
        AzureSearchRequest? seen = null;
        var service = Service(index => index.ConfigureSearch((options, request) =>
        {
            seen = request;
            if (request.Culture == "fr-FR")
            {
                options.ScoringProfile = "titleFirst";
            }

            options.Filter = $"({options.Filter}) and objectType eq 'Document'";
        }));

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "avion", culture: "fr-FR");

        var body = service.LastSearchBody();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("scoringProfile").GetString(), Is.EqualTo("titleFirst"));
            Assert.That(body.GetProperty("filter").GetString(), Does.StartWith("((culture eq 'fr-fr'").And.EndWith("and objectType eq 'Document'"));
            Assert.That(seen, Is.EqualTo(new AzureSearchRequest(IndexAlias, IndexName, "avion", "fr-FR", null)));
        });
    }

    [Test]
    public void AcceptsAFieldAnalyzerThatConfigureIndexAdds()
    {
        var service = Service(index => index
            .ConfigureIndex(AddAnalyzerAndProfile)
            .Field("names", f => f.Analyzer("names_folded")));

        Assert.DoesNotThrowAsync(() => StartAsync(service));
    }

    [Test]
    public void StillRejectsAnUnknownAnalyzerWithoutConfigureIndex()
    {
        var service = Service(index => index.Field("names", f => f.Analyzer("names_folded")));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("\"names_folded\"").And.Contains("or add the analyzer with ConfigureIndex"));
    }

    [Test]
    public async Task DoesNotRunConfigureIndexForValidation()
    {
        var calls = 0;
        var service = Service(index => index.ConfigureIndex(_ => calls++));

        await StartAsync(service);

        Assert.That(calls, Is.Zero);
    }

    private static async Task StartAsync(FakeSearchService service)
    {
        foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private sealed class LanguageSource(string[] languages) : IUmbracoContentSource
    {
        public Guid? GetContentTypeKey(string alias, Umbraco.Cms.Core.Models.UmbracoObjectTypes objectType) => null;

        public Umbraco.Cms.Core.Models.IContentBase? GetContent(Guid id, Umbraco.Cms.Core.Models.UmbracoObjectTypes objectType) => null;

        public Task<string[]> GetLanguageIsoCodesAsync() => Task.FromResult(languages);
    }
}
