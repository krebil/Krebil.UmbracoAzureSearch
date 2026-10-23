using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexOptionsTests
{
    private const string IndexAlias = "Umb_PublishedContent";

    private static AzureSearchIndexOptions Options(FakeSearchService service, string indexAlias = IndexAlias)
        => service.CreateServices().GetRequiredService<IOptionsMonitor<AzureSearchIndexOptions>>().Get(indexAlias);

    private static async Task StartAsync(FakeSearchService service)
    {
        foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private static string[] Warnings(FakeSearchService service)
        => service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToArray();

    [Test]
    public void UsesTheDefaultsForAnIndexWithoutAnEntry()
    {
        var options = Options(new FakeSearchService());

        Assert.Multiple(() =>
        {
            Assert.That(options.Suggestions.Enabled, Is.True);
            Assert.That(options.Suggestions.Fields, Is.Null);
        });
    }

    [Test]
    public void ReadsTheIndexSettingsFromAppSettings()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Enabled"] = "false";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Fields:0"] = "title";

        var options = Options(service);

        Assert.Multiple(() =>
        {
            Assert.That(options.Suggestions.Enabled, Is.False);
            Assert.That(options.Suggestions.Fields, Is.EqualTo(new[] { "title" }));
        });
    }

    [Test]
    public void ReadsTheIndexSettingsFromCode()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services.ConfigureAzureSearchIndex(IndexAlias, index => index
                .Suggestions(false)
                .SuggestionFields("title", "subtitle")),
        };

        var options = Options(service);

        Assert.Multiple(() =>
        {
            Assert.That(options.Suggestions.Enabled, Is.False);
            Assert.That(options.Suggestions.Fields, Is.EqualTo(new[] { "title", "subtitle" }));
        });
    }

    [Test]
    public void CodeWinsOverAppSettings()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services.ConfigureAzureSearchIndex(IndexAlias, index => index.Suggestions(true)),
        };
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Enabled"] = "false";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Fields:0"] = "title";

        var options = Options(service);

        Assert.Multiple(() =>
        {
            Assert.That(options.Suggestions.Enabled, Is.True);
            Assert.That(options.Suggestions.Fields, Is.EqualTo(new[] { "title" }), "Code does not set the fields, so appsettings still applies.");
        });
    }

    [Test]
    public void CodeWinsWhenItIsRegisteredBeforeAddUmbracoAzureSearch()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Enabled"] = "false";

        var services = new ServiceCollection()
            .ConfigureAzureSearchIndex(IndexAlias, index => index.Suggestions(true))
            .AddUmbracoAzureSearch(new ConfigurationBuilder().AddInMemoryCollection(service.Settings).Build())
            .BuildServiceProvider();

        Assert.That(services.GetRequiredService<IOptionsMonitor<AzureSearchIndexOptions>>().Get(IndexAlias).Suggestions.Enabled, Is.True);
    }

    [Test]
    public void AppliesSeveralCodeCallsForOneIndexInOrder()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services
                .ConfigureAzureSearchIndex(IndexAlias, index => index.Suggestions(false))
                .ConfigureAzureSearchIndex(IndexAlias.ToLowerInvariant(), index => index.Suggestions(true)),
        };

        Assert.That(Options(service).Suggestions.Enabled, Is.True);
    }

    [Test]
    public async Task ReadsTheOldSuggestionsLocationAndLogsAWarning()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";

        await StartAsync(service);

        Assert.Multiple(() =>
        {
            Assert.That(Options(service).Suggestions.Enabled, Is.False);
            Assert.That(Warnings(service), Has.One.Contains($"UmbracoAzureSearch:Suggestions:{IndexAlias}")
                .And.Contains($"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions"));
        });
    }

    [Test]
    public async Task IgnoresTheOldSuggestionsLocationWhenTheNewOneIsSet()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Fields:0"] = "old";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Fields:0"] = "new";

        await StartAsync(service);

        var options = Options(service);
        Assert.Multiple(() =>
        {
            Assert.That(options.Suggestions.Enabled, Is.True, "The old location is ignored as a whole.");
            Assert.That(options.Suggestions.Fields, Is.EqualTo(new[] { "new" }));
            Assert.That(Warnings(service), Has.One.Contains($"UmbracoAzureSearch:Suggestions:{IndexAlias} is ignored"));
        });
    }

    [Test]
    public async Task LogsNoWarningWithoutTheOldSuggestionsLocation()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Suggestions:Enabled"] = "false";

        await StartAsync(service);

        Assert.That(Warnings(service), Is.Empty);
    }

    [Test]
    public void StopsStartupForAnUnknownIndexInAppSettings()
    {
        var service = new FakeSearchService();
        service.Settings["UmbracoAzureSearch:Indexes:Umb_PublishedContnet:Suggestions:Enabled"] = "false";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("UmbracoAzureSearch:Indexes:Umb_PublishedContnet")
            .And.Contains("\"Umb_PublishedContnet\"")
            .And.Contains(IndexAlias));
    }

    [Test]
    public void StopsStartupForAnUnknownIndexInCode()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services.ConfigureAzureSearchIndex("siteSearch", index => index.Suggestions(false)),
        };

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("ConfigureAzureSearchIndex(\"siteSearch\")"));
    }

    [Test]
    public void ListsEveryUnknownIndexInOneError()
    {
        var service = new FakeSearchService();
        service.Settings["UmbracoAzureSearch:Indexes:first:Suggestions:Enabled"] = "false";
        service.Settings["UmbracoAzureSearch:Indexes:second:Suggestions:Enabled"] = "false";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("\"first\"").And.Contains("\"second\""));
    }

    [Test]
    public void AcceptsAnIndexAliasInAnotherCase()
    {
        var service = new FakeSearchService();
        service.Settings["UmbracoAzureSearch:Indexes:umb_publishedcontent:Suggestions:Enabled"] = "false";

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrowAsync(() => StartAsync(service));
            Assert.That(Options(service).Suggestions.Enabled, Is.False);
        });
    }

    [Test]
    public void AcceptsAKnownIndexInCode()
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services.ConfigureAzureSearchIndex(IndexAlias, index => index.Suggestions(false)),
        };

        Assert.DoesNotThrowAsync(() => StartAsync(service));
    }
}
