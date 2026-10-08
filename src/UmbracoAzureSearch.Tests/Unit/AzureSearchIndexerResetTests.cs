using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Sync;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexerResetTests
{
    [Test]
    public async Task SubscriberDoesNotResetIndex()
    {
        var service = new FakeSearchService();
        var indexer = service.CreateServices(ServerRole.Subscriber).GetRequiredService<IAzureSearchIndexer>();

        await indexer.ResetAsync("unit-test-index");

        Assert.That(service.Requests, Is.Empty);
    }

    [TestCase(ServerRole.Single)]
    [TestCase(ServerRole.SchedulingPublisher)]
    public async Task CanResetIndex(ServerRole serverRole)
    {
        var service = new FakeSearchService();
        var indexer = service.CreateServices(serverRole).GetRequiredService<IAzureSearchIndexer>();

        await indexer.ResetAsync("unit-test-index");

        Assert.That(service.Requests.Select(r => r.Method), Does.Contain(HttpMethod.Delete));
    }
}
