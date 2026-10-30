using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Notifications;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

// Umbraco Search rebuilds an index in one async flow: ResetAsync, then AddOrUpdateAsync for each item. Those are the
// rebuild writes. A zero-downtime rebuild writes them to the new slot only; other writes go to both slots.
[TestFixture]
public class AzureSearchRebuildWriteTests
{
    internal const string IndexAlias = "Umb_PublishedContent";
    internal const string Name = "umb-publishedcontent";
    internal const string SlotA = "umb-publishedcontent-a";
    internal const string SlotB = "umb-publishedcontent-b";

    internal static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) Create(
        Action<AzureSearchIndexBuilder>? configure = null)
    {
        var azure = new FakeAzureIndexes();
        var service = new FakeSearchService
        {
            Respond = azure.Respond,
            ConfigureServices = services => services.ConfigureAzureSearchIndex(IndexAlias,
                configure ?? (index => index.ZeroDowntimeRebuild(r => r.DeleteDelay(TimeSpan.Zero).MinimumDocumentPercent(0)))),
        };
        var provider = service.CreateServices();
        provider.GetRequiredService<IndexSchemaUpdates>().RetryDelay = _ => TimeSpan.Zero;
        azure.AddIndex(SlotA);
        azure.Aliases[Name] = SlotA;
        return (service, azure, provider);
    }

    internal static Task WriteAsync(IServiceProvider services, Guid id, params string[] cultures)
        => services.GetRequiredService<IIndexer>().AddOrUpdateAsync(IndexAlias, id, UmbracoObjectTypes.Document,
            cultures.Length == 0 ? [new Variation(null, null)] : cultures.Select(c => new Variation(c, null)).ToArray(),
            [new IndexField("title", new IndexValue { Keywords = ["Red boat"] }, null, null)], null);

    internal static Task CompleteAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => services.GetServices<INotificationAsyncHandler<IndexRebuildCompletedNotification>>().Single()
            .HandleAsync(new IndexRebuildCompletedNotification(IndexAlias), cancellationToken);

    /// <summary>
    /// A rebuild as Umbraco Search runs it (PublishedContentChangeStrategy.RebuildAsync): the reset, then the writes, in
    /// one async method. <paramref name="duringRebuild"/> runs between two writes and returns when the rebuild may go on.
    /// </summary>
    internal static async Task RebuildAsync(IServiceProvider services, Guid[] items, Func<Task>? duringRebuild = null)
    {
        var indexer = services.GetRequiredService<IIndexer>();
        await indexer.ResetAsync(IndexAlias);
        for (var i = 0; i < items.Length; i++)
        {
            await WriteAsync(services, items[i]);
            if (i == 0 && duringRebuild is not null)
                await duringRebuild();
        }
    }

    [Test]
    public async Task RebuildWritesGoToTheNewSlotOnly()
    {
        var (_, azure, services) = Create();

        await RebuildAsync(services, [Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents.Keys, Is.EquivalentTo(new[] { SlotB }));
            Assert.That(azure.Documents[SlotB], Has.Count.EqualTo(2));
        });
    }

    // The new slot starts empty, so a rebuild write has no removed variations to delete there.
    [Test]
    public async Task RebuildWritesLookUpNoRemovedVariations()
    {
        var (_, azure, services) = Create();

        await RebuildAsync(services, [Guid.NewGuid(), Guid.NewGuid()]);

        Assert.That(azure.Searches, Is.Empty);
    }

    [Test]
    public async Task AWriteOutsideTheRebuildGoesToBothSlotsAndCleansUp()
    {
        var (_, azure, services) = Create();
        var publish = Guid.NewGuid();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebuild = RebuildAsync(services, [Guid.NewGuid(), Guid.NewGuid()], async () =>
        {
            paused.SetResult();
            await resume.Task;
        });
        await paused.Task;
        await WriteAsync(services, publish);
        resume.SetResult();
        await rebuild;

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents[SlotA], Is.EqualTo(new[] { $"{publish.AsKeyword()}_inv_def" }));
            Assert.That(azure.Documents[SlotB], Has.Count.EqualTo(3));
            Assert.That(azure.Searches, Is.EquivalentTo(new[] { SlotA, SlotB }), "The publish cleans up removed variations in both slots.");
        });
    }

    // Site code that calls the indexer in parallel with the rebuild runs in another flow.
    [Test]
    public async Task AParallelWriteIsNotARebuildWrite()
    {
        var (_, azure, services) = Create();
        var publish = Guid.NewGuid();

        await RebuildAsync(services, [Guid.NewGuid()], () =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(() => WriteAsync(services, publish));
            }
        });

        Assert.That(azure.Documents[SlotA], Is.EqualTo(new[] { $"{publish.AsKeyword()}_inv_def" }));
    }

    [Test]
    public async Task AWriteAfterTheRebuildCompletedIsNotARebuildWrite()
    {
        var (_, azure, services) = Create();
        var items = new[] { Guid.NewGuid() };
        var publish = Guid.NewGuid();

        // Umbraco Search publishes the completed notification after RebuildAsync returned.
        await RebuildAsync(services, items);
        await CompleteAsync(services);
        await WriteAsync(services, publish);

        Assert.That(azure.Documents[SlotB], Is.EqualTo(new[] { $"{items[0].AsKeyword()}_inv_def", $"{publish.AsKeyword()}_inv_def" }));
    }

    // Without zero-downtime rebuild the index has one Azure index, which gets every write as before.
    [Test]
    public async Task APlainRebuildWritesTheIndex()
    {
        var (_, azure, services) = Create(_ => { });
        azure.Aliases.Clear();
        azure.Indexes.Clear();
        azure.AddIndex(Name);

        await RebuildAsync(services, [Guid.NewGuid()]);

        Assert.That(azure.Documents[Name], Has.Count.EqualTo(1));
    }

    // The rebuild deletes content that has no published variation. It only affects the new slot.
    [Test]
    public async Task RebuildDeletesGoToTheNewSlotOnly()
    {
        var (service, _, services) = Create();

        await DeleteInRebuildAsync(services);

        Assert.That(service.Requests.Where(r => r.Path.Contains("search.post.search")).Select(r => r.Path),
            Has.All.Contains(SlotB).And.Not.Empty);
    }

    private static async Task DeleteInRebuildAsync(IServiceProvider services)
    {
        var indexer = services.GetRequiredService<IIndexer>();
        await indexer.ResetAsync(IndexAlias);
        await indexer.DeleteAsync(IndexAlias, [Guid.NewGuid()]);
    }
}
