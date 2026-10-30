using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;
using DeliveryApi = Umbraco.Cms.Core.DeliveryApi;
using SearchFieldNames = Umbraco.Cms.Search.Core.Constants.FieldNames;

namespace UmbracoAzureSearch.Tests.Unit;

// A new index gets the fields the definition already knows, so the first documents of a rebuild do not each update the
// schema. The fields are the same as those the first document with the field adds.
[TestFixture]
public class AzureSearchKnownFieldsTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string Name = "umb-publishedcontent";

    private static readonly DeliveryApi.IndexField[] HandlerFields =
    [
        new() { FieldName = "universe", FieldType = DeliveryApi.FieldType.StringRaw, VariesByCulture = false },
        new() { FieldName = "headline", FieldType = DeliveryApi.FieldType.StringAnalyzed, VariesByCulture = true },
        new() { FieldName = "sortTitle", FieldType = DeliveryApi.FieldType.StringSortable, VariesByCulture = false },
        new() { FieldName = "price", FieldType = DeliveryApi.FieldType.Number, VariesByCulture = false },
        new() { FieldName = "published", FieldType = DeliveryApi.FieldType.Date, VariesByCulture = false },
    ];

    // The values Umbraco Search writes: the system fields (SystemFieldsContentIndexer) and the index handler fields
    // (DeliveryApiContentIndexer: StringRaw as keywords, StringAnalyzed and StringSortable as texts, Number as decimals,
    // Date as dates).
    private static readonly IndexField[] DocumentFields =
    [
        Keywords(SearchFieldNames.Id), Keywords(SearchFieldNames.ParentId), Keywords(SearchFieldNames.PathIds),
        Keywords(SearchFieldNames.ContentTypeId), Keywords(SearchFieldNames.ObjectType), Keywords(SearchFieldNames.Tags),
        Dates(SearchFieldNames.CreateDate), Dates(SearchFieldNames.UpdateDate),
        Integers(SearchFieldNames.Level), Integers(SearchFieldNames.SortOrder),
        new(SearchFieldNames.Name, new IndexValue { TextsR1 = ["Red boat"] }, null, null),
        Keywords("universe"), Texts("headline"), Texts("sortTitle"),
        new("price", new IndexValue { Decimals = [12.5m] }, null, null), Dates("published"),
    ];

    private static IndexField Keywords(string name) => new(name, new IndexValue { Keywords = ["value"] }, null, null);

    private static IndexField Texts(string name) => new(name, new IndexValue { Texts = ["Some text"] }, null, null);

    private static IndexField Integers(string name) => new(name, new IndexValue { Integers = [3] }, null, null);

    private static IndexField Dates(string name) => new(name, new IndexValue { DateTimeOffsets = [DateTimeOffset.UtcNow] }, null, null);

    private static (FakeSearchService Service, FakeAzureIndexes Azure, IServiceProvider Services) Create(Action<AzureSearchIndexBuilder>? configure = null)
    {
        var azure = new FakeAzureIndexes();
        var service = new FakeSearchService
        {
            Respond = azure.Respond,
            ConfigureServices = services =>
            {
                services.AddSingleton(new DeliveryApi.ContentIndexHandlerCollection(() => [new FakeIndexHandler(HandlerFields)]));
                if (configure is not null)
                {
                    services.ConfigureAzureSearchIndex(IndexAlias, configure);
                }
            },
        };
        return (service, azure, service.CreateServices());
    }

    // The index as the package built it before: no field that documents bring.
    private static void AddIndexWithoutKnownFields(FakeAzureIndexes azure, IServiceProvider services, string name = Name)
    {
        var options = services.GetRequiredService<IOptionsMonitor<AzureSearchIndexOptions>>().Get(IndexAlias);
        var index = IndexSchema.Build(new PhysicalIndex(name, null), IndexAlias, new IndexDefinition(options));
        azure.AddIndexJson(name, ModelReaderWriter.Write(index).ToString());
    }

    private static Dictionary<string, string> Fields(FakeAzureIndexes azure, string name = Name)
        => azure.Fields(name).ToDictionary(f => f["name"]!.GetValue<string>(), f => f.ToJsonString());

    [Test]
    public async Task ANewIndexHasTheSystemFieldsAndTheIndexHandlerFields()
    {
        var (_, azure, services) = Create();

        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.That(Fields(azure).Keys, Is.SupersetOf(new[]
        {
            "Umb_Id_keywords", "Umb_Id_keywords_sort", "Umb_ParentId_keywords", "Umb_PathIds_keywords", "Umb_ContentTypeId_keywords",
            "Umb_ObjectType_keywords", "Umb_Tags_keywords", "Umb_CreateDate_datetimeoffsets", "Umb_CreateDate_datetimeoffsets_sort",
            "Umb_UpdateDate_datetimeoffsets", "Umb_Level_integers", "Umb_Level_integers_sort", "Umb_SortOrder_integers",
            "Umb_Name_texts", "Umb_Name_texts_r1", "Umb_Name_texts_r2", "Umb_Name_texts_r3", "Umb_Name_texts_sort",
            "universe_keywords", "universe_keywords_sort", "headline_texts", "headline_texts_r3", "headline_texts_sort",
            "sortTitle_texts", "price_decimals", "price_decimals_sort", "published_datetimeoffsets", "published_datetimeoffsets_sort",
        }));
    }

    private static readonly object[] Definitions =
    [
        new object[] { "defaults", (Action<AzureSearchIndexBuilder>)(_ => { }) },
        new object[] { "automatic fields off", (Action<AzureSearchIndexBuilder>)(index => index.AutomaticFields(false)) },
        new object[] { "changed attributes", (Action<AzureSearchIndexBuilder>)(index => index.AutomaticFields(false)
            .Field(SearchFieldNames.Name, f => f.Sortable(false).Analyzer("en.microsoft"))
            .Field("price", f => f.Facetable(false))
            .Field("headline", f => f.Searchable(false))) },
    ];

    [TestCaseSource(nameof(Definitions))]
    public async Task TheFieldsAreTheSameAsTheFieldsTheFirstDocumentAdds(string name, Action<AzureSearchIndexBuilder> configure)
    {
        var (_, upFront, upFrontServices) = Create(configure);
        await upFrontServices.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        var (_, lazy, lazyServices) = Create(configure);
        AddIndexWithoutKnownFields(lazy, lazyServices);
        var before = Fields(lazy).Keys.ToHashSet();
        await lazyServices.GetRequiredService<IIndexer>().AddOrUpdateAsync(
            IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, [new Variation(null, null)], DocumentFields, null);

        var added = Fields(lazy).Where(f => !before.Contains(f.Key)).ToDictionary();
        var created = Fields(upFront).Where(f => !before.Contains(f.Key)).ToDictionary();
        Assert.That(created, Is.EquivalentTo(added));
    }

    [Test]
    public async Task LeavesOutAFieldTheDefinitionDoesNotKeep()
    {
        var (_, azure, services) = Create(index => index.AutomaticFields(false)
            .Field(SearchFieldNames.Name, f => f.Sortable(false))
            .Field("headline", f => f.Searchable(false).Sortable(false).Filterable(false).Facetable(false)));

        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(Fields(azure).Keys, Does.Contain("Umb_Name_texts").And.Not.Contain("Umb_Name_texts_sort"));
            Assert.That(Fields(azure).Keys, Has.None.StartsWith("headline_"));
        });
    }

    // Opt-in and automatic property fields still come with the first document that has them.
    [Test]
    public async Task LeavesOutPropertyFields()
    {
        var (_, azure, services) = Create(index => index.Field("title", f => f.Searchable()));

        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.That(Fields(azure).Keys, Has.None.StartsWith("title_"));
    }

    [Test]
    public async Task DoesNotChangeAnExistingIndex()
    {
        var (service, azure, services) = Create();
        AddIndexWithoutKnownFields(azure, services);
        var before = Fields(azure);

        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(Fields(azure), Is.EquivalentTo(before));
            Assert.That(service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message), Has.None.StartsWith("Rebuild required"));
        });
    }

    [Test]
    public async Task ARebuildCreatesTheIndexWithTheFields()
    {
        var (_, azure, services) = Create();
        AddIndexWithoutKnownFields(azure, services);

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);

        Assert.That(Fields(azure).Keys, Does.Contain("Umb_Id_keywords").And.Contain("universe_keywords"));
    }

    [Test]
    public async Task AZeroDowntimeRebuildCreatesTheNewSlotWithTheFields()
    {
        var (_, azure, services) = Create(index => index.ZeroDowntimeRebuild());
        AddIndexWithoutKnownFields(azure, services, $"{Name}-a");
        azure.Aliases[Name] = $"{Name}-a";

        await services.GetRequiredService<IIndexer>().ResetAsync(IndexAlias);

        Assert.Multiple(() =>
        {
            Assert.That(Fields(azure, $"{Name}-b").Keys, Does.Contain("Umb_Id_keywords").And.Contain("universe_keywords"));
            Assert.That(Fields(azure, $"{Name}-a").Keys, Has.None.StartsWith("Umb_"));
        });
    }

    // The first document of a rebuild has only fields the new index already has, so it reads no schema and updates none.
    [Test]
    public async Task TheFirstDocumentOfARebuildUpdatesNoSchema()
    {
        var (_, azure, services) = Create();
        await services.GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);

        await services.GetRequiredService<IIndexer>().AddOrUpdateAsync(
            IndexAlias, Guid.NewGuid(), UmbracoObjectTypes.Document, [new Variation(null, null)], DocumentFields, null);

        Assert.Multiple(() =>
        {
            Assert.That(azure.Count(azure.SchemaReads, Name), Is.Zero);
            Assert.That(azure.Count(azure.SchemaUpdates, Name), Is.Zero);
        });
    }

    // The fields of the index handlers are part of the definition now, also with automatic fields on.
    [Test]
    public void CountsIndexHandlerFieldsInTheFieldBudgetWithAutomaticFieldsOn()
    {
        var (service, _, _) = Create();
        service.Settings["UmbracoAzureSearch:MaxFieldsPerIndex"] = "40";

        var exception = Assert.CatchAsync(async () =>
        {
            foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        });

        // 10 base fields, 25 system fields, 1 suggestion field, 2 for universe, 6 for headline, 6 for sortTitle, 2 for price, 2 for published.
        Assert.That(exception!.Message, Does.Contain("needs 54 Azure fields, but the service allows 40"));
    }

    private sealed class FakeIndexHandler(DeliveryApi.IndexField[] fields) : DeliveryApi.IContentIndexHandler
    {
        public IEnumerable<DeliveryApi.IndexFieldValue> GetFieldValues(IContent content, string? culture) => [];

        public IEnumerable<DeliveryApi.IndexField> GetFields() => fields;
    }
}
