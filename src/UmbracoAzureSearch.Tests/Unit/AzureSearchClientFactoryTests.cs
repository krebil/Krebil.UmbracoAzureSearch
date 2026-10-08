using System.Net;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Factory;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchClientFactoryTests
{
    private const string Endpoint = "https://unit-test.search.windows.net";
    private const string Key = "test-key";
    private const string Token = "test-token";

    [Test]
    public void CanAuthenticateWithKey()
    {
        var handler = new RecordingHandler();
        var factory = CreateFactory(Key, handler);

        SendRequest(factory);

        var request = handler.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.GetValues("api-key"), Is.EqualTo(new[] { Key }));
            Assert.That(request.Headers.Authorization, Is.Null);
        });
    }

    [TestCase(null)]
    [TestCase(Key)]
    public void CanAuthenticateWithCredential(string? key)
    {
        var handler = new RecordingHandler();
        var factory = CreateFactory(key, handler, new CountingCredential());

        SendRequest(factory);

        var request = handler.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.Authorization?.ToString(), Is.EqualTo($"Bearer {Token}"));
            Assert.That(request.Headers.Contains("api-key"), Is.False);
        });
    }

    [Test]
    public void CanReuseAccessTokenAcrossRequests()
    {
        var handler = new RecordingHandler();
        var credential = new CountingCredential();
        var factory = CreateFactory(null, handler, credential);

        SendRequest(factory);
        SendRequest(factory);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            Assert.That(credential.TokenRequests, Is.EqualTo(1));
        });
    }

    [Test]
    public void CanReuseClient()
    {
        var factory = CreateFactory(Key, new RecordingHandler());

        var first = factory.GetSearchIndexClient();
        var second = factory.GetSearchIndexClient();

        Assert.That(second, Is.SameAs(first));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void CannotCreateClientWithoutKeyOrCredential(string? key)
    {
        var factory = CreateFactory(key, new RecordingHandler());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.GetSearchIndexClient());
        Assert.That(exception!.Message, Does.Contain("UmbracoAzureSearch:Key").And.Contain("TokenCredential"));
    }

    [Test]
    public void CannotRegisterNullCredential()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddUmbracoAzureSearch(BuildConfiguration(Key), null!));
    }

    private static AzureSearchClientFactory CreateFactory(string? key, RecordingHandler handler, TokenCredential? credential = null)
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(key);

        if (credential is null)
        {
            services.AddUmbracoAzureSearch(configuration);
        }
        else
        {
            services.AddUmbracoAzureSearch(configuration, credential);
        }

        var factory = (AzureSearchClientFactory)services.BuildServiceProvider().GetRequiredService<IAzureSearchClientFactory>();
        factory.Transport = new HttpClientTransport(new HttpClient(handler));
        return factory;
    }

    private static IConfiguration BuildConfiguration(string? key)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAzureSearch:Endpoint"] = Endpoint,
                ["UmbracoAzureSearch:Key"] = key,
            })
            .Build();

    // The handler answers 404, so each call ends in a RequestFailedException after the request is recorded.
    private static void SendRequest(IAzureSearchClientFactory factory)
        => Assert.ThrowsAsync<RequestFailedException>(
            async () => await factory.GetSearchIndexClient().GetIndexAsync("missing"));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CountingCredential : TokenCredential
    {
        public int TokenRequests { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            TokenRequests++;
            return new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
