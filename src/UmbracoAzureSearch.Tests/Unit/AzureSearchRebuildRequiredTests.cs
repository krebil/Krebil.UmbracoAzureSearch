using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Configuration;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchRebuildRequiredTests
{
    private const string IndexAlias = "Umb_PublishedContent";
    private const string Name = "umb-publishedcontent";

    // The index as the package builds it with default options, plus extra fields that documents brought.
    private static string ExistingIndex(params string[] extraFields)
    {
        var json = JsonNode.Parse(ModelReaderWriter.Write(IndexSchema.Build(
            new PhysicalIndex(Name, null), IndexAlias, new IndexDefinition(new AzureSearchIndexOptions()))).ToString())!;
        foreach (var field in extraFields)
        {
            json["fields"]!.AsArray().Add(JsonNode.Parse(field));
        }

        return json.ToJsonString();
    }

    private static async Task<string[]> RebuildRequiredAsync(Action<AzureSearchIndexBuilder> configure, string existingIndex)
    {
        var service = new FakeSearchService
        {
            ConfigureServices = services => services.ConfigureAzureSearchIndex(IndexAlias, configure),
            Respond = request =>
            {
                if (request.Method != HttpMethod.Get)
                    return null;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/indexes"))
                    return FakeSearchService.Json($$"""{"value":[{{existingIndex}}]}""");
                if (path.Contains("/indexes('") && path.EndsWith("')"))
                    return FakeSearchService.Json(existingIndex);
                return null;
            },
        };

        await service.CreateServices().GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);
        return service.Logs.Where(l => l.Level == LogLevel.Warning && l.Message.StartsWith("Rebuild required")).Select(l => l.Message).ToArray();
    }

    private const string TitleTexts = """{"name":"title_texts","type":"Collection(Edm.String)","searchable":true,"filterable":true,"analyzer":"standard.lucene"}""";
    private const string BodyTexts = """{"name":"body_texts","type":"Collection(Edm.String)","searchable":true,"filterable":true}""";
    private const string AuthorKeywords = """{"name":"author_keywords","type":"Collection(Edm.String)","filterable":true,"facetable":true}""";

    [Test]
    public async Task ReportsNothingForAnIndexThatMatchesTheDefinition()
    {
        var messages = await RebuildRequiredAsync(_ => { }, ExistingIndex(TitleTexts, AuthorKeywords));

        Assert.That(messages, Is.Empty);
    }

    [Test]
    public async Task ReportsANewAnalyzerOnAnExistingField()
    {
        var messages = await RebuildRequiredAsync(index => index.Field("title", f => f.Analyzer("en.microsoft")), ExistingIndex(TitleTexts));

        Assert.That(messages, Has.One.Contains("the analyzer of field title_texts is standard.lucene, and the definition sets en.microsoft"));
    }

    [Test]
    public async Task ReportsFieldsThatAutomaticFieldsOffLeavesOut()
    {
        var messages = await RebuildRequiredAsync(
            index => index.AutomaticFields(false).Field("title", f => f.Searchable().Filterable()),
            ExistingIndex(TitleTexts, BodyTexts));

        Assert.That(messages, Has.One.Contains("field body_texts is in the index, and the definition leaves body out"));
    }

    [Test]
    public async Task ReportsAChangedAttribute()
    {
        var messages = await RebuildRequiredAsync(index => index.Field("author", f => f.Facetable(false)), ExistingIndex(AuthorKeywords));

        Assert.That(messages, Has.One.Contains("field author_keywords is filterable True and facetable True, and the definition sets True and False"));
    }

    [Test]
    public async Task ReportsNewSuggesterFields()
    {
        var messages = await RebuildRequiredAsync(
            index => index.Analyzers(a => a.FieldPerCulture().Culture("fr", "fr.microsoft")),
            ExistingIndex());

        Assert.That(messages, Has.One.Contains("the suggester fields are suggestion, and the definition sets"));
    }

    [Test]
    public async Task ReportsAnAnalyzerThatConfigureIndexAdds()
    {
        var messages = await RebuildRequiredAsync(
            index => index.ConfigureIndex(searchIndex => searchIndex.Analyzers.Add(new CustomAnalyzer("folded", LexicalTokenizerName.Standard))),
            ExistingIndex());

        Assert.That(messages, Has.One.Contains("the text analysis component folded is new"));
    }
}
