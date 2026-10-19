using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;

using System.Net;
using Microsoft.Extensions.Logging;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherSuggestionTests
{
    private const string IndexAlias = "unit-test-index";

    private static FakeSearchService ServiceAnsweringAutocomplete(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new()
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("search.post.autocomplete")
                ? FakeSearchService.Json(json, status)
                : null
        };

    // Azure's answer to Autocomplete on an index without the suggester.
    private const string MissingSuggesterError = """
        {"error":{"code":"MissingRequiredParameter","message":"The specified suggester name 'sg' does not exist in this index definition."}}
        """;

    private static string[] Warnings(FakeSearchService service)
        => service.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToArray();

    [Test]
    public async Task ReturnsTheCompletedQueriesAsSuggestions()
    {
        var service = ServiceAnsweringAutocomplete("""
            {"value":[{"text":"red board","queryPlusText":"red board"},{"text":"red boat","queryPlusText":"red boat"}]}
            """);

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "red bo", maxSuggestions: 5);

        Assert.That(result.Suggestions, Is.EqualTo(new[] { "red board", "red boat" }));
        var body = service.LastAutocompleteBody();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("search").GetString(), Is.EqualTo("red bo"));
            Assert.That(body.GetProperty("suggesterName").GetString(), Is.EqualTo("sg"));
            Assert.That(body.GetProperty("autocompleteMode").GetString(), Is.EqualTo("oneTermWithContext"));
            Assert.That(body.GetProperty("top").GetInt32(), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task SendsTheTrimmedQueryWithSingleSpaces()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "  red \t  bo ", maxSuggestions: 5);

        Assert.That(service.LastAutocompleteBody().GetProperty("search").GetString(), Is.EqualTo("red bo"));
    }

    [Test]
    public async Task DoesNotSendTheWildcardSuffixOfASingleWordQuery()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 5);

        Assert.That(service.LastAutocompleteBody().GetProperty("search").GetString(), Is.EqualTo("re"));
    }

    [Test]
    public async Task AsksAzureForAtMostOneHundredSuggestions()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 500);

        Assert.That(service.LastAutocompleteBody().GetProperty("top").GetInt32(), Is.EqualTo(100));
    }

    [TestCase(0, "re")]
    [TestCase(-1, "re")]
    [TestCase(5, null)]
    [TestCase(5, "")]
    [TestCase(5, "   ")]
    public async Task DoesNotAskForSuggestionsWhenNoneCanBeMade(int maxSuggestions, string? query)
    {
        var service = new FakeSearchService();

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: query, culture: "en-US", maxSuggestions: maxSuggestions);

        Assert.Multiple(() =>
        {
            Assert.That(service.SentAutocomplete(), Is.False);
            Assert.That(result.Suggestions, Is.Null);
        });
    }

    [Test]
    public async Task RemovesDuplicatesAndKeepsTheOrder()
    {
        var service = ServiceAnsweringAutocomplete("""
            {"value":[
              {"text":"red","queryPlusText":"red"},
              {"text":"read","queryPlusText":"read"},
              {"text":"Red","queryPlusText":"Red"},
              {"text":"reed","queryPlusText":"reed"}
            ]}
            """);

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 5);

        Assert.That(result.Suggestions, Is.EqualTo(new[] { "red", "read", "reed" }));
    }

    [Test]
    public async Task ReturnsAtMostTheRequestedNumberOfSuggestions()
    {
        var service = ServiceAnsweringAutocomplete("""
            {"value":[
              {"text":"red","queryPlusText":"red"},
              {"text":"read","queryPlusText":"read"},
              {"text":"reed","queryPlusText":"reed"}
            ]}
            """);

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 2);

        Assert.That(result.Suggestions, Is.EqualTo(new[] { "red", "read" }));
    }

    [Test]
    public async Task ReturnsNullWhenNothingMatches()
    {
        var service = ServiceAnsweringAutocomplete("""{"value":[]}""");

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "xyz", maxSuggestions: 5);

        Assert.That(result.Suggestions, Is.Null);
    }

    [Test]
    public async Task NarrowsSuggestionsLikeTheSearchExceptForTextFilters()
    {
        var service = new FakeSearchService();
        var memberKey = Guid.NewGuid();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "re",
            filters:
            [
                new KeywordFilter("genre", ["rock"], false),
                new IntegerExactFilter("year", [1999], true),
                new TextFilter("title", ["boat"], false)
            ],
            facets: [new KeywordFacet("genre")],
            culture: "en-US",
            segment: "seg",
            accessContext: new AccessContext(memberKey, []),
            maxSuggestions: 5);

        var searchFilter = service.LastFilter()!;
        var suggestionFilter = service.LastAutocompleteBody().GetProperty("filter").GetString()!;
        Assert.Multiple(() =>
        {
            Assert.That(suggestionFilter, Does.Contain("culture eq 'en-us'"));
            Assert.That(suggestionFilter, Does.Contain("segment eq 'seg'"));
            Assert.That(suggestionFilter, Does.Contain(memberKey.ToString("D")));
            Assert.That(suggestionFilter, Does.Contain("genre_keywords/any(k: k eq 'rock')"));
            Assert.That(suggestionFilter, Does.Contain("not (year_integers/any(f: f eq 1999))"));
            Assert.That(suggestionFilter, Does.Not.Contain("search.ismatch"));
            Assert.That(searchFilter, Does.Contain("search.ismatch"));
        });
    }

    [Test]
    public async Task DoesNotAskForSuggestionsWhenTheyAreDisabledForTheIndex()
    {
        var service = new FakeSearchService();
        service.Settings[$"UmbracoAzureSearch:Suggestions:{IndexAlias}:Enabled"] = "false";

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 5);

        Assert.Multiple(() =>
        {
            Assert.That(service.SentAutocomplete(), Is.False);
            Assert.That(result.Suggestions, Is.Null);
        });
    }

    [Test]
    public async Task UsesExactMatchingWithoutHighlights()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 5);

        var body = service.LastAutocompleteBody();
        Assert.Multiple(() =>
        {
            Assert.That(body.TryGetProperty("fuzzy", out var fuzzy) && fuzzy.GetBoolean(), Is.False);
            Assert.That(body.TryGetProperty("highlightPreTag", out _), Is.False);
            Assert.That(body.TryGetProperty("highlightPostTag", out _), Is.False);
        });
    }

    [Test]
    public async Task StillReturnsTheResultsWhenSuggestionsFail()
    {
        var service = ServiceAnsweringAutocomplete("""{"error":{"code":"","message":"Forbidden"}}""", HttpStatusCode.Forbidden);

        var result = await service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 5);

        Assert.Multiple(() =>
        {
            Assert.That(result.Suggestions, Is.Null);
            Assert.That(result.Total, Is.EqualTo(0));
            Assert.That(Warnings(service), Has.One.Contains("403"));
        });
    }

    [Test]
    public async Task WarnsOnceWhenTheIndexHasNoSuggester()
    {
        var indexAlias = $"no-suggester-{Guid.NewGuid():N}";
        var service = ServiceAnsweringAutocomplete(MissingSuggesterError, HttpStatusCode.BadRequest);

        var first = await service.CreateSearcher().SearchAsync(indexAlias, query: "re", maxSuggestions: 5);
        var second = await service.CreateSearcher().SearchAsync(indexAlias, query: "re", maxSuggestions: 5);

        Assert.Multiple(() =>
        {
            Assert.That(first.Suggestions, Is.Null);
            Assert.That(second.Suggestions, Is.Null);
            Assert.That(Warnings(service), Has.Length.EqualTo(1));
            Assert.That(Warnings(service)[0], Does.Contain(indexAlias).And.Contain("rebuild").IgnoreCase);
        });
    }

    [Test]
    public async Task StillFailsWhenTheSearchItselfFails()
    {
        var service = new FakeSearchService
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("search.post.search")
                ? FakeSearchService.Json("""{"error":{"code":"","message":"Bad request"}}""", HttpStatusCode.BadRequest)
                : null
        };

        Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => service.CreateSearcher().SearchAsync(IndexAlias, query: "re", maxSuggestions: 5));
    }

    [Test]
    public async Task SendsTheSearchFacetAndSuggestionRequestsTogether()
    {
        // Each request waits until all three have arrived; requests sent one after another time out.
        using var allArrived = new CountdownEvent(3);
        var timedOut = false;
        var service = new FakeSearchService
        {
            Respond = request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/docs/"))
                {
                    allArrived.Signal();
                    timedOut |= !allArrived.Wait(TimeSpan.FromSeconds(2));
                }
                return null;
            }
        };

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "re",
            filters: [new KeywordFilter("genre", ["rock"], false)],
            facets: [new KeywordFacet("genre")],
            maxSuggestions: 5);

        Assert.That(timedOut, Is.False);
    }
}
