using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexDefinitionTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private static readonly Guid ArticleType = Guid.NewGuid();
    private static readonly Guid FolderType = Guid.NewGuid();

    private static readonly Variation[] Invariant = [new(Culture: null, Segment: null)];

    private static IndexField ContentType(Guid key) => Keywords(Umbraco.Cms.Search.Core.Constants.FieldNames.ContentTypeId, key.ToString("D"));

    private static IndexField Keywords(string name, string value, string? culture = null)
        => new(name, new IndexValue { Keywords = [value] }, culture, null);

    private static IndexField Text(string name, string value, string? culture = null)
        => new(name, new IndexValue { Texts = [value] }, culture, null);

    private static FakeSearchService Service(Action<AzureSearchIndexBuilder>? configure = null)
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services =>
            {
                services.AddSingleton<IUmbracoContentSource>(new FakeContentSource());
                if (configure is not null)
                {
                    services.ConfigureAzureSearchIndex(IndexAlias, configure);
                }
            },
        };
        return service;
    }

    private static Task IndexAsync(FakeSearchService service, Variation[] variations, params IndexField[] fields)
        => service.CreateServices().GetRequiredService<IAzureSearchIndexer>()
            .AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, variations, fields, null);

    private static JsonElement[] Documents(FakeSearchService service)
    {
        var body = service.Requests.Last(r => r.Path.Contains("search.index")).Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().ToArray();
    }

    private static string[] Properties(JsonElement document)
        => document.EnumerateObject().Select(p => p.Name).Where(n => !n.StartsWith('@')).ToArray();

    // The fields the indexer added to the schema, from the last index update.
    private static Dictionary<string, JsonElement> SchemaFields(FakeSearchService service)
    {
        var body = service.Requests.Last(r => r.Method == HttpMethod.Put && r.Path.Contains("indexes")).Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
    }

    private static bool Attribute(JsonElement field, string name)
        => field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string[] AllTexts(JsonElement document)
        => document.GetProperty("allTexts").EnumerateArray().Select(t => t.GetString()!).ToArray();

    [Test]
    public async Task WritesEveryFieldWithAutomaticFieldsOn()
    {
        var service = Service();

        await IndexAsync(service, Invariant, Text("title", "Red boat"), Keywords("author", "ann"));

        Assert.That(Properties(Documents(service).Single()), Does.Contain("title_texts").And.Contains("author_keywords"));
    }

    [Test]
    public async Task WritesOnlyOptedInAndSystemFieldsWithAutomaticFieldsOff()
    {
        var service = Service(index => index.AutomaticFields(false).Field("title", f => f.Searchable()));

        await IndexAsync(service, Invariant,
            Text("title", "Red boat"),
            Text("body", "hidden text"),
            Keywords("deliveryApiField", "x"),
            Keywords(Umbraco.Cms.Search.Core.Constants.FieldNames.PathIds, Guid.NewGuid().ToString("D")));

        var document = Documents(service).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Properties(document), Does.Contain("title_texts").And.Contains("Umb_PathIds_keywords"));
            Assert.That(Properties(document), Has.None.StartsWith("body_").And.None.StartsWith("deliveryApiField_"));
            Assert.That(AllTexts(document), Is.EqualTo(new[] { "Red boat" }), "Fields outside the index do not feed the free-text query.");
        });
    }

    [Test]
    public async Task WritesOnlyTheAttributesAnOptedInFieldHas()
    {
        var service = Service(index => index.AutomaticFields(false).Field("date", f => f.Filterable().Sortable()));

        await IndexAsync(service, Invariant, new IndexField("date", new IndexValue { DateTimeOffsets = [DateTimeOffset.UnixEpoch] }, null, null));

        var fields = SchemaFields(service);
        Assert.Multiple(() =>
        {
            Assert.That(Attribute(fields["date_datetimeoffsets"], "filterable"), Is.True);
            Assert.That(Attribute(fields["date_datetimeoffsets"], "facetable"), Is.False);
            Assert.That(Attribute(fields["date_datetimeoffsets_sort"], "sortable"), Is.True);
        });
    }

    [Test]
    public async Task ChangesTheAttributesOfAnAutomaticField()
    {
        var service = Service(index => index
            .Field("summary", f => f.Searchable(false))
            .Field("title", f => f.Analyzer("en.microsoft"))
            .Field("author", f => f.Facetable(false)));

        await IndexAsync(service, Invariant, Text("summary", "not searchable"), Text("title", "Red boat"), Keywords("author", "ann"));

        var document = Documents(service).Single();
        var fields = SchemaFields(service);
        Assert.Multiple(() =>
        {
            Assert.That(Properties(document), Has.None.StartsWith("summary_texts_r"));
            Assert.That(Properties(document), Does.Contain("summary_texts_sort"), "Unset attributes keep the automatic value.");
            Assert.That(AllTexts(document), Is.EqualTo(new[] { "Red boat" }));
            Assert.That(fields["title_texts"].GetProperty("analyzer").GetString(), Is.EqualTo("en.microsoft"));
            Assert.That(Attribute(fields["author_keywords"], "facetable"), Is.False);
            Assert.That(Attribute(fields["author_keywords"], "filterable"), Is.True);
        });
    }

    [Test]
    public async Task ReadsFieldsAndAutomaticFieldsFromAppSettings()
    {
        var service = Service();
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:AutomaticFields"] = "false";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Fields:author:Filterable"] = "true";

        await IndexAsync(service, Invariant, Keywords("author", "ann"), Keywords("category", "news"));

        var properties = Properties(Documents(service).Single());
        Assert.Multiple(() =>
        {
            Assert.That(properties, Does.Contain("author_keywords"));
            Assert.That(properties, Has.None.StartsWith("category_"));
        });
    }

    [Test]
    public async Task CodeWinsOverAppSettingsPerAttribute()
    {
        var service = Service(index => index.Field("author", f => f.Facetable()));
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:AutomaticFields"] = "false";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Fields:author:Filterable"] = "true";
        service.Settings[$"UmbracoAzureSearch:Indexes:{IndexAlias}:Fields:author:Facetable"] = "false";

        await IndexAsync(service, Invariant, Keywords("author", "ann"));

        var field = SchemaFields(service)["author_keywords"];
        Assert.Multiple(() =>
        {
            Assert.That(Attribute(field, "facetable"), Is.True);
            Assert.That(Attribute(field, "filterable"), Is.True);
        });
    }

    [Test]
    public async Task FillsSuggestionsFromSuggestableFields()
    {
        var service = Service(index => index.Field("title", f => f.Searchable().Suggestable()));

        await IndexAsync(service, Invariant,
            new IndexField("title", new IndexValue { TextsR2 = ["Red boat"] }, null, null),
            new IndexField("name", new IndexValue { TextsR1 = ["Ignored name"] }, null, null));

        Assert.That(Documents(service).Single().GetProperty("suggestion").GetString(), Is.EqualTo("Red boat"));
    }

    [Test]
    public async Task SkipsDocumentsOfOtherContentTypes()
    {
        var service = Service(index => index.ContentTypes("article"));

        await IndexAsync(service, Invariant, ContentType(FolderType), Text("title", "Folder"));

        Assert.That(service.Requests.Any(r => r.Path.Contains("search.index")), Is.False);
    }

    [Test]
    public async Task WritesDocumentsOfTheScopedContentTypes()
    {
        var service = Service(index => index.ContentTypes("folder", "article"));

        await IndexAsync(service, Invariant, ContentType(ArticleType), Text("title", "Article"));

        Assert.That(Documents(service), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task WritesOnlyTheScopedCultures()
    {
        var service = Service(index => index.Cultures("pt", "en-US"));

        await IndexAsync(service,
            [new Variation("pt-BR", null), new Variation("en-US", null), new Variation("en-GB", null), new Variation("da-DK", null)],
            Text("title", "Barco", "pt-BR"), Text("title", "Boat", "en-US"), Text("title", "Boat", "en-GB"), Text("title", "Båd", "da-DK"));

        Assert.That(Documents(service).Select(d => d.GetProperty("culture").GetString()), Is.EqualTo(new[] { "pt-br", "en-us" }));
    }

    [Test]
    public async Task AlwaysWritesInvariantDocuments()
    {
        var service = Service(index => index.Cultures("fr"));

        await IndexAsync(service, Invariant, Text("title", "Boat"));

        Assert.That(Documents(service), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RunsAPerIndexIndexerForThatIndexOnly()
    {
        var service = Service(index => index.AutomaticFields(false).Indexer<SiteSearchTextIndexer>().Field("siteText", f => f.Searchable()));
        var services = service.CreateServices();

        await services.GetRequiredService<IAzureSearchIndexer>()
            .AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, Invariant, [Text("title", "Red boat")], null);
        var indexed = Documents(service).Single();

        await services.GetRequiredService<IAzureSearchIndexer>()
            .AddOrUpdateAsync("Umb_Content", Guid.NewGuid(), UmbracoObjectTypes.Document, Invariant, [Text("title", "Red boat")], null);
        var other = Documents(service).Single();

        Assert.Multiple(() =>
        {
            Assert.That(AllTexts(indexed), Is.EqualTo(new[] { "site text, published: True" }));
            Assert.That(Properties(other), Has.None.StartsWith("siteText_"));
        });
    }

    [Test]
    public async Task AppliesTheDefinitionToDirectIndexerCalls()
    {
        var service = Service(index => index.AutomaticFields(false));

        await service.CreateServices().GetRequiredService<IIndexer>()
            .AddOrUpdateAsync(IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, Invariant, [Text("title", "Red boat")], null);

        Assert.That(Properties(Documents(service).Single()), Has.None.StartsWith("title_"));
    }

    [Test]
    public void StopsStartupForASuggestableFieldThatIsNotSearchable()
    {
        var service = Service(index => index.AutomaticFields(false).Field("title", f => f.Suggestable()));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("\"title\" is suggestable but not searchable"));
    }

    [Test]
    public void StopsStartupForAnUnknownAnalyzer()
    {
        var service = Service(index => index.Field("title", f => f.Analyzer("en.microsft")));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        Assert.That(exception!.Message, Does.Contain("\"en.microsft\""));
    }

    [Test]
    public void StopsStartupWhenTheFieldsExceedTheTierLimit()
    {
        var service = Service(index => index
            .AutomaticFields(false)
            .Field("title", f => f.Searchable().Sortable())
            .Field("date", f => f.Filterable().Sortable()));
        service.Settings["UmbracoAzureSearch:MaxFieldsPerIndex"] = "40";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service));

        // 10 base fields, 25 system fields, 1 suggestion field, 5 for title and 2 for date.
        Assert.That(exception!.Message, Does.Contain("needs 43 Azure fields, but the service allows 40"));
    }

    [Test]
    public void AcceptsAValidDefinition()
    {
        var service = Service(index => index
            .AutomaticFields(false)
            .Field("title", f => f.Searchable().Suggestable().Analyzer("fr.microsoft").Boost(3)));

        Assert.DoesNotThrowAsync(() => StartAsync(service));
    }

    private static async Task StartAsync(FakeSearchService service)
    {
        foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private sealed class FakeContentSource : IUmbracoContentSource
    {
        public Guid? GetContentTypeKey(string alias, UmbracoObjectTypes objectType) => alias switch
        {
            "article" => ArticleType,
            "folder" => FolderType,
            _ => null,
        };

        public IContentBase GetContent(Guid id, UmbracoObjectTypes objectType) => DispatchProxy.Create<IContentBase, DefaultProxy>();
    }

    // Answers every member with its default value; the test indexer does not read the content.
    public class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }

    private sealed class SiteSearchTextIndexer : IContentIndexer
    {
        public Task<IEnumerable<IndexField>> GetIndexFieldsAsync(IContentBase content, string?[] cultures, bool published, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<IndexField>>([Text("siteText", $"site text, published: {published}")]);
    }
}
