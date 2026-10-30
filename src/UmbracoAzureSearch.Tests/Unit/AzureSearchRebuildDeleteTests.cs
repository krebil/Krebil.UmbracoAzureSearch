using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using static UmbracoAzureSearch.Tests.Unit.AzureSearchRebuildWriteTests;

namespace UmbracoAzureSearch.Tests.Unit;

// Umbraco Search deletes each item without a published variation during a rebuild, and skips its descendants. A delete
// searches the index for the documents of the item and its descendants (Umb_PathIds). During a zero-downtime rebuild the
// new slot only holds what the rebuild wrote, so such a search finds nothing: the rebuild delete sends no request.
[TestFixture]
public class AzureSearchRebuildDeleteTests
{
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();

    private static string DocumentId(Guid id) => $"{id.AsKeyword()}_inv_def";

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

    private static string? IndexName(string path)
    {
        const string prefix = "/indexes('";
        var start = path.IndexOf(prefix, StringComparison.Ordinal);
        return start < 0 ? null : path[(start + prefix.Length)..path.IndexOf("')", start, StringComparison.Ordinal)];
    }

    private static string[] SearchedIndexes(FakeSearchService service)
    {
        lock (service.Requests)
        {
            return service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => IndexName(r.Path)!).ToArray();
        }
    }

    private static int RequestCount(FakeSearchService service)
    {
        lock (service.Requests)
        {
            return service.Requests.Count;
        }
    }

    private static void ClearRequests(FakeSearchService service)
    {
        lock (service.Requests)
        {
            service.Requests.Clear();
        }
    }

    // A publish or site code: another async flow than the rebuild.
    private static Task OutsideTheRebuild(Func<Task> write)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(write);
        }
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
        ClearRequests(service);
        await indexer.DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(RequestCount(service), Is.Zero);
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
        ClearRequests(service);
        await indexer.DeleteAsync(IndexAlias, [Parent]);
        var requests = RequestCount(service);
        await CompleteAsync(services);

        Assert.Multiple(() =>
        {
            Assert.That(requests, Is.Zero);
            Assert.That(azure.Documents[SlotB], Is.EqualTo(new[] { DocumentId(item) }), "The waiting document goes when the rebuild completes.");
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
            ClearRequests(service);
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
        ClearRequests(service);

        await services.GetRequiredService<IIndexer>().DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(SearchedIndexes(service), Is.EqualTo(new[] { SlotA }));
            Assert.That(azure.Documents[SlotA], Is.Empty);
        });
    }

    // A plain rebuild writes the live index, so its deletes search and delete there as before.
    [Test]
    public async Task APlainRebuildDeleteDeletesTheDescendants()
    {
        var (service, azure, services) = CreateWithTree(_ => { });
        azure.Aliases.Clear();
        azure.Indexes.Clear();
        azure.AddIndex(Name);
        var indexer = services.GetRequiredService<IIndexer>();

        await indexer.ResetAsync(IndexAlias);
        await WriteTreeAsync(services);
        ClearRequests(service);
        await indexer.DeleteAsync(IndexAlias, [Parent]);

        Assert.Multiple(() =>
        {
            Assert.That(SearchedIndexes(service), Is.EqualTo(new[] { Name }));
            Assert.That(azure.Documents[Name], Is.Empty);
        });
    }
}
