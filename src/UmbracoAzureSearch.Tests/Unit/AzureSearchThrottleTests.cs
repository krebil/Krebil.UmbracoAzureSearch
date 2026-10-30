using System.Net;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

// A Basic service answers a busy period with 503 ("not enough resources") or 429. A write waits and tries again,
// a bounded number of times, instead of failing after the retries of the Azure SDK.
[TestFixture]
public class AzureSearchThrottleTests
{
    private const string IndexAlias = "unit-test-index";

    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) Create()
    {
        var azure = new FakeAzureIndexes();
        var service = new FakeSearchService { Respond = azure.Respond };
        var services = service.CreateServices();
        services.GetRequiredService<IndexSchemaUpdates>().RetryDelay = _ => TimeSpan.Zero;
        services.GetRequiredService<DocumentWriter>().RetryDelay = _ => TimeSpan.Zero;
        azure.AddIndex(IndexAlias);
        return (service, azure, services);
    }

    private static Task WriteAsync(IServiceProvider services, Guid id)
        => services.GetRequiredService<IIndexer>().AddOrUpdateAsync(IndexAlias, id, UmbracoObjectTypes.Document,
            [new Variation(null, null)], [new IndexField("title", new IndexValue { Keywords = ["Red boat"] }, null, null)], null);

    private static HttpResponseMessage Throttled(HttpStatusCode status)
        => FakeAzureIndexes.Error(status, "", "The request is throttled: there are not enough resources to cover 100% of the index.");

    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task RetriesAThrottledWrite(HttpStatusCode status)
    {
        var (service, azure, services) = Create();
        var throttled = 0;
        azure.OnUpload = (_, _) => ++throttled <= 3 ? Throttled(status) : null;

        await WriteAsync(services, Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(azure.Documents[IndexAlias], Has.Count.EqualTo(1));
            Assert.That(service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message), Has.Some.Contains("throttled"));
        });
    }

    [Test]
    public void ThrowsWhenTheServiceStaysThrottled()
    {
        var (_, azure, services) = Create();
        var attempts = 0;
        azure.OnUpload = (_, _) =>
        {
            attempts++;
            return Throttled(HttpStatusCode.ServiceUnavailable);
        };

        var exception = Assert.ThrowsAsync<RequestFailedException>(() => WriteAsync(services, Guid.NewGuid()));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Status, Is.EqualTo(503));
            Assert.That(attempts, Is.EqualTo(DocumentWriter.Attempts));
        });
    }

    // Azure can accept a request and throttle single documents in it (207 with a 503 per document).
    [Test]
    public async Task RetriesThrottledDocumentsOfAnAcceptedRequest()
    {
        var (_, azure, services) = Create();
        var id = Guid.NewGuid();
        var throttled = 0;
        azure.OnUpload = (_, ids) => ++throttled <= 2
            ? FakeSearchService.Json($$"""{"value":[{"key":"{{ids[0]}}","status":false,"errorMessage":"Throttled","statusCode":503}]}""", (HttpStatusCode)207)
            : null;

        await WriteAsync(services, id);

        Assert.That(azure.Documents[IndexAlias], Is.EqualTo(new[] { $"{id.AsKeyword()}_inv_def" }));
    }

    [Test]
    public void DoesNotRetryABadRequest()
    {
        var (_, azure, services) = Create();
        var attempts = 0;
        azure.OnUpload = (_, _) =>
        {
            attempts++;
            return FakeAzureIndexes.Error(HttpStatusCode.RequestEntityTooLarge, "", "Request too large");
        };

        Assert.ThrowsAsync<RequestFailedException>(() => WriteAsync(services, Guid.NewGuid()));
        Assert.That(attempts, Is.EqualTo(1));
    }
}
