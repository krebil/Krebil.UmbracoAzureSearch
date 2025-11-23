using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using DotNetEnv.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Provider.Examine.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services;
using UmbracoAzureSearch.Services.Factory;

namespace UmbracoAzureSearch.Tests;

[TestFixture]
public abstract class AzureSearchTestBase
{
    private IServiceProvider _serviceProvider;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddDotNetEnv()
            .Build();

        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddUmbracoAzureSearch(configuration)
            .AddLogging();

        serviceCollection.Configure<SearcherOptions>(
            options => { options.MaxFacetValues = 500; }
        );

        serviceCollection.AddSingleton<IServerRoleAccessor, SingleServerRoleAccessor>();

        PerformAdditionalConfiguration(serviceCollection);

        _serviceProvider = serviceCollection.BuildServiceProvider();

        await PerformOneTimeSetUpAsync();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await PerformOneTimeTearDownAsync();

        if (_serviceProvider is IDisposable disposableServiceProvider)
        {
            disposableServiceProvider.Dispose();
        }
    }

    protected virtual void PerformAdditionalConfiguration(ServiceCollection serviceCollection)
    {
    }

    protected virtual Task PerformOneTimeSetUpAsync()
        => Task.CompletedTask;

    protected virtual Task PerformOneTimeTearDownAsync()
        => Task.CompletedTask;

    protected T GetRequiredService<T>() where T : notnull
        => _serviceProvider.GetRequiredService<T>();

    protected Task WaitForIndexingOperationsToCompleteAsync()
    {
        // Azure Search operations are eventually consistent
        // Give it more time to ensure operations are visible in search results
        Thread.Sleep(3000);
        return Task.CompletedTask;
    }

    protected async Task DeleteIndex(string indexAlias)
    {
        var client = GetRequiredService<IAzureSearchClientFactory>().GetSearchIndexClient();

        var indices = client.GetIndexes().ToList();

        var existsResponse = indices.FirstOrDefault(i => i.Name.Equals(indexAlias, StringComparison.OrdinalIgnoreCase));
        if (existsResponse != null)
        {
            var response = await client.DeleteIndexAsync(existsResponse);
            Assert.That(response.IsError, Is.False);
        }
    }
}