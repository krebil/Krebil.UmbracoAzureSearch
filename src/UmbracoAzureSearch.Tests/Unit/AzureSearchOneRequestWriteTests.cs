using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

// A write sends the documents of the variations the content has as uploads, and the documents of every other variation
// it can have as deletes, in one request. Document ids come from the content key, the culture and the segment, so the
// package knows them from the Umbraco languages without a search.
[TestFixture]
public class AzureSearchOneRequestWriteTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string Name = "umb-publishedcontent";

    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) Create(
        string[] languages, Action<IServiceCollection>? configure = null)
    {
        var azure = new FakeAzureIndexes();
        var service = new FakeSearchService
        {
            Respond = azure.Respond,
            ConfigureServices = services =>
            {
                services.AddSingleton<IUmbracoContentSource>(new LanguageSource(languages));
                configure?.Invoke(services);
            },
        };
        var provider = service.CreateServices();
        azure.AddIndex(Name);
        return (service, azure, provider);
    }

    private static Task WriteAsync(IServiceProvider services, Guid id, params Variation[] variations)
        => services.GetRequiredService<IIndexer>().AddOrUpdateAsync(IndexAlias, id, UmbracoObjectTypes.Document, variations,
            variations.Select(v => new IndexField("title", new IndexValue { Keywords = ["Red boat"] }, v.Culture, v.Segment)).ToArray(), null);

    private static Variation[] Cultures(params string[] cultures) => cultures.Select(c => new Variation(c, null)).ToArray();

    // The first write of an index reads the schema and adds the fields; later writes know them.
    private static async Task WarmUpAsync(FakeSearchService service, FakeAzureIndexes azure, IServiceProvider services, params string[] cultures)
    {
        await WriteAsync(services, Guid.NewGuid(), Cultures(cultures.Length > 0 ? cultures : ["en-US", "da-DK"]));
        lock (service.Requests)
        {
            service.Requests.Clear();
        }

        azure.Searches.Clear();
    }

    private static (string Action, string Id)[] Actions((HttpMethod Method, string Path, string? Body) request)
        => JsonDocument.Parse(request.Body!).RootElement.GetProperty("value").EnumerateArray()
            .Select(d => (d.GetProperty("@search.action").GetString()!, d.GetProperty("id").GetString()!))
            .ToArray();

    [Test]
    public async Task AWriteOfEveryCultureSendsOneRequest()
    {
        var (service, azure, services) = Create(["en-US", "da-DK"]);
        await WarmUpAsync(service, azure, services);
        var id = Guid.NewGuid();

        await WriteAsync(services, id, Cultures("en-US", "da-DK"));

        Assert.That(service.Requests, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(service.Requests[0].Path, Does.Contain("search.index"));
            Assert.That(Actions(service.Requests[0]), Is.EquivalentTo(new[]
            {
                ("upload", $"{id.AsKeyword()}_en-us_def"),
                ("upload", $"{id.AsKeyword()}_da-dk_def"),
                ("delete", $"{id.AsKeyword()}_inv_def"),
            }));
        });
    }

    [Test]
    public async Task AWriteDeletesARemovedCultureInTheSameRequest()
    {
        var (service, azure, services) = Create(["en-US", "da-DK"]);
        await WarmUpAsync(service, azure, services);
        var id = Guid.NewGuid();
        await WriteAsync(services, id, Cultures("en-US", "da-DK"));
        lock (service.Requests)
        {
            service.Requests.Clear();
        }

        await WriteAsync(services, id, Cultures("en-US"));

        Assert.That(service.Requests, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(Actions(service.Requests[0]), Does.Contain(("upload", $"{id.AsKeyword()}_en-us_def")));
            Assert.That(Actions(service.Requests[0]), Does.Contain(("delete", $"{id.AsKeyword()}_da-dk_def")));
            Assert.That(azure.Documents[Name], Does.Not.Contain($"{id.AsKeyword()}_da-dk_def"));
            Assert.That(azure.Searches, Is.Empty);
        });
    }

    // Content that changes from culture variant to invariant leaves its culture documents behind, and the reverse.
    [Test]
    public async Task AnInvariantWriteDeletesTheCultureDocuments()
    {
        var (service, azure, services) = Create(["en-US", "da-DK"]);
        await WarmUpAsync(service, azure, services);
        var id = Guid.NewGuid();

        await WriteAsync(services, id, new Variation(null, null));

        Assert.That(Actions(service.Requests.Single()), Is.EquivalentTo(new[]
        {
            ("upload", $"{id.AsKeyword()}_inv_def"),
            ("delete", $"{id.AsKeyword()}_en-us_def"),
            ("delete", $"{id.AsKeyword()}_da-dk_def"),
        }));
    }

    // The package cannot list the segments up front, so a write with segments looks up the documents of the content.
    [Test]
    public async Task AWriteWithASegmentLooksUpRemovedVariations()
    {
        var (service, azure, services) = Create(["en-US"]);
        await WarmUpAsync(service, azure, services);
        var id = Guid.NewGuid();

        await WriteAsync(services, id, new Variation("en-US", null), new Variation("en-US", "vip"));

        Assert.Multiple(() =>
        {
            Assert.That(azure.Searches, Is.EqualTo(new[] { Name }));
            Assert.That(Actions(service.Requests.First(r => r.Path.Contains("search.index"))).Select(a => a.Action), Has.All.EqualTo("upload"));
        });
    }

    // With segments on in Umbraco, content that no longer varies by segment can still have segment documents.
    [Test]
    public async Task AWriteLooksUpRemovedVariationsWhenUmbracoUsesSegments()
    {
        var (service, azure, services) = Create(["en-US"], services => services.Configure<SegmentSettings>(s => s.Enabled = true));
        await WarmUpAsync(service, azure, services);

        await WriteAsync(services, Guid.NewGuid(), Cultures("en-US"));

        Assert.That(azure.Searches, Is.EqualTo(new[] { Name }));
    }

    // Without the languages (e.g. before Umbraco is installed) the candidates are unknown.
    [Test]
    public async Task AWriteLooksUpRemovedVariationsWhenTheLanguagesAreUnknown()
    {
        var (service, azure, services) = Create(["en-US"], services => services.AddSingleton<IUmbracoContentSource>(new LanguageSource(null)));
        await WarmUpAsync(service, azure, services);

        await WriteAsync(services, Guid.NewGuid(), Cultures("en-US"));

        Assert.That(azure.Searches, Is.EqualTo(new[] { Name }));
    }

    // The content keeps a culture outside the index, so nothing is written, but the culture the index held is gone.
    [Test]
    public async Task AWriteWithNoCultureInTheIndexDeletesTheOtherCulturesInOneRequest()
    {
        var (service, azure, services) = Create(["en-US", "da-DK"],
            services => services.ConfigureAzureSearchIndex(IndexAlias, index => index.Cultures("da-DK")));
        await WarmUpAsync(service, azure, services);
        var id = Guid.NewGuid();

        await WriteAsync(services, id, Cultures("en-US"));

        Assert.That(service.Requests, Has.Count.EqualTo(1));
        Assert.That(Actions(service.Requests[0]), Is.EquivalentTo(new[]
        {
            ("delete", $"{id.AsKeyword()}_en-us_def"),
            ("delete", $"{id.AsKeyword()}_da-dk_def"),
            ("delete", $"{id.AsKeyword()}_inv_def"),
        }));
    }

    // A failed delete does not fail the write: the upload went in the same request.
    [Test]
    public async Task LogsAFailedDeleteAndKeepsTheUpload()
    {
        var (service, azure, services) = Create(["en-US", "da-DK"]);
        await WarmUpAsync(service, azure, services);
        var id = Guid.NewGuid();
        azure.FailingDocuments[$"{id.AsKeyword()}_da-dk_def"] = "Broken";

        await WriteAsync(services, id, Cultures("en-US"));

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents[Name], Does.Contain($"{id.AsKeyword()}_en-us_def"));
            Assert.That(service.Logs, Has.Some.Matches<(LogLevel Level, string Message)>(
                log => log.Level == LogLevel.Error && log.Message.Contains("removed variation") && log.Message.Contains("da-dk")));
            Assert.That(service.Logs, Has.None.Matches<(LogLevel Level, string Message)>(
                log => log.Level == LogLevel.Error && log.Message.Contains("Failed to index")));
        });
    }

    // IndexPerCulture: each index gets the deletes of its own cultures, and of the invariant document.
    [Test]
    public async Task IndexPerCultureDeletesTheCandidatesOfEachIndexInItsOwnRequest()
    {
        var (service, azure, services) = Create(["fr-FR", "en-US", "da-DK"], services => services.ConfigureAzureSearchIndex(IndexAlias,
            index => index.Analyzers(a => a.IndexPerCulture().Culture("fr", "fr.microsoft").Culture("en", "en.microsoft"))));
        foreach (var index in new[] { $"{Name}-fr", $"{Name}-en", $"{Name}-fallback" })
        {
            azure.AddIndex(index);
        }
        await WarmUpAsync(service, azure, services, "fr-FR", "en-US", "da-DK");
        var id = Guid.NewGuid();

        await WriteAsync(services, id, Cultures("fr-FR"));

        var requests = service.Requests.Where(r => r.Path.Contains("search.index"))
            .ToDictionary(r => r.Path.Split("indexes('")[1].Split("')")[0], Actions);
        var key = id.AsKeyword();
        Assert.Multiple(() =>
        {
            Assert.That(service.Requests, Has.Count.EqualTo(3));
            Assert.That(requests[$"{Name}-fr"], Is.EquivalentTo(new[] { ("upload", $"{key}_fr-fr_def"), ("delete", $"{key}_inv_def") }));
            Assert.That(requests[$"{Name}-en"], Is.EquivalentTo(new[] { ("delete", $"{key}_en-us_def"), ("delete", $"{key}_inv_def") }));
            Assert.That(requests[$"{Name}-fallback"], Is.EquivalentTo(new[] { ("delete", $"{key}_da-dk_def"), ("delete", $"{key}_inv_def") }));
        });
    }

    // During a zero-downtime rebuild a publish goes to both slots, each with its deletes. Rebuild writes go to the new
    // slot, which starts empty, so they send no deletes.
    [Test]
    public async Task APublishDuringARebuildSendsOneRequestPerSlot()
    {
        const string slotA = $"{Name}-a", slotB = $"{Name}-b";
        var (service, azure, services) = Create(["en-US", "da-DK"], services => services.ConfigureAzureSearchIndex(IndexAlias,
            index => index.ZeroDowntimeRebuild(r => r.DeleteDelay(TimeSpan.Zero).MinimumDocumentPercent(0))));
        services.GetRequiredService<IndexSchemaUpdates>().RetryDelay = _ => TimeSpan.Zero;
        azure.AddIndex(slotA);
        azure.Aliases[Name] = slotA;
        var indexer = services.GetRequiredService<IIndexer>();
        var publish = Guid.NewGuid();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RebuildAsync()
        {
            await indexer.ResetAsync(IndexAlias);
            await WriteAsync(services, Guid.NewGuid(), Cultures("en-US", "da-DK"));
            paused.SetResult();
            await resume.Task;
            await WriteAsync(services, Guid.NewGuid(), Cultures("en-US", "da-DK"));
        }

        var rebuild = RebuildAsync();
        await paused.Task;
        lock (service.Requests)
        {
            service.Requests.Clear();
        }

        await WriteAsync(services, publish, Cultures("en-US"));
        var publishRequests = service.Requests.ToArray();
        resume.SetResult();
        await rebuild;

        var deleteOfDanish = ("delete", $"{publish.AsKeyword()}_da-dk_def");
        Assert.Multiple(() =>
        {
            Assert.That(publishRequests.Where(r => r.Path.Contains("search.index")).Select(r => r.Path.Split("indexes('")[1].Split("')")[0]),
                Is.EquivalentTo(new[] { slotA, slotB }));
            Assert.That(publishRequests.Where(r => r.Path.Contains("search.index")).Select(Actions), Has.All.Contains(deleteOfDanish));
            Assert.That(azure.Searches, Is.Empty);
            Assert.That(service.Requests.Except(publishRequests).Where(r => r.Path.Contains("search.index")).SelectMany(Actions).Select(a => a.Action),
                Has.All.EqualTo("upload"), "Rebuild writes send no deletes.");
        });
    }

    private sealed class LanguageSource(string[]? languages) : IUmbracoContentSource
    {
        public Guid? GetContentTypeKey(string alias, UmbracoObjectTypes objectType) => null;

        public IContentBase? GetContent(Guid id, UmbracoObjectTypes objectType) => null;

        public Task<string[]> GetLanguageIsoCodesAsync()
            => languages is null ? throw new InvalidOperationException("No languages") : Task.FromResult(languages);
    }
}
