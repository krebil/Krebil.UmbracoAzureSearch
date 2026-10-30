using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;
using static UmbracoAzureSearch.Tests.Unit.AzureSearchRebuildWriteTests;

namespace UmbracoAzureSearch.Tests.Unit;

// With RebuildBatchSize, a rebuild sends its writes in requests of many documents. Only rebuild writes wait in a batch.
[TestFixture]
public class AzureSearchRebuildBatchTests
{
    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) CreateBatched(
        int batchSize, int minimumPercent = 0, bool zeroDowntime = true)
    {
        var created = Create(index =>
        {
            index.Rebuild(r => r.BatchSize(batchSize).MinimumDocumentPercent(minimumPercent).DeleteDelay(TimeSpan.Zero));
            if (zeroDowntime)
                index.ZeroDowntimeRebuild();
        });
        var writer = created.Services.GetRequiredService<DocumentWriter>();
        writer.RetryDelay = _ => TimeSpan.Zero;
        writer.SearchableWait = (TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
        return created;
    }

    // A publish or site code: another async flow than the rebuild.
    private static Task OutsideTheRebuild(Func<Task> write)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(write);
        }
    }

    private static Guid[] Items(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

    private static string DocumentId(Guid id) => $"{id.AsKeyword()}_inv_def";

    private static string[] Errors(FakeSearchService service)
        => service.Logs.Where(l => l.Level == LogLevel.Error).Select(l => l.Message).ToArray();

    [Test]
    public async Task SendsRebuildWritesInBatches()
    {
        var (_, azure, services) = CreateBatched(3);

        await RebuildAsync(services, Items(7));
        var beforeCompletion = azure.Uploads[SlotB].ToArray();
        await CompleteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(beforeCompletion, Is.EqualTo(new[] { 3, 3 }));
            Assert.That(azure.Uploads[SlotB], Is.EqualTo(new[] { 3, 3, 1 }), "The rest goes when the rebuild completes.");
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotB));
        });
    }

    [Test]
    public async Task ABatchSizeOfOneSendsEachItem()
    {
        var (_, azure, services) = Create();

        await RebuildAsync(services, Items(3));

        Assert.That(azure.Uploads[SlotB], Is.EqualTo(new[] { 1, 1, 1 }));
    }

    // A publish during the rebuild is written at once to both slots. The rebuild documents that wait go first, so an older
    // rebuild document never overwrites the publish.
    [Test]
    public async Task APublishDuringTheRebuildIsWrittenAtOnce()
    {
        var (_, azure, services) = CreateBatched(10);
        var items = Items(2);
        var publish = Guid.NewGuid();

        await RebuildAsync(services, items, () => OutsideTheRebuild(() => WriteAsync(services, publish)));

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents[SlotA], Is.EqualTo(new[] { DocumentId(publish) }));
            Assert.That(azure.Documents[SlotB], Is.EqualTo(new[] { DocumentId(items[0]), DocumentId(publish) }));
        });
    }

    [Test]
    public async Task ADeleteDuringTheRebuildSendsTheWaitingDocumentsFirst()
    {
        var (_, azure, services) = CreateBatched(10);
        var items = Items(2);

        await RebuildAsync(services, items, () => OutsideTheRebuild(() => services.GetRequiredService<IIndexer>().DeleteAsync(IndexAlias, [Guid.NewGuid()])));

        Assert.That(azure.Uploads[SlotB], Is.EqualTo(new[] { 1 }));
    }

    // The count check compares the documents of the old and the new index, so the last batch must be in the new index.
    [Test]
    public async Task TheCountCheckSeesTheLastBatch()
    {
        var (_, azure, services) = CreateBatched(10, minimumPercent: 100);
        azure.Documents[SlotA] = Items(4).Select(DocumentId).ToList();
        azure.StaleCounts[SlotB] = 2;

        await RebuildAsync(services, Items(4));
        await CompleteAsync(services);

        Assert.That(azure.Aliases[Name], Is.EqualTo(SlotB), "Azure counts new documents a moment after the write; the swap waits for them.");
    }

    [Test]
    public async Task ACancelledRebuildDropsTheWaitingDocuments()
    {
        var (service, azure, services) = CreateBatched(10);

        await RebuildAsync(services, Items(2));
        await CompleteAsync(services, new CancellationToken(canceled: true));

        Assert.Multiple(() =>
        {
            Assert.That(azure.Uploads.ContainsKey(SlotB), Is.False);
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA));
            Assert.That(service.Logs.Select(l => l.Message), Has.Some.Contains("Swap skipped").And.Contains("cancelled"));
        });
    }

    [Test]
    public async Task APlainRebuildSendsTheRestWhenItCompletes()
    {
        var (_, azure, services) = CreateBatched(10, zeroDowntime: false);
        azure.Aliases.Clear();
        azure.Indexes.Clear();
        azure.AddIndex(Name);

        await RebuildAsync(services, Items(3));
        await CompleteAsync(services);

        Assert.That(azure.Uploads[Name], Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public async Task ADocumentAzureRejectsIsLoggedAndTheOthersAreWritten()
    {
        var (service, azure, services) = CreateBatched(3);
        var items = Items(3);
        azure.FailingDocuments[DocumentId(items[1])] = "Document is too large";

        await RebuildAsync(services, items);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents[SlotB], Is.EquivalentTo(new[] { DocumentId(items[0]), DocumentId(items[2]) }));
            Assert.That(Errors(service), Has.One.Contains(DocumentId(items[1])).And.Contains("Document is too large"));
        });
    }

    // A request Azure rejects as a whole is split in halves, down to the document that fails alone.
    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.RequestEntityTooLarge)]
    public async Task ARejectedBatchIsSplitAndTheBrokenDocumentLeftOut(HttpStatusCode status)
    {
        var (service, azure, services) = CreateBatched(4);
        var items = Items(4);
        var broken = DocumentId(items[2]);
        azure.OnUpload = (_, ids) => ids.Contains(broken) ? FakeAzureIndexes.Error(status, "", "The request is invalid") : null;

        await RebuildAsync(services, items);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents[SlotB], Is.EquivalentTo(items.Select(DocumentId).Where(id => id != broken)));
            Assert.That(Errors(service), Has.One.Contains(broken).And.Contains("goes on"));
        });
    }

    [Test]
    public async Task AThrottledBatchIsRetriedWhole()
    {
        var (_, azure, services) = CreateBatched(4);
        var throttled = 0;
        azure.OnUpload = (_, _) => ++throttled <= 2
            ? FakeAzureIndexes.Error(HttpStatusCode.ServiceUnavailable, "", "Not enough resources to cover 100% of the index")
            : null;

        await RebuildAsync(services, Items(4));

        Assert.That(azure.Uploads[SlotB], Is.EqualTo(new[] { 4 }));
    }

    // The fields of all documents of a batch go into one schema update.
    [Test]
    public async Task ABatchUpdatesTheSchemaOnce()
    {
        var (_, azure, services) = CreateBatched(5);
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        for (var i = 0; i < 5; i++)
        {
            await indexer.AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, [new Variation(null, null)],
                [new IndexField($"field{i}", new IndexValue { Keywords = ["value"] }, null, null)], null);
        }

        Assert.Multiple(() =>
        {
            Assert.That(azure.Count(azure.SchemaUpdates, SlotB), Is.EqualTo(1));
            Assert.That(azure.FieldNames(SlotB), Is.SupersetOf(Enumerable.Range(0, 5).Select(i => $"field{i}_keywords")));
        });
    }

    [Test]
    public void ReadsTheBatchSizeFromAppSettingsAndCode()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = collection => collection.ConfigureAzureSearchIndex("Umb_Media", index => index.Rebuild(r => r.BatchSize(500))),
        };
        service.Settings["UmbracoAzureSearch:Indexes:Umb_PublishedContent:RebuildBatchSize"] = "250";

        var options = service.CreateServices().GetRequiredService<IOptionsMonitor<AzureSearchIndexOptions>>();

        Assert.Multiple(() =>
        {
            Assert.That(options.Get("Umb_PublishedContent").RebuildBatchSize, Is.EqualTo(250));
            Assert.That(options.Get("Umb_Media").RebuildBatchSize, Is.EqualTo(500));
            Assert.That(options.Get("Umb_Media").ZeroDowntimeRebuild, Is.False, "Rebuild() sets the rebuild without zero-downtime rebuild.");
            Assert.That(options.Get("Umb_Content").RebuildBatchSize, Is.EqualTo(1));
        });
    }

    [TestCase(0)]
    [TestCase(1001)]
    public void StopsStartupForABatchSizeOutOfRange(int batchSize)
    {
        var service = new FakeSearchService();
        service.Settings["UmbracoAzureSearch:Indexes:Umb_PublishedContent:RebuildBatchSize"] = batchSize.ToString();

        var exception = Assert.CatchAsync(async () =>
        {
            foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        });

        Assert.That(exception!.Message, Does.Contain($"RebuildBatchSize is {batchSize}. Use 1 to 1000"));
    }
}
