using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using static UmbracoAzureSearch.Tests.Unit.AzureSearchRebuildWriteTests;

namespace UmbracoAzureSearch.Tests.Unit;

// A delete searches the index for the documents of the item and its descendants (Umb_PathIds). A delete of a zero-downtime
// rebuild sends no request: the search would find nothing (see "Deletes of the rebuild" in zero-downtime-rebuild.md).
[TestFixture]
public class AzureSearchRebuildDeleteTests
{
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();

    // The fake service does not filter. A search by Umb_PathIds finds the parent and the child, as Azure would in an index
    // that holds both.
    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) CreateWithTree(
        Action<AzureSearchIndexBuilder>? configure = null)
    {
        var (service, azure, services) = Create(configure);
        service.Respond = request =>
        {
            var body = request.Content?.ReadAsStringAsync().Result;
            if (request.RequestUri!.AbsolutePath.Contains("search.post.search") && body?.Contains("Umb_PathIds_keywords") == true)
                return FakeSearchService.Json($$"""{"value":[{"id":"{{DocumentId(Parent)}}"},{"id":"{{DocumentId(Child)}}"}]}""");

            return azure.Respond(request);
        };
        return (service, azure, services);
    }

    private static string[] SearchedIndexes(FakeSearchService service)
    {
        lock (service.Requests)
        {
            return service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => r.Path.Split("indexes('")[1].Split("')")[0]).ToArray();
        }
    }

    // Without zero-downtime rebuild: one Azure index, which a rebuild writes directly.
    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) CreatePlainWithTree()
    {
        var created = CreateWithTree(_ => { });
        created.Azure.Aliases.Clear();
        created.Azure.Indexes.Clear();
        created.Azure.AddIndex(Name);
        return created;
    }

    private static async Task WriteTreeAsync(IServiceProvider services)
    {
        await WriteAsync(services, Parent);
        await WriteAsync(services, Child);
    }

    [Test]
    public async Task ARebuildDeleteSendsNoRequest()
    {
        var (service, azure, services) = CreateWithTree();
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(service.RequestCount, Is.Zero);
            Assert.That(azure.Aliases[Name], Is.EqualTo(SlotA), "The rebuild goes on.");
        });
    }

    // With a batch, the delete keeps the documents that wait: they belong to other items.
    [Test]
    public async Task ABatchedRebuildDeleteSendsNoRequest()
    {
        var (service, azure, services) = CreateWithTree(index =>
        {
            index.Rebuild(r => r.BatchSize(10).MinimumDocumentPercent(0).DeleteDelay(TimeSpan.Zero));
            index.ZeroDowntimeRebuild();
        });
        var indexer = services.GetRequiredService<IIndexer>();
        var item = Guid.NewGuid();

        await indexer.ResetAsync(IndexAlias);
        await WriteAsync(services, item);
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Parent]);
        var requests = service.RequestCount;
        await CompleteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(requests, Is.Zero);
            Assert.That(azure.Documents[SlotB], Is.EqualTo(new[] { DocumentId(item) }), "The waiting document goes when the rebuild completes.");
        });
    }

    // Umbraco Search never deletes an item that the rebuild wrote. Code inside the rebuild flow can, and the document stays.
    [Test]
    public async Task ARebuildDeleteKeepsADocumentTheRebuildWrote()
    {
        var (service, azure, services) = CreateWithTree();
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        await WriteAsync(services, Parent);
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(service.RequestCount, Is.Zero);
            Assert.That(azure.Documents[SlotB], Is.EqualTo(new[] { DocumentId(Parent) }));
        });
    }

    // An unpublish during the rebuild runs in another flow. It searches both slots and deletes the item and its
    // descendants there, as before.
    [Test]
    public async Task ADeleteOutsideTheRebuildDeletesTheDescendantsInBothSlots()
    {
        var (service, azure, services) = CreateWithTree();
        await WriteTreeAsync(services);

        await RebuildAsync(services, [Guid.NewGuid(), Guid.NewGuid()], () => OutsideTheRebuild(async () =>
        {
            await WriteTreeAsync(services);
            service.ClearRequests();
            await services.GetRequiredService<IIndexer>().DeleteAsync(IndexAlias, [Parent]);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(SearchedIndexes(service), Is.EquivalentTo(new[] { SlotA, SlotB }));
            Assert.That(azure.Documents[SlotA], Has.No.Member(DocumentId(Parent)).And.No.Member(DocumentId(Child)));
            Assert.That(azure.Documents[SlotB], Has.No.Member(DocumentId(Parent)).And.No.Member(DocumentId(Child)));
        });
    }

    [Test]
    public async Task ADeleteWithoutARebuildDeletesTheDescendants()
    {
        var (service, azure, services) = CreateWithTree();
        await WriteTreeAsync(services);
        service.ClearRequests();

        await services.GetRequiredService<IIndexer>().DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(SearchedIndexes(service), Is.EqualTo(new[] { SlotA }));
            Assert.That(azure.Documents[SlotA], Is.Empty);
        });
    }

    // A plain rebuild also starts from an empty index: ResetAsync deletes and creates it again. Its deletes send no request.
    [Test]
    public async Task APlainRebuildDeleteSendsNoRequest()
    {
        var (service, _, services) = CreatePlainWithTree();
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        await WriteAsync(services, Guid.NewGuid());
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Parent]);

        Assert.That(service.RequestCount, Is.Zero);
    }

    // A serverless service has no aliases, so the rebuild is a plain rebuild also with ZeroDowntimeRebuild on.
    [Test]
    public async Task AServerlessRebuildDeleteSendsNoRequest()
    {
        var azure = new FakeAzureIndexes();
        var service = new FakeSearchService
        {
            Respond = azure.Respond,
            ConfigureServices = services => services.ConfigureAzureSearchIndex(IndexAlias, index => index.ZeroDowntimeRebuild()),
        };
        service.Settings["UmbracoAzureSearch:Serverless"] = "true";
        var services = service.CreateServices();
        azure.AddIndex(Name);
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        await WriteAsync(services, Guid.NewGuid());
        service.ClearRequests();
        await indexer.DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(service.RequestCount, Is.Zero);
            Assert.That(azure.Aliases, Is.Empty);
        });
    }

    // An unpublish during a plain rebuild runs in another flow. It searches the index and deletes the descendants.
    [Test]
    public async Task ADeleteOutsideAPlainRebuildDeletesTheDescendants()
    {
        var (service, azure, services) = CreatePlainWithTree();

        await RebuildAsync(services, [Guid.NewGuid(), Guid.NewGuid()], () => OutsideTheRebuild(async () =>
        {
            await WriteTreeAsync(services);
            service.ClearRequests();
            await services.GetRequiredService<IIndexer>().DeleteAsync(IndexAlias, [Parent]);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(SearchedIndexes(service), Is.EqualTo(new[] { Name }));
            Assert.That(azure.Documents[Name], Has.No.Member(DocumentId(Parent)).And.No.Member(DocumentId(Child)));
        });
    }
}
