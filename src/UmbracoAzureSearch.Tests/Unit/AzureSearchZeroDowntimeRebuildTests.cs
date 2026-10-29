using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Models.Persistence;
using Umbraco.Cms.Search.Core.Notifications;
using Umbraco.Cms.Search.Core.Services;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchZeroDowntimeRebuildTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string Name = "umb-publishedcontent";
    private const string SlotA = "umb-publishedcontent-a";
    private const string SlotB = "umb-publishedcontent-b";

    private static readonly Action<AzureSearchIndexBuilder> ZeroDowntime = index => index.ZeroDowntimeRebuild(r => r.DeleteDelay(TimeSpan.Zero));

    private static (FakeSearchService Service, AzureState Azure) Service(Action<AzureSearchIndexBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var azure = new AzureState();
        var service = new FakeSearchService
        {
            ConfigureServices = collection =>
            {
                collection.ConfigureAzureSearchIndex(IndexAlias, configure ?? ZeroDowntime);
                services?.Invoke(collection);
            },
        };
        service.Respond = azure.Respond;
        return (service, azure);
    }

    private static Task CompleteAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => services.GetServices<INotificationAsyncHandler<IndexRebuildCompletedNotification>>().Single()
            .HandleAsync(new IndexRebuildCompletedNotification(IndexAlias), cancellationToken);

    private static Task WriteAsync(IServiceProvider services)
        => services.GetRequiredService<IIndexer>().AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document,
            [new Variation(null, null)], [new IndexField("title", new IndexValue { TextsR1 = ["Red boat"] }, null, null)], null);

    private static string[] Messages(FakeSearchService service, LogLevel level)
        => service.Logs.Where(l => l.Level == level).Select(l => l.Message).ToArray();

    [Test]
    public async Task CreatesANewIndexAsSlotABehindAnAlias()
    {
        var (service, azure) = Service();

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotA }));
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA));
        });
    }

    [Test]
    public async Task KeepsAPlainIndexUntilTheFirstRebuild()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(Name);

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { Name }));
            Assert.That(azure.Aliases, Is.Empty);
        });
    }

    [Test]
    public async Task DeletesAHalfBuiltSlotAtStartup()
    {
        var (service, azure) = Service();
        azure.Indexes.UnionWith([SlotA, SlotB]);
        azure.Aliases[Name] = SlotA;

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotA }));
            Assert.That(Messages(service, LogLevel.Warning), Has.One.Contains($"half-built index {SlotB}"));
        });
    }

    [Test]
    public async Task ARebuildFillsTheInactiveSlotWhileSearchesUseTheAlias()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(SlotA);
        azure.Aliases[Name] = SlotA;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        await WriteAsync(services);
        // The write looks up removed variations in both slots; only the search below is checked.
        azure.Searches.Clear();
        await services.GetRequiredService<IAzureSearchSearcher>().SearchAsync(IndexAlias, query: "red");

        Assert.Multiple(() =>
        {
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotA, SlotB }));
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA), "The alias moves only at the swap.");
            Assert.That(azure.Writes.Keys, Is.EquivalentTo(new[] { SlotA, SlotB }), "A write during the rebuild goes to both slots.");
            Assert.That(azure.Searches, Is.EqualTo(new[] { Name }), "Searches use the alias.");
            Assert.That(Messages(service, LogLevel.Information), Has.One.Contains("Rebuild started").And.Contains(SlotB).And.Contains(SlotA));
        });
    }

    // The cleanup of a culture the content no longer has must reach the new slot too, or the document returns at the swap.
    [Test]
    public async Task ARebuildCleansUpRemovedCulturesInBothSlots()
    {
        var (service, azure) = Service(index => ZeroDowntime(index.Cultures("da-DK")));
        azure.Indexes.Add(SlotA);
        azure.Aliases[Name] = SlotA;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        await services.GetRequiredService<IIndexer>().AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document,
            [new Variation("en-US", null)], [new IndexField("title", new IndexValue { TextsR1 = ["Red boat"] }, "en-US", null)], null);

        Assert.That(azure.Searches, Is.EquivalentTo(new[] { SlotA, SlotB }));
    }

    [Test]
    public async Task SwapsAndDeletesTheOldSlotWhenTheRebuildSucceeds()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(SlotA);
        azure.Aliases[Name] = SlotA;
        azure.Counts[SlotA] = 100;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        azure.Counts[SlotB] = 98;
        await CompleteAsync(services);
        azure.Writes.Clear();
        await WriteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotB));
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotB }));
            Assert.That(azure.Writes.Keys, Is.EquivalentTo(new[] { SlotB }), "After the swap, writes go to the new slot only.");
            Assert.That(Messages(service, LogLevel.Information), Has.One.Contains("Swap done").And.Contains("100").And.Contains("98"));
            Assert.That(Messages(service, LogLevel.Information), Has.One.Contains("Old index deleted").And.Contains(SlotA));
        });
    }

    [Test]
    public async Task SkipsTheSwapOfACancelledRebuild()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(SlotA);
        azure.Aliases[Name] = SlotA;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        await CompleteAsync(services, new CancellationToken(canceled: true));

        Assert.Multiple(() =>
        {
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA));
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotA }));
            Assert.That(Messages(service, LogLevel.Warning), Has.One.Contains("Swap skipped").And.Contains("cancelled"));
        });
    }

    [Test]
    public async Task SkipsTheSwapWhenTheCountCheckFails()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(SlotA);
        azure.Aliases[Name] = SlotA;
        azure.Counts[SlotA] = 100;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        azure.Counts[SlotB] = 49;
        await CompleteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA));
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotA }));
            Assert.That(Messages(service, LogLevel.Warning), Has.One.Contains("count check").And.Contains("100/49"));
        });
    }

    [Test]
    public async Task SwapsWithAnyCountWhenTheCountCheckIsOff()
    {
        var (service, azure) = Service(index => index.ZeroDowntimeRebuild(r => r.MinimumDocumentPercent(0).DeleteDelay(TimeSpan.Zero)));
        azure.Indexes.Add(SlotA);
        azure.Aliases[Name] = SlotA;
        azure.Counts[SlotA] = 100;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        await CompleteAsync(services);

        Assert.That(azure.Aliases[Name], Is.EqualTo(SlotB));
    }

    [Test]
    public async Task TheFirstRebuildReplacesAPlainIndexWithAnAlias()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(Name);
        azure.Counts[Name] = 10;
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        azure.Counts[SlotA] = 10;
        await CompleteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { SlotA }));
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA));
        });
    }

    [Test]
    public async Task EachCultureIndexGetsItsOwnAliasAndSlots()
    {
        var (service, azure) = Service(
            index => index
                .Analyzers(a => a.IndexPerCulture().Culture("fr", "fr.microsoft").Culture("en", "en.microsoft"))
                .ZeroDowntimeRebuild(r => r.DeleteDelay(TimeSpan.Zero)),
            collection => collection.AddSingleton<IUmbracoContentSource>(new LanguageSource(["fr-FR", "en-US"])));
        var services = service.CreateServices();

        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);
        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        await CompleteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Aliases, Is.EquivalentTo(new Dictionary<string, string>
            {
                [$"{Name}-fr"] = $"{Name}-fr-b",
                [$"{Name}-en"] = $"{Name}-en-b",
            }));
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { $"{Name}-fr-b", $"{Name}-en-b" }));
        });
    }

    [Test]
    public async Task ServerlessIgnoresTheOption()
    {
        var (service, azure) = Service();
        service.Settings["UmbracoAzureSearch:IsServerless"] = "true";
        azure.Indexes.Add(Name);
        var services = service.CreateServices();

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);
        foreach (var hostedService in services.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        Assert.Multiple(() =>
        {
            Assert.That(azure.Indexes, Is.EquivalentTo(new[] { Name }));
            Assert.That(azure.Aliases, Is.Empty);
            Assert.That(Messages(service, LogLevel.Warning), Has.One.Contains("ZeroDowntimeRebuild has no effect"));
        });
    }

    [Test]
    public async Task ClearsTheDocumentCacheWhenARebuildStarts()
    {
        var cache = new DocumentCache();
        var (service, azure) = Service(
            index => index.ClearDocumentCacheOnRebuild(),
            collection => collection.AddSingleton<IIndexDocumentService>(cache));
        azure.Indexes.Add(Name);

        await service.CreateServices().GetRequiredService<IIndexer>().ResetAsync(IndexAlias);

        Assert.That(cache.Cleared, Is.EqualTo(1));
    }

    [Test]
    public async Task ReportsTheMetadataOfTheLiveSlot()
    {
        var (service, azure) = Service();
        azure.Indexes.Add(SlotB);
        azure.Aliases[Name] = SlotB;

        await service.CreateServices().GetRequiredService<IIndexer>().GetMetadataAsync(IndexAlias);

        Assert.That(azure.Statistics, Is.EqualTo(new[] { SlotB }));
    }

    // A search service with indexes, aliases, document counts, and the requests that wrote or searched.
    private sealed class AzureState
    {
        public HashSet<string> Indexes { get; } = [];
        public Dictionary<string, string> Aliases { get; } = [];
        public Dictionary<string, long> Counts { get; } = [];
        public Dictionary<string, int> Writes { get; } = [];
        public List<string> Searches { get; } = [];
        public List<string> Statistics { get; } = [];

        public HttpResponseMessage? Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method;
            var index = Name("/indexes('", path);
            var alias = Name("/aliases('", path);

            if (alias is not null)
            {
                if (method == HttpMethod.Get)
                    return Aliases.TryGetValue(alias, out var target)
                        ? FakeSearchService.Json($$"""{"name":"{{alias}}","indexes":["{{target}}"]}""")
                        : NotFound();
                if (method == HttpMethod.Put)
                {
                    Aliases[alias] = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("indexes")[0].GetString()!;
                    return FakeSearchService.Json(request.Content.ReadAsStringAsync().Result);
                }

                return Aliases.Remove(alias) ? new HttpResponseMessage(HttpStatusCode.NoContent) : NotFound();
            }

            if (index is null)
            {
                if (method == HttpMethod.Get && path.EndsWith("/indexes"))
                    return FakeSearchService.Json($$"""{"value":[{{string.Join(",", Indexes.Select(n => $$"""{"name":"{{n}}","fields":[]}"""))}}]}""");
                if (method == HttpMethod.Post && path.EndsWith("/indexes"))
                {
                    var body = request.Content!.ReadAsStringAsync().Result;
                    Indexes.Add(JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!);
                    return FakeSearchService.Json(body, HttpStatusCode.Created);
                }

                return null;
            }

            // Documents go through an alias or an index name.
            var physical = Aliases.TryGetValue(index, out var aliased) ? aliased : index;
            if (path.Contains("search.post.search"))
            {
                Searches.Add(index);
                return FakeSearchService.Json($$"""{"@odata.count":{{Counts.GetValueOrDefault(physical)}},"value":[]}""");
            }

            if (path.Contains("search.index"))
            {
                Writes[physical] = Writes.GetValueOrDefault(physical) + 1;
                return FakeSearchService.Json("""{"value":[]}""");
            }

            if (path.Contains("search.stats"))
            {
                Statistics.Add(index);
                return FakeSearchService.Json("""{"documentCount":1,"storageSize":0}""");
            }

            if (!Indexes.Contains(index))
                return NotFound();
            if (method == HttpMethod.Get)
                return FakeSearchService.Json($$"""{"name":"{{index}}","fields":[{"name":"id","type":"Edm.String","key":true}]}""");
            if (method == HttpMethod.Delete)
            {
                Indexes.Remove(index);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return FakeSearchService.Json(request.Content!.ReadAsStringAsync().Result);
        }

        private static string? Name(string prefix, string path)
        {
            var start = path.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
                return null;
            start += prefix.Length;
            return path[start..path.IndexOf("')", start, StringComparison.Ordinal)];
        }

        private static HttpResponseMessage NotFound()
            => FakeSearchService.Json("""{"error":{"code":"","message":"Not found"}}""", HttpStatusCode.NotFound);
    }

    private sealed class LanguageSource(string[] languages) : IUmbracoContentSource
    {
        public Guid? GetContentTypeKey(string alias, UmbracoObjectTypes objectType) => null;

        public IContentBase? GetContent(Guid id, UmbracoObjectTypes objectType) => null;

        public Task<string[]> GetLanguageIsoCodesAsync() => Task.FromResult(languages);
    }

    private sealed class DocumentCache : IIndexDocumentService
    {
        public int Cleared { get; private set; }

        public Task AddAsync(IndexDocument indexDocument) => Task.CompletedTask;

        public Task DeleteAsync(Guid[] ids, bool published) => Task.CompletedTask;

        public Task<IndexDocument?> GetAsync(Guid id, bool published) => Task.FromResult<IndexDocument?>(null);

        public Task DeleteAllAsync()
        {
            Cleared++;
            return Task.CompletedTask;
        }

        public Task DeleteCulturesAsync(IReadOnlyCollection<string> isoCodes) => Task.CompletedTask;
    }
}
