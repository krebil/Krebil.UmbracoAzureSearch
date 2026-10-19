using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchIndexManagerSuggestionTests
{
    private const string IndexAlias = "unit-test-index";

    private const string IndexWithoutSuggestions = """
        {"name":"unit-test-index","fields":[{"name":"id","type":"Edm.String","key":true}]}
        """;

    private const string IndexWithSuggestions = """
        {"name":"unit-test-index","fields":[
          {"name":"id","type":"Edm.String","key":true},
          {"name":"suggestion","type":"Edm.String","searchable":true,"analyzer":"standard.lucene"}
        ],"suggesters":[{"name":"sg","searchMode":"analyzingInfixMatching","sourceFields":["suggestion"]}]}
        """;

    // Answers the index list and index definition calls as if the index has the given definition, or does not exist.
    private static FakeSearchService ServiceWithIndex(string? indexDefinition)
        => new()
        {
            Respond = request =>
            {
                if (request.Method != HttpMethod.Get)
                    return null;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/indexes"))
                    return FakeSearchService.Json(indexDefinition is null ? """{"value":[]}""" : $$"""{"value":[{{indexDefinition}}]}""");
                if (path.Contains("/indexes('") && indexDefinition is not null)
                    return FakeSearchService.Json(indexDefinition);
                return null;
            }
        };

    private static IAzureSearchIndexManager IndexManager(FakeSearchService service)
        => service.CreateServices().GetRequiredService<IAzureSearchIndexManager>();

    private static JsonElement[] SchemaWrites(FakeSearchService service)
        => service.Requests
            .Where(r => (r.Method == HttpMethod.Post || r.Method == HttpMethod.Put) && !r.Path.Contains("/docs"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .ToArray();

    private static bool HasSuggestions(JsonElement index)
        => index.GetProperty("fields").EnumerateArray().Any(f =>
               f.GetProperty("name").GetString() == "suggestion"
               && f.GetProperty("type").GetString() == "Edm.String"
               && f.GetProperty("searchable").GetBoolean()
               && f.GetProperty("analyzer").GetString() == "standard.lucene")
           && index.TryGetProperty("suggesters", out var suggesters)
           && suggesters.EnumerateArray().Any(s =>
               s.GetProperty("name").GetString() == "sg"
               && s.GetProperty("sourceFields").EnumerateArray().Select(f => f.GetString()).SequenceEqual(["suggestion"]));

    [Test]
    public async Task CreatesANewIndexWithSuggestions()
    {
        var service = ServiceWithIndex(null);

        await IndexManager(service).EnsureAsync(IndexAlias);

        Assert.That(SchemaWrites(service).Single(), Has.Property(nameof(JsonElement.ValueKind)).EqualTo(JsonValueKind.Object)
            .And.Matches<JsonElement>(HasSuggestions));
    }

    [Test]
    public async Task AddsSuggestionsToAnExistingIndexInOneUpdate()
    {
        var service = ServiceWithIndex(IndexWithoutSuggestions);

        await IndexManager(service).EnsureAsync(IndexAlias);

        var update = service.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Multiple(() =>
        {
            Assert.That(SchemaWrites(service), Has.Length.EqualTo(1));
            Assert.That(update.Path, Does.Contain("unit-test-index"));
            Assert.That(HasSuggestions(SchemaWrites(service).Single()), Is.True);
            Assert.That(
                service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message),
                Has.One.Contains("unit-test-index").And.Contains("rebuild").IgnoreCase);
        });
    }

    [Test]
    public async Task LeavesAnIndexWithSuggestionsAlone()
    {
        var service = ServiceWithIndex(IndexWithSuggestions);

        await IndexManager(service).EnsureAsync(IndexAlias);

        Assert.That(SchemaWrites(service), Is.Empty);
    }

    [Test]
    public async Task DoesNotAddSuggestionsWhenTheyAreDisabledForTheIndex()
    {
        var service = ServiceWithIndex(IndexWithoutSuggestions);
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";

        await IndexManager(service).EnsureAsync(IndexAlias);

        Assert.That(SchemaWrites(service), Is.Empty);
    }

    [Test]
    public async Task CreatesANewIndexWithoutSuggestionsWhenTheyAreDisabled()
    {
        var service = ServiceWithIndex(null);
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";

        await IndexManager(service).EnsureAsync(IndexAlias);

        var created = SchemaWrites(service).Single();
        Assert.Multiple(() =>
        {
            Assert.That(created.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()), Does.Not.Contain("suggestion"));
            Assert.That(created.TryGetProperty("suggesters", out var suggesters) && suggesters.GetArrayLength() > 0, Is.False);
        });
    }

    [Test]
    public async Task ResetRecreatesTheIndexWithSuggestions()
    {
        var service = ServiceWithIndex(IndexWithoutSuggestions);

        await IndexManager(service).ResetAsync(IndexAlias);

        var created = SchemaWrites(service).Single();
        Assert.Multiple(() =>
        {
            Assert.That(service.Requests.Select(r => r.Method), Does.Contain(HttpMethod.Delete));
            Assert.That(HasSuggestions(created), Is.True);
            Assert.That(created.GetProperty("defaultScoringProfile").GetString(), Is.EqualTo("relevanceBoost"));
        });
    }

    [Test]
    public async Task ResetDropsSuggestionsWhenTheyAreDisabled()
    {
        var service = ServiceWithIndex(IndexWithSuggestions);
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";

        await IndexManager(service).ResetAsync(IndexAlias);

        Assert.That(HasSuggestions(SchemaWrites(service).Single()), Is.False);
    }
}
