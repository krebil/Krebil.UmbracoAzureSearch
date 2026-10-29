using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Scoring;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchScoringProfileTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string Name = "umb-publishedcontent";

    private static ScoringProfile Recency(string field = "date_datetimeoffsets_sort")
        => new("recency") { Functions = { new FreshnessScoringFunction(field, 2, new FreshnessScoringParameters(TimeSpan.FromDays(30))) } };

    // A service with one index. existingIndex null: the index does not exist yet. PUT replaces the index.
    private sealed class IndexService
    {
        public FakeSearchService Service { get; } = new();
        public string? Index { get; set; }
        public List<JsonElement> Puts { get; } = [];
        public JsonElement? Created { get; private set; }

        public IndexService(Action<AzureSearchIndexBuilder>? configure, IScoringProfileProvider? provider, string? existingIndex)
        {
            Index = existingIndex;
            Service.ConfigureServices = services =>
            {
                if (configure is not null)
                {
                    services.ConfigureAzureSearchIndex(IndexAlias, configure);
                }

                if (provider is not null)
                {
                    services.AddSingleton(provider);
                }
            };
            Service.Respond = request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/indexes"))
                    return FakeSearchService.Json(Index is null ? """{"value":[]}""" : $$"""{"value":[{{Index}}]}""");
                if (request.Method == HttpMethod.Post && path.EndsWith("/indexes"))
                {
                    Index = request.Content!.ReadAsStringAsync().Result;
                    Created = JsonDocument.Parse(Index).RootElement;
                    return FakeSearchService.Json(Index, System.Net.HttpStatusCode.Created);
                }

                if (path.Contains($"/indexes('{Name}')") && path.EndsWith("')"))
                {
                    if (request.Method == HttpMethod.Get)
                        return Index is null ? FakeSearchService.Json("""{"error":{"message":"Not found"}}""", System.Net.HttpStatusCode.NotFound) : FakeSearchService.Json(Index);
                    if (request.Method == HttpMethod.Put)
                    {
                        Index = request.Content!.ReadAsStringAsync().Result;
                        Puts.Add(JsonDocument.Parse(Index).RootElement);
                        return FakeSearchService.Json(Index);
                    }
                }

                return null;
            };
        }

        public Task EnsureAsync() => Service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);
    }

    private static string BuiltIndex(Action<AzureSearchIndexOptions>? configure = null, params ScoringProfile[] extraProfiles)
    {
        var options = new AzureSearchIndexOptions();
        configure?.Invoke(options);
        var index = IndexSchema.Build(new PhysicalIndex(Name, null), IndexAlias, new IndexDefinition(options));
        foreach (var profile in extraProfiles)
        {
            index.ScoringProfiles.Add(profile);
        }

        return ModelReaderWriter.Write(index).ToString();
    }

    private static Dictionary<string, JsonElement> Profiles(JsonElement index)
        => index.GetProperty("scoringProfiles").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p);

    private static Dictionary<string, double> Weights(JsonElement profile)
        => profile.GetProperty("text").GetProperty("weights").EnumerateObject().ToDictionary(w => w.Name, w => w.Value.GetDouble());

    private static JsonElement Field(JsonElement index, string name)
        => index.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);

    private static bool Flag(JsonElement field, string name) => field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Default(JsonElement index)
        => index.TryGetProperty("defaultScoringProfile", out var value) ? value.GetString() : null;

    [Test]
    public async Task GeneratesUmbracoBoostsFromFieldBoosts()
    {
        var service = new IndexService(index => index.Field("title", f => f.Searchable().Boost(3)), null, null);

        await service.EnsureAsync();

        var created = service.Created!.Value;
        var weights = Weights(Profiles(created)["umbraco-boosts"]);
        Assert.Multiple(() =>
        {
            Assert.That(weights["title_texts"], Is.EqualTo(3));
            Assert.That(weights["title_texts_r1"], Is.EqualTo(3));
            Assert.That(weights["allTextsR1"], Is.EqualTo(4), "The relevance weights stay.");
            Assert.That(Default(created), Is.EqualTo("umbraco-boosts"));
            Assert.That(Flag(Field(created, "title_texts_r2"), "searchable"), Is.True, "Boosted fields exist before documents bring them.");
        });
    }

    [Test]
    public async Task KeepsRelevanceBoostAsTheDefaultWithoutBoosts()
    {
        var service = new IndexService(null, null, null);

        await service.EnsureAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Profiles(service.Created!.Value).Keys, Is.EqualTo(new[] { "relevanceBoost" }));
            Assert.That(Default(service.Created!.Value), Is.EqualTo("relevanceBoost"));
        });
    }

    [Test]
    public async Task BoostsThePerCultureCopiesOfAField()
    {
        var service = new IndexService(index => index
            .Analyzers(a => a.FieldPerCulture().Culture("fr", "fr.microsoft"))
            .Field("title", f => f.Searchable().PerCulture().Boost(2)), null, null);

        await service.EnsureAsync();

        var weights = Weights(Profiles(service.Created!.Value)["umbraco-boosts"]);
        Assert.That(weights, Does.ContainKey("title_texts_r1_fr").And.ContainKey("title_texts_inv").And.ContainKey("allTextsR1_fr"));
    }

    [Test]
    public async Task AddsProviderProfilesAndTheFieldsTheyNeed()
    {
        var service = new IndexService(index => index.DefaultScoringProfile("recency"), new Provider(Recency()), null);

        await service.EnsureAsync();

        var created = service.Created!.Value;
        var date = Field(created, "date_datetimeoffsets_sort");
        Assert.Multiple(() =>
        {
            Assert.That(Profiles(created).Keys, Does.Contain("recency"));
            Assert.That(Default(created), Is.EqualTo("recency"));
            Assert.That(date.GetProperty("type").GetString(), Is.EqualTo("Edm.DateTimeOffset"));
            Assert.That(Flag(date, "filterable"), Is.True, "A scoring function needs a filterable field.");
            Assert.That(Flag(date, "sortable"), Is.True);
        });
    }

    [Test]
    public async Task LaterSourcesWinOnANameCollision()
    {
        var fromProvider = new ScoringProfile("umbraco-boosts") { TextWeights = new TextWeights(new Dictionary<string, double> { ["allTexts"] = 9 }) };
        var service = new IndexService(
            index => index
                .Field("title", f => f.Searchable().Boost(3))
                .ConfigureIndex(searchIndex => searchIndex.ScoringProfiles.Single(p => p.Name == "umbraco-boosts").TextWeights.Weights["allTexts"] = 7),
            new Provider(fromProvider),
            null);

        await service.EnsureAsync();

        Assert.That(Weights(Profiles(service.Created!.Value)["umbraco-boosts"]), Is.EqualTo(new Dictionary<string, double> { ["allTexts"] = 7 }));
    }

    [Test]
    public async Task UpdatesAnExistingIndexAndKeepsLeftoverProfiles()
    {
        var handMade = new ScoringProfile("handMade") { TextWeights = new TextWeights(new Dictionary<string, double> { ["allTexts"] = 2 }) };
        var service = new IndexService(null, new Provider(Recency()), BuiltIndex(extraProfiles: handMade));

        await service.EnsureAsync();

        var update = service.Puts.Single();
        Assert.Multiple(() =>
        {
            Assert.That(Profiles(update).Keys, Is.EquivalentTo(new[] { "relevanceBoost", "handMade", "recency" }));
            Assert.That(service.Service.Logs.Where(l => l.Level == LogLevel.Information).Select(l => l.Message),
                Has.One.Contains("no source returns: handMade"));
        });
    }

    [Test]
    public async Task RemovesUmbracoBoostsWhenNoFieldHasABoost()
    {
        var existing = BuiltIndex(options => options.Fields["title"] = new FieldOptions { Searchable = true, Boost = 3 });
        var service = new IndexService(null, null, existing);

        await service.EnsureAsync();

        var update = service.Puts.Single();
        Assert.Multiple(() =>
        {
            Assert.That(Profiles(update).Keys, Is.EqualTo(new[] { "relevanceBoost" }));
            Assert.That(Default(update), Is.EqualTo("relevanceBoost"));
        });
    }

    [Test]
    public async Task SkipsAProfileWhoseFieldIsNotFilterableOnTheLiveIndex()
    {
        var existing = JsonNode.Parse(BuiltIndex())!;
        existing["fields"]!.AsArray().Add(JsonNode.Parse("""{"name":"date_datetimeoffsets_sort","type":"Edm.DateTimeOffset","sortable":true,"filterable":false}"""));
        var service = new IndexService(null, new Provider(Recency()), existing.ToJsonString());

        await service.EnsureAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.Puts, Is.Empty);
            Assert.That(service.Service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message),
                Has.One.Contains("Rebuild required").And.Contains("recency").And.Contains("needs a filterable field"));
        });
    }

    [Test]
    public async Task RemovesAProfileOnRequest()
    {
        var service = new IndexService(null, null, BuiltIndex(extraProfiles: Recency()));

        await service.Service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().RemoveScoringProfileAsync(IndexAlias, "recency");

        Assert.That(Profiles(service.Puts.Single()).Keys, Is.EqualTo(new[] { "relevanceBoost" }));
    }

    [TestCase("umbraco-boosts", "belongs to the package")]
    [TestCase("relevanceBoost", "belongs to the package")]
    [TestCase("recency", "is the default profile")]
    public void RefusesToRemoveAPackageOrDefaultProfile(string profile, string message)
    {
        var service = new IndexService(index => index.DefaultScoringProfile("recency"), new Provider(Recency()), BuiltIndex(extraProfiles: Recency()));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.Service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().RemoveScoringProfileAsync(IndexAlias, profile));

        Assert.That(exception!.Message, Does.Contain(message));
    }

    [Test]
    public async Task RefreshesProviderProfilesWithoutARebuild()
    {
        var provider = new Provider(Recency());
        var service = new IndexService(null, provider, BuiltIndex(extraProfiles: Recency()));
        provider.Profiles = [new ScoringProfile("recency") { Functions = { new FreshnessScoringFunction("date_datetimeoffsets_sort", 5, new FreshnessScoringParameters(TimeSpan.FromDays(7))) } }];

        await service.Service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().RefreshScoringProfilesAsync(IndexAlias);

        var function = Profiles(service.Puts.Single())["recency"].GetProperty("functions")[0];
        Assert.That(function.GetProperty("boost").GetDouble(), Is.EqualTo(5));
    }

    [Test]
    public void ARefreshFailsForAProfileTheIndexCannotTake()
    {
        var provider = new Provider(Recency("title_texts"));
        var service = new IndexService(null, provider, BuiltIndex());

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.Service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().RefreshScoringProfilesAsync(IndexAlias));

        Assert.That(exception!.Message, Does.Contain("a freshness function needs a single date field"));
    }

    [Test]
    public async Task SendsTheScoringProfileOfAQuery()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "boat", culture: "fr-FR", scoringProfile: "geo", scoringParameters: ["currentLocation--122.1,44.7"]);

        var body = service.LastSearchBody();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("scoringProfile").GetString(), Is.EqualTo("geo"));
            Assert.That(body.GetProperty("scoringParameters")[0].GetString(), Is.EqualTo("currentLocation--122.1,44.7"));
        });
    }

    [TestCase("title_texts", "a freshness function needs a single date field")]
    [TestCase("date_datetimeoffsets", "a freshness function needs a single date field")]
    [TestCase("lastUpdated", "which the index does not have")]
    public void StopsStartupForAProfileTheIndexCannotTake(string field, string message)
    {
        var service = new IndexService(null, new Provider(Recency(field)), null);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service.Service));

        Assert.That(exception!.Message, Does.Contain(message));
    }

    [Test]
    public void StopsStartupForADefaultProfileThatDoesNotExist()
    {
        var service = new IndexService(index => index.DefaultScoringProfile("recency"), null, null);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service.Service));

        Assert.That(exception!.Message, Does.Contain("the default scoring profile \"recency\" is not a profile of the index"));
    }

    [Test]
    public void StopsStartupForABoostThatIsNotPositive()
    {
        var service = new IndexService(index => index.Field("title", f => f.Searchable().Boost(0)), null, null);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(service.Service));

        Assert.That(exception!.Message, Does.Contain("A boost must be greater than 0"));
    }

    [Test]
    public void AcceptsAValidProvider()
    {
        var service = new IndexService(index => index.DefaultScoringProfile("recency"), new Provider(Recency()), null);

        Assert.DoesNotThrowAsync(() => StartAsync(service.Service));
    }

    private static async Task StartAsync(FakeSearchService service)
    {
        foreach (var hostedService in service.CreateServices().GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private sealed class Provider(params ScoringProfile[] profiles) : IScoringProfileProvider
    {
        public ScoringProfile[] Profiles { get; set; } = profiles;

        public Task<IEnumerable<ScoringProfile>> GetScoringProfilesAsync(string indexAlias, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<ScoringProfile>>(indexAlias == IndexAlias ? Profiles : []);
    }
}
