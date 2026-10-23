using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class UmbracoAzureSearchOptionsTests
{
    private static bool IsServerless(FakeSearchService service)
        => service.CreateServices().GetRequiredService<IOptions<UmbracoAzureSearchOptions>>().Value.IsServerless;

    [TestCase("Serverless", "true", true)]
    [TestCase("IsServerless", "true", true)]
    [TestCase("Serverless", "false", false)]
    public void ReadsBothServerlessKeys(string key, string value, bool expected)
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:{key}"] = value;

        Assert.That(IsServerless(service), Is.EqualTo(expected));
    }

    [Test]
    public void IsNotServerlessByDefault()
        => Assert.That(IsServerless(new FakeSearchService()), Is.False);

    [TestCase("true", "false", false)]
    [TestCase("false", "true", true)]
    public void IsServerlessWinsWhenBothKeysAreSet(string serverless, string isServerless, bool expected)
    {
        var service = new FakeSearchService();
        service.Settings["UmbracoAzureSearch:Serverless"] = serverless;
        service.Settings["UmbracoAzureSearch:IsServerless"] = isServerless;

        Assert.That(IsServerless(service), Is.EqualTo(expected));
    }

    [Test]
    public void CodeWinsOverTheServerlessKey()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services.Configure<UmbracoAzureSearchOptions>(options => options.IsServerless = false),
        };
        service.Settings["UmbracoAzureSearch:Serverless"] = "true";

        Assert.That(IsServerless(service), Is.False);
    }
}
