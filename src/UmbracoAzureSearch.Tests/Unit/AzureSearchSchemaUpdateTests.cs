using System.Net;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Notifications;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

// The index schema grows as documents bring new fields. Concurrent writes must not lose a field or fail on a busy index,
// and a write whose fields are all known must not read the schema.
[TestFixture]
public class AzureSearchSchemaUpdateTests
{
    private const string IndexAlias = "unit-test-index";

    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) Create(
        Action<IServiceCollection>? configure = null)
    {
        var azure = new FakeAzureIndexes();
        var service = new FakeSearchService { Respond = azure.Respond, ConfigureServices = configure };
        var services = service.CreateServices();
        services.GetRequiredService<IndexSchemaUpdates>().RetryDelay = _ => TimeSpan.Zero;
        return (service, azure, services);
    }

    private static Task WriteAsync(IServiceProvider services, string fieldName, string indexAlias = IndexAlias)
        => services.GetRequiredService<IIndexer>().AddOrUpdateAsync(indexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document,
            [new Variation(null, null)], [new IndexField(fieldName, new IndexValue { Keywords = ["value"] }, null, null)], null);

    [Test]
    public async Task ConcurrentWritesUpdateTheSchemaOneAtATimeAndKeepEveryField()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        azure.SchemaCallDuration = TimeSpan.FromMilliseconds(30);

        await Task.WhenAll(WriteAsync(services, "title"), WriteAsync(services, "body"), WriteAsync(services, "author"));

        Assert.Multiple(() =>
        {
            Assert.That(azure.MaxSchemaCallsInFlight, Is.EqualTo(1));
            Assert.That(azure.FieldNames(IndexAlias), Is.SupersetOf(new[] { "title_keywords", "body_keywords", "author_keywords" }));
            Assert.That(azure.Documents[IndexAlias], Has.Count.EqualTo(3));
        });
    }

    // Another process changed the index between the read and the update, or Azure still applies an earlier update.
    [Test]
    public async Task RetriesAConflictWithAFreshRead()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        var conflicts = 0;
        azure.OnSchemaUpdate = index =>
        {
            if (++conflicts > 2)
                return null;
            azure.AddIndex(index, $"other{conflicts}_keywords");
            return FakeAzureIndexes.Conflict(index);
        };

        await WriteAsync(services, "title");

        Assert.Multiple(() =>
        {
            Assert.That(azure.FieldNames(IndexAlias), Does.Contain("title_keywords").And.Contain("other2_keywords"));
            Assert.That(azure.Count(azure.SchemaReads, IndexAlias), Is.EqualTo(3));
            Assert.That(azure.Documents[IndexAlias], Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task RetriesAnIndexInTheUpdatingState()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        var conflicts = 0;
        azure.OnSchemaUpdate = index => ++conflicts > 1
            ? null
            : FakeAzureIndexes.Error(HttpStatusCode.Conflict, "OperationNotAllowed", $"Index '{index}' cannot be updated in 'Updating' state.");

        await WriteAsync(services, "title");

        Assert.That(azure.FieldNames(IndexAlias), Does.Contain("title_keywords"));
    }

    [Test]
    public async Task ThrowsAfterRepeatedConflicts()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        var attempts = 0;
        azure.OnSchemaUpdate = index =>
        {
            attempts++;
            return FakeAzureIndexes.Conflict(index);
        };

        var exception = Assert.ThrowsAsync<RequestFailedException>(() => WriteAsync(services, "title"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Status, Is.EqualTo(409));
            Assert.That(attempts, Is.EqualTo(IndexSchemaUpdates.Attempts));
        });
    }

    [Test]
    public async Task AWriteWithKnownFieldsReadsNoSchema()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);

        await WriteAsync(services, "title");
        await WriteAsync(services, "title");
        await WriteAsync(services, "title");

        Assert.Multiple(() =>
        {
            Assert.That(azure.Count(azure.SchemaReads, IndexAlias), Is.EqualTo(1));
            Assert.That(azure.Documents[IndexAlias], Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task ReadsTheSchemaAgainAfterAFailedUpdate()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        await WriteAsync(services, "title");
        azure.OnSchemaUpdate = _ => FakeAzureIndexes.Error(HttpStatusCode.BadRequest, "", "Update failed");

        Assert.ThrowsAsync<RequestFailedException>(() => WriteAsync(services, "body"));
        azure.OnSchemaUpdate = null;
        await WriteAsync(services, "title");

        Assert.That(azure.Count(azure.SchemaReads, IndexAlias), Is.EqualTo(3));
    }

    // Another server deleted and created the index again, so a field this server knows is gone.
    [Test]
    public async Task ReadsTheSchemaAgainWhenAWriteNamesAFieldTheIndexLost()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        await WriteAsync(services, "title");
        azure.AddIndex(IndexAlias);

        await WriteAsync(services, "title");

        Assert.Multiple(() =>
        {
            Assert.That(azure.FieldNames(IndexAlias), Does.Contain("title_keywords"));
            Assert.That(azure.RejectedUploads, Is.EqualTo(1));
            Assert.That(azure.Documents[IndexAlias], Has.Count.EqualTo(2));
        });
    }

    // Slot a is deleted after the first swap and created again by the second rebuild, without the fields documents added.
    [Test]
    public async Task ASlotCreatedAgainDoesNotUseTheFieldsOfTheDeletedSlot()
    {
        const string alias = "Umb_PublishedContent";
        var (_, azure, services) = Create(collection => collection.ConfigureAzureSearchIndex(alias,
            index => index.ZeroDowntimeRebuild(r => r.DeleteDelay(TimeSpan.Zero).MinimumDocumentPercent(0))));
        azure.AddIndex("umb-publishedcontent-a");
        azure.Aliases["umb-publishedcontent"] = "umb-publishedcontent-a";
        var indexer = services.GetRequiredService<IIndexer>();
        var completed = services.GetServices<INotificationAsyncHandler<IndexRebuildCompletedNotification>>().Single();

        await WriteAsync(services, "title", alias);
        await RebuildAsync(indexer, alias);
        await completed.HandleAsync(new IndexRebuildCompletedNotification(alias), CancellationToken.None);
        await RebuildAsync(indexer, alias);
        await WriteAsync(services, "title", alias);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Aliases["umb-publishedcontent"], Is.EqualTo("umb-publishedcontent-b"));
            Assert.That(azure.FieldNames("umb-publishedcontent-a"), Does.Contain("title_keywords"));
            Assert.That(azure.RejectedUploads, Is.Zero, "The write to the new slot a adds the field before the upload.");
        });
    }

    // The scoring profile methods update the schema of an index that documents also write to.
    [Test]
    public async Task ARefreshOfScoringProfilesWaitsForASchemaUpdateOfAWrite()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        azure.SchemaCallDuration = TimeSpan.FromMilliseconds(30);

        await Task.WhenAll(
            WriteAsync(services, "title"),
            services.GetRequiredService<IAzureSearchIndexManager>().RefreshScoringProfilesAsync(IndexAlias),
            WriteAsync(services, "body"));

        Assert.Multiple(() =>
        {
            Assert.That(azure.MaxSchemaCallsInFlight, Is.EqualTo(1));
            Assert.That(azure.FieldNames(IndexAlias), Does.Contain("title_keywords").And.Contain("body_keywords"));
        });
    }

    [Test]
    public async Task StartupRetriesAConflict()
    {
        var (_, azure, services) = Create();
        azure.AddIndex(IndexAlias);
        var conflicts = 0;
        azure.OnSchemaUpdate = index => ++conflicts > 1 ? null : FakeAzureIndexes.Conflict(index);

        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.That(azure.Count(azure.SchemaUpdates, IndexAlias), Is.EqualTo(1));
    }

    // A rebuild as Umbraco Search runs it: the reset, then the writes, in one async flow.
    private static async Task RebuildAsync(IIndexer indexer, string indexAlias) => await indexer.ResetAsync(indexAlias);
}
