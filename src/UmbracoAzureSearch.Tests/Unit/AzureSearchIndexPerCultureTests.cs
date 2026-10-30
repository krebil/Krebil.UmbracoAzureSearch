using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexPerCultureTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string Fr = "umb-publishedcontent-fr";
    private const string En = "umb-publishedcontent-en";
    private const string Fallback = "umb-publishedcontent-fallback";

    private static readonly Action<AzureSearchIndexBuilder> IndexPerCulture = index => index
        .Analyzers(a => a.IndexPerCulture().Culture("fr", "fr.microsoft").Culture("en", "en.microsoft"));

    // existingIndexes: the Azure indexes the fake service has; null answers every index as existing.
    private static FakeSearchService Service(
        string[] languages, Action<AzureSearchIndexBuilder>? configure = null, string[]? existingIndexes = null,
        LanguageSource? languageSource = null)
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services =>
            {
                services.AddSingleton<IUmbracoContentSource>(languageSource ?? new LanguageSource(languages));
                services.ConfigureAzureSearchIndex(IndexAlias, configure ?? IndexPerCulture);
            },
        };
        if (existingIndexes is not null)
        {
            // Indexes the services create exist from then on.
            var indexes = existingIndexes.ToHashSet();
            service.Respond = request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/indexes"))
                    return FakeSearchService.Json($$"""{"value":[{{string.Join(",", indexes.Select(n => $$"""{"name":"{{n}}","fields":[]}"""))}}]}""");
                if (request.Method == HttpMethod.Post && path.EndsWith("/indexes"))
                {
                    var created = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("name").GetString()!;
                    indexes.Add(created);
                    return FakeSearchService.Json($$"""{"name":"{{created}}","fields":[]}""", HttpStatusCode.Created);
                }
                var name = IndexName(path);
                if (name is not null && !indexes.Contains(name) && request.Method != HttpMethod.Post)
                    return FakeSearchService.Json("""{"error":{"code":"","message":"No index with the name was found"}}""", HttpStatusCode.NotFound);
                if (name is not null && request.Method == HttpMethod.Get && path.EndsWith("')"))
                    return FakeSearchService.Json($$"""{"name":"{{name}}","fields":[{"name":"id","type":"Edm.String","key":true}]}""");
                return null;
            };
        }

        return service;
    }

    private static string? IndexName(string path)
    {
        var start = path.IndexOf("/indexes('", StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += "/indexes('".Length;
        return path[start..path.IndexOf("')", start, StringComparison.Ordinal)];
    }

    private static string[] CreatedIndexes(FakeSearchService service)
        => service.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/indexes"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("name").GetString()!)
            .ToArray();

    private static JsonElement Created(FakeSearchService service, string name)
        => service.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/indexes"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .Single(index => index.GetProperty("name").GetString() == name);

    private static string? Analyzer(JsonElement index, string field)
        => index.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == field)
            .TryGetProperty("analyzer", out var analyzer) ? analyzer.GetString() : null;

    // The culture of each document written, by the index it was written to.
    private static Dictionary<string, string[]> Writes(FakeSearchService service)
        => service.Requests
            .Where(r => r.Path.Contains("search.index"))
            .GroupBy(r => IndexName(r.Path)!)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("value").EnumerateArray())
                    .Where(d => d.GetProperty("@search.action").GetString() == "upload")
                    .Select(d => d.GetProperty("culture").GetString()!)
                    .ToArray());

    private static Task IndexAsync(FakeSearchService service, params Variation[] variations)
        => service.CreateServices().GetRequiredService<IAzureSearchIndexer>().AddOrUpdateAsync(
            IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, variations,
            variations.Select(v => new IndexField("title", new IndexValue { TextsR1 = ["text"] }, v.Culture, null)).ToArray(), null);

    private static async Task StartAsync(FakeSearchService service)
    {
        foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task CreatesOneIndexPerMappedCultureWithItsAnalyzer()
    {
        var service = Service(["fr-FR", "en-US"], existingIndexes: []);

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        var fr = Created(service, Fr);
        Assert.Multiple(() =>
        {
            Assert.That(CreatedIndexes(service), Is.EquivalentTo(new[] { Fr, En }), "Every language is mapped, so no fallback index.");
            Assert.That(Analyzer(fr, "allTextsR1"), Is.EqualTo("fr.microsoft"));
            Assert.That(Analyzer(fr, "suggestion"), Is.EqualTo("fr.microsoft"));
            Assert.That(Analyzer(Created(service, En), "allTexts"), Is.EqualTo("en.microsoft"));
        });
    }

    [Test]
    public async Task CreatesAFallbackIndexForALanguageOutsideTheMap()
    {
        var service = Service(["fr-FR", "en-US", "da-DK"], existingIndexes: []);

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(CreatedIndexes(service), Is.EquivalentTo(new[] { Fr, En, Fallback }));
            Assert.That(Analyzer(Created(service, Fallback), "allTextsR1"), Is.EqualTo("standard.lucene"));
        });
    }

    [Test]
    public async Task LogsRebuildRequiredWhenAFallbackIndexIsAddedToExistingCultureIndexes()
    {
        var service = Service(["fr-FR", "en-US", "da-DK"], existingIndexes: [Fr, En]);

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(CreatedIndexes(service), Is.EqualTo(new[] { Fallback }));
            Assert.That(service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message),
                Has.One.Contains("Rebuild required").And.Contains(Fallback));
        });
    }

    [Test]
    public async Task WritesEachCultureToItsIndexAndInvariantDocumentsToEveryIndex()
    {
        var service = Service(["fr-FR", "en-US", "da-DK"]);

        await IndexAsync(service, new Variation("fr-FR", null), new Variation("da-DK", null), new Variation(null, null));

        var writes = Writes(service);
        Assert.Multiple(() =>
        {
            Assert.That(writes[Fr], Is.EquivalentTo(new[] { "fr-fr", "inv" }));
            Assert.That(writes[En], Is.EquivalentTo(new[] { "inv" }));
            Assert.That(writes[Fallback], Is.EquivalentTo(new[] { "da-dk", "inv" }));
        });
    }

    [Test]
    public async Task WritesInvariantDocumentsOnlyToTheCultureIndexesWithoutAFallback()
    {
        var service = Service(["fr-FR", "en-US"]);

        await IndexAsync(service, new Variation(null, null));

        Assert.That(Writes(service).Keys, Is.EquivalentTo(new[] { Fr, En }));
    }

    // An unpublished culture gets nothing to write, but its documents are in its own index. The index gets the deletes of
    // its cultures and of the invariant document, without a search. The id after the content key is the variation.
    [Test]
    public async Task DeletesTheDocumentsOfARemovedCultureFromItsIndex()
    {
        var service = Service(["fr-FR", "en-US"]);

        await IndexAsync(service, new Variation("fr-FR", null));

        var deletes = service.Requests
            .Where(r => r.Path.Contains("search.index"))
            .SelectMany(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("value").EnumerateArray()
                .Where(d => d.GetProperty("@search.action").GetString() == "delete")
                .Select(d => (Index: IndexName(r.Path), Id: d.GetProperty("id").GetString()!.Split('_', 2)[1])))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(deletes, Is.EquivalentTo(new[] { (En, "en-us_def"), (En, "inv_def"), (Fr, "inv_def") }));
            Assert.That(service.Requests.Where(r => r.Path.Contains("search.post.search")), Is.Empty);
        });
    }

    [Test]
    public async Task CreatesTheFallbackIndexOnTheFirstDocumentOfAnUnmappedCulture()
    {
        var service = Service(["fr-FR", "en-US"], existingIndexes: [Fr, En]);

        await IndexAsync(service, new Variation("da-DK", null));

        Assert.Multiple(() =>
        {
            Assert.That(CreatedIndexes(service), Is.EqualTo(new[] { Fallback }));
            Assert.That(Writes(service)[Fallback], Is.EqualTo(new[] { "da-dk" }));
        });
    }

    [TestCase("fr-FR", Fr)]
    [TestCase("FR-ca", Fr)]
    [TestCase("da-DK", Fallback)]
    public async Task RoutesAQueryToTheIndexOfItsCulture(string culture, string index)
    {
        var service = Service(["fr-FR", "en-US", "da-DK"]);

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "avion", culture: culture, maxSuggestions: 5);

        var searches = service.Requests.Where(r => r.Path.Contains("search.post.search") || r.Path.Contains("search.post.autocomplete")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(searches.Select(r => IndexName(r.Path)).Distinct(), Is.EqualTo(new[] { index }));
            Assert.That(service.LastFilter(), Does.Contain("culture eq 'inv'"));
        });
    }

    [Test]
    public void RejectsAQueryWithoutACulture()
    {
        var service = Service(["fr-FR"]);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateSearcher().SearchAsync(IndexAlias, query: "avion"));

        Assert.That(exception!.Message, Does.Contain("needs a culture"));
    }

    [Test]
    public async Task ResetsEveryIndexOfTheAlias()
    {
        var service = Service(["fr-FR", "en-US", "da-DK"], existingIndexes: [Fr, En]);

        await service.CreateServices().GetRequiredService<IAzureSearchIndexer>().ResetAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(service.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => IndexName(r.Path)), Is.EquivalentTo(new[] { Fr, En, Fallback }));
            Assert.That(CreatedIndexes(service), Is.EquivalentTo(new[] { Fr, En, Fallback }));
        });
    }

    [Test]
    public async Task DeletesFromEveryIndexOfTheAlias()
    {
        var service = Service(["fr-FR", "en-US", "da-DK"], existingIndexes: [Fr, En]);

        await service.CreateServices().GetRequiredService<IIndexer>().DeleteAsync(IndexAlias, [Guid.NewGuid()]);

        Assert.That(service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => IndexName(r.Path)),
            Is.EquivalentTo(new[] { Fr, En, Fallback }));
    }

    // A plain rebuild empties every index of the alias, so its deletes send no request there. A language added during the
    // rebuild needs the fallback index, which the reset did not empty. The delete searches it.
    [Test]
    public async Task APlainRebuildDeleteSearchesOnlyAnIndexTheResetDidNotEmpty()
    {
        var languages = new LanguageSource(["fr-FR", "en-US"]);
        var service = Service([], existingIndexes: [Fr, En], languageSource: languages);
        var services = service.CreateServices();
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        languages.Languages = ["fr-FR", "en-US", "da-DK"];
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Guid.NewGuid()]);

        Assert.That(service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => IndexName(r.Path)),
            Is.EqualTo(new[] { Fallback }));
    }

    // A zero-downtime rebuild empties the new slots only. A fallback index that a language added during the rebuild needs
    // has no slot, so the delete searches the live fallback index.
    [Test]
    public async Task AZeroDowntimeRebuildDeleteSearchesOnlyAnIndexTheResetDidNotEmpty()
    {
        var languages = new LanguageSource(["fr-FR", "en-US"]);
        var service = Service([], index =>
        {
            IndexPerCulture(index);
            index.ZeroDowntimeRebuild();
        }, existingIndexes: [Fr, En], languageSource: languages);
        var indexer = service.CreateServices().GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        languages.Languages = ["fr-FR", "en-US", "da-DK"];
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Guid.NewGuid()]);

        Assert.That(service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => IndexName(r.Path)),
            Is.EqualTo(new[] { Fallback }));
    }

    [Test]
    public async Task CountsTheDocumentsOfEveryIndex()
    {
        var service = Service(["fr-FR", "en-US"]);
        service.Respond = request => request.RequestUri!.AbsolutePath.Contains("search.stats")
            ? FakeSearchService.Json("""{"documentCount":3,"storageSize":0}""")
            : null;

        var metadata = await service.CreateServices().GetRequiredService<IIndexer>().GetMetadataAsync(IndexAlias);

        Assert.That(metadata.DocumentCount, Is.EqualTo(6));
    }

    [Test]
    public void StopsStartupForIndexPerCultureOnABackofficeIndex()
    {
        var service = Service(["fr-FR"]);
        service.ConfigureServices += services => services.ConfigureAzureSearchIndex("Umb_Content", IndexPerCulture);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("not allowed on the backoffice index \"Umb_Content\""));
    }

    [Test]
    public void StopsStartupWhenTheIndexesExceedTheTierLimit()
    {
        var service = Service(["fr-FR", "en-US", "da-DK"]);
        service.Settings["UmbracoAzureSearch:MaxIndexes"] = "5";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        // Three other default indexes, plus fr, en and the fallback index.
        Assert.That(exception!.Message, Does.Contain("need 6 Azure indexes").And.Contains("Umb_PublishedContent: 3").And.Contains("allows 5"));
    }

    [Test]
    public void AcceptsIndexPerCultureWithinTheLimit()
    {
        var service = Service(["fr-FR", "en-US"]);

        Assert.DoesNotThrowAsync(() => StartAsync(service));
    }

    private sealed class LanguageSource(string[] languages) : IUmbracoContentSource
    {
        public string[] Languages { get; set; } = languages;

        public Guid? GetContentTypeKey(string alias, UmbracoObjectTypes objectType) => null;

        public IContentBase? GetContent(Guid id, UmbracoObjectTypes objectType) => null;

        public Task<string[]> GetLanguageIsoCodesAsync() => Task.FromResult(Languages);
    }
}
