using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Factory;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchClientFactoryTests
{
    private const string Endpoint = "https://unit-test.search.windows.net";

    [Test]
    public void Key_Only_Creates_Client()
    {
        var factory = CreateFactory(key: "test-key");

        var client = factory.GetSearchIndexClient();

        Assert.That(client.Endpoint, Is.EqualTo(new Uri(Endpoint)));
    }

    [Test]
    public void Returns_Same_Client_On_Every_Call()
    {
        var factory = CreateFactory(key: "test-key");

        var first = factory.GetSearchIndexClient();
        var second = factory.GetSearchIndexClient();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Neither_Key_Nor_Credential_Throws()
    {
        var factory = CreateFactory(key: null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.GetSearchIndexClient());
        Assert.That(exception!.Message, Does.Contain("UmbracoAzureSearch:Key").And.Contain("TokenCredential"));
    }

    [TestCase(null)]
    [TestCase("test-key")]
    public void Credential_Is_Used_For_Requests(string? key)
    {
        var credential = new MarkerCredential();
        var factory = CreateFactory(key, credential);

        // The credential throws before any request leaves the process, so this never reaches the network.
        Assert.ThrowsAsync<MarkerCredential.TokenRequestedException>(
            async () => await factory.GetSearchIndexClient().GetServiceStatisticsAsync());
        Assert.That(credential.TokenRequests, Is.EqualTo(1));
    }

    [Test]
    public void Null_Credential_Is_Rejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddUmbracoAzureSearch(BuildConfiguration(key: "test-key"), null!));
    }

    private static IAzureSearchClientFactory CreateFactory(string? key, TokenCredential? credential = null)
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

        return services.BuildServiceProvider().GetRequiredService<IAzureSearchClientFactory>();
    }

    private static IConfiguration BuildConfiguration(string? key)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAzureSearch:Endpoint"] = Endpoint,
                ["UmbracoAzureSearch:Key"] = key,
            })
            .Build();

    private sealed class MarkerCredential : TokenCredential
    {
        public int TokenRequests { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            TokenRequests++;
            throw new TokenRequestedException();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));

        public sealed class TokenRequestedException : Exception;
    }
}
