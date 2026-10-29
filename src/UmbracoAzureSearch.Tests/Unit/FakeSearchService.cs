using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Sync;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests.Unit;

// Answers Azure AI Search calls with an empty index or result and records the requests.
internal sealed class FakeSearchService : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    /// <summary>A field the fake index does not have: a search naming it is answered as Azure does, with a 400.</summary>
    public string? UnknownField { get; set; }

    /// <summary>Report <see cref="UnknownField"/> for every search, whether the search names it or not.</summary>
    public bool UnknownFieldAlways { get; set; }

    /// <summary>Extra configuration, e.g. <c>["UmbracoAzureSearch:Indexes:my-index:Suggestions:Enabled"] = "false"</c>.</summary>
    public Dictionary<string, string?> Settings { get; } = [];

    /// <summary>Answers a request before the defaults; returning null falls through to them.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>Awaited before each request is answered, e.g. to hold requests until others arrive.</summary>
    public Func<HttpRequestMessage, Task>? BeforeRespond { get; set; }

    /// <summary>Everything the services logged.</summary>
    public List<(LogLevel Level, string Message)> Logs { get; } = [];

    /// <summary>The notifications the services published through <see cref="IEventAggregator"/>.</summary>
    public List<INotification> Notifications { get; } = [];

    /// <summary>Runs for each published notification, e.g. to throw as a failing handler does.</summary>
    public Action<INotification>? OnPublish { get; set; }

    /// <summary>Extra registrations, e.g. <c>ConfigureAzureSearchIndex</c> calls. Runs after <c>AddUmbracoAzureSearch</c>.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public IServiceProvider CreateServices(ServerRole serverRole = ServerRole.Single)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAzureSearch:Endpoint"] = "https://unit-test.search.windows.net",
                ["UmbracoAzureSearch:Key"] = "test-key",
            })
            .AddInMemoryCollection(Settings)
            .Build();

        var serviceCollection = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(new RecordingLoggerProvider(Logs)))
            .AddSingleton<IServerRoleAccessor>(new FixedServerRoleAccessor(serverRole))
            .AddSingleton<IEventAggregator>(new RecordingEventAggregator(this))
            .AddUmbracoAzureSearch(configuration);
        ConfigureServices?.Invoke(serviceCollection);
        var services = serviceCollection.BuildServiceProvider();
        ((AzureSearchClientFactory)services.GetRequiredService<IAzureSearchClientFactory>()).Transport =
            new HttpClientTransport(new HttpClient(this));
        return services;
    }

    public IAzureSearchSearcher CreateSearcher(ServerRole serverRole = ServerRole.Single)
        => CreateServices(serverRole).GetRequiredService<IAzureSearchSearcher>();

    public JsonElement LastSearchBody()
    {
        string? body;
        lock (Requests)
        {
            body = Requests.Last(r => r.Path.Contains("search.post.search")).Body;
        }
        Assert.That(body, Is.Not.Null);
        return JsonDocument.Parse(body!).RootElement;
    }

    public string? LastFilter()
        => LastSearchBody().TryGetProperty("filter", out var filter) ? filter.GetString() : null;

    public JsonElement LastAutocompleteBody()
    {
        string? body;
        lock (Requests)
        {
            body = Requests.Last(r => r.Path.Contains("search.post.autocomplete")).Body;
        }
        Assert.That(body, Is.Not.Null);
        return JsonDocument.Parse(body!).RootElement;
    }

    public bool SentAutocomplete()
    {
        lock (Requests)
        {
            return Requests.Any(r => r.Path.Contains("search.post.autocomplete"));
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public string? LastOrderBy()
        => LastSearchBody().TryGetProperty("orderby", out var orderBy) ? orderBy.GetString() : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real transport returns to the caller while the request is in flight.
        await Task.Yield();

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        }

        if (BeforeRespond is not null)
            await BeforeRespond(request);

        if (Respond?.Invoke(request) is { } response)
            return response;

        if (request.Method == HttpMethod.Delete)
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        // The service has no index aliases unless a test answers them.
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/aliases("))
            return Json("""{"error":{"code":"","message":"No alias with the name was found"}}""", HttpStatusCode.NotFound);

        if (UnknownField is { } unknown
            && (request.RequestUri!.AbsolutePath.Contains("search.post.search") || request.RequestUri.AbsolutePath.Contains("search.post.autocomplete"))
            && (UnknownFieldAlways || body?.Contains(unknown) == true))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    $$$"""{"error":{"code":"","message":"Invalid expression: Could not find a property named '{{{unknown}}}' on type 'search.document'.\r\nParameter name: $orderby"}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        var json = request.RequestUri!.AbsolutePath.Contains("search.post.autocomplete")
            ? """{"value":[]}"""
            : request.RequestUri.AbsolutePath.Contains("/docs/")
            ? """{"@odata.count":0,"value":[]}"""
            : """{"name":"unit-test-index","fields":[{"name":"id","type":"Edm.String","key":true}]}""";
        var status = request.Method == HttpMethod.Post && !request.RequestUri.AbsolutePath.Contains("/docs/")
            ? HttpStatusCode.Created
            : HttpStatusCode.OK;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // Records what the services publish; Umbraco runs the handlers in PublishAsync, so OnPublish stands in for a handler.
    private sealed class RecordingEventAggregator(FakeSearchService service) : IEventAggregator
    {
        public Task PublishAsync<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            lock (service.Notifications)
            {
                service.Notifications.Add(notification);
            }

            service.OnPublish?.Invoke(notification);
            return Task.CompletedTask;
        }

        public void Publish<TNotification>(TNotification notification) where TNotification : INotification
            => PublishAsync(notification).GetAwaiter().GetResult();

        public void Publish<TNotification, TNotificationHandler>(IEnumerable<TNotification> notifications)
            where TNotification : INotification where TNotificationHandler : INotificationHandler
            => throw new NotSupportedException();

        public Task PublishAsync<TNotification, TNotificationHandler>(IEnumerable<TNotification> notifications, CancellationToken cancellationToken = default)
            where TNotification : INotification where TNotificationHandler : INotificationHandler
            => throw new NotSupportedException();

        public bool PublishCancelable<TCancelableNotification>(TCancelableNotification notification)
            where TCancelableNotification : ICancelableNotification
            => throw new NotSupportedException();

        public Task<bool> PublishCancelableAsync<TCancelableNotification>(TCancelableNotification notification)
            where TCancelableNotification : ICancelableNotification
            => throw new NotSupportedException();
    }

    private sealed class RecordingLoggerProvider(List<(LogLevel, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(logs);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<(LogLevel, string)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (logs)
            {
                logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    internal sealed class FixedServerRoleAccessor(ServerRole serverRole) : IServerRoleAccessor
    {
        public ServerRole CurrentServerRole => serverRole;
    }
}
