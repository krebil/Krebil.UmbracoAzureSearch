using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests.Search;

// Ported from the Elasticsearch reference provider (kjac/Kjac.SearchProvider.Elasticsearch, ElasticsearchSuggestionTests.cs,
// commit 3fef9ab). Azure Autocomplete returns lowercase completions; text filters do not narrow suggestions here.
public class AzureSearchSuggestionTests : AzureSearchTestBase
{
    // Unique per run: Azure deletes indexes asynchronously, so a name from the previous run may still be in use.
    private static readonly string IndexAlias = $"suggestindex{Guid.NewGuid():N}"[..24];
    private const string FieldTitle = "title";
    private const string FieldTag = "tag";

    private readonly Guid _principalId = Guid.NewGuid();

    protected override async Task PerformOneTimeSetUpAsync()
    {
        await RecreateIndexAsync();

        // core / filter scoping data (invariant, public)
        await AddDocumentAsync("red boat", tag: "a");
        await AddDocumentAsync("red board", tag: "b");
        await AddDocumentAsync("red book", tag: "b");
        await AddDocumentAsync("read the news", tag: "b");
        await AddDocumentAsync("reading list", tag: "b");

        // access scoping data (invariant, protected)
        await AddDocumentAsync("secret manuscript", protection: new ContentProtection([_principalId]));

        // culture scoping data (single document varying by culture)
        await AddCultureDocumentAsync("umbraco workflow", "umbrella stand");

        // segment scoping data (single document with a segment variant)
        await AddSegmentDocumentAsync("default landing", "special landing");

        await WaitForIndexingOperationsToCompleteAsync();
    }

    protected override async Task PerformOneTimeTearDownAsync()
        => await DeleteIndex(ResolvedName());

    [Test]
    public async Task SuggestsNextWordFromSingleTypedWord()
    {
        SearchResult result = await SearchAsync(query: "re", maxSuggestions: 20);

        Assert.That(result.Suggestions, Is.Not.Null);
        Assert.That(result.Suggestions, Does.Contain("red"));
        Assert.That(result.Suggestions, Does.Contain("read"));
    }

    [Test]
    public async Task CompletesTrailingWordPreservingTypedPrefix()
    {
        SearchResult result = await SearchAsync(query: "red bo", maxSuggestions: 20);

        Assert.That(result.Suggestions, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result.Suggestions, Does.Contain("red boat"));
            Assert.That(result.Suggestions, Does.Contain("red board"));
            Assert.That(result.Suggestions!.All(s => s.StartsWith("red bo")), Is.True);
        });
    }

    [Test]
    public async Task LimitsSuggestionsToMaxSuggestions()
    {
        SearchResult result = await SearchAsync(query: "red", maxSuggestions: 2);

        Assert.That(result.Suggestions, Is.Not.Null);
        Assert.That(result.Suggestions!.Count(), Is.LessThanOrEqualTo(2));
    }

    [Test]
    public async Task ProducesNoSuggestionsWhenMaxSuggestionsIsZero()
    {
        SearchResult result = await SearchAsync(query: "red", maxSuggestions: 0);

        Assert.That(result.Suggestions, Is.Null);
    }

    [Test]
    public async Task ProducesNoSuggestionsForEmptyQuery()
    {
        SearchResult result = await SearchAsync(query: " ", maxSuggestions: 20);

        Assert.That(result.Suggestions, Is.Null);
    }

    [Test]
    public async Task ProducesNoSuggestionsWhenNothingMatches()
    {
        SearchResult result = await SearchAsync(query: "zzz", maxSuggestions: 20);

        Assert.That(result.Suggestions, Is.Null);
    }

    [Test]
    public async Task RespectsRegularFilter()
    {
        SearchResult result = await SearchAsync(
            query: "red bo",
            maxSuggestions: 20,
            filters: [new KeywordFilter(FieldTag, ["a"], false)]
        );

        Assert.Multiple(() =>
        {
            Assert.That(result.Suggestions, Does.Contain("red boat"));
            Assert.That(result.Suggestions, Does.Not.Contain("red board"));
            Assert.That(result.Suggestions, Does.Not.Contain("red book"));
        });
    }

    [Test]
    public async Task RespectsNegatedFilter()
    {
        SearchResult result = await SearchAsync(
            query: "red bo",
            maxSuggestions: 20,
            filters: [new KeywordFilter(FieldTag, ["a"], true)]
        );

        Assert.Multiple(() =>
        {
            Assert.That(result.Suggestions, Does.Not.Contain("red boat"));
            Assert.That(result.Suggestions, Does.Contain("red board"));
        });
    }

    // Inverted from the reference: Autocomplete rejects search.ismatchscoring, so text filters are left out.
    [Test]
    public async Task IgnoresTextFilter()
    {
        SearchResult result = await SearchAsync(
            query: "red bo",
            maxSuggestions: 20,
            filters: [new TextFilter(FieldTitle, ["boat"], false)]
        );

        Assert.Multiple(() =>
        {
            Assert.That(result.Suggestions, Does.Contain("red boat"));
            Assert.That(result.Suggestions, Does.Contain("red board"));
        });
    }

    [Test]
    public async Task ExcludesProtectedSuggestionsForAnonymous()
    {
        SearchResult result = await SearchAsync(query: "secr", maxSuggestions: 20);

        Assert.That(result.Suggestions, Is.Null);
    }

    [Test]
    public async Task IncludesProtectedSuggestionsForPrincipal()
    {
        SearchResult result = await SearchAsync(
            query: "secr",
            maxSuggestions: 20,
            accessContext: new AccessContext(_principalId, null)
        );

        Assert.That(result.Suggestions, Does.Contain("secret"));
    }

    [Test]
    public async Task ScopesSuggestionsByCulture()
    {
        SearchResult english = await SearchAsync(query: "umbr", maxSuggestions: 20, culture: "en-US");
        SearchResult danish = await SearchAsync(query: "umbr", maxSuggestions: 20, culture: "da-DK");

        Assert.Multiple(() =>
        {
            Assert.That(english.Suggestions, Does.Contain("umbraco"));
            Assert.That(english.Suggestions, Does.Not.Contain("umbrella"));

            Assert.That(danish.Suggestions, Does.Contain("umbrella"));
            Assert.That(danish.Suggestions, Does.Not.Contain("umbraco"));
        });
    }

    [Test]
    public async Task ScopesSuggestionsBySegment()
    {
        SearchResult withoutSegment = await SearchAsync(query: "spec", maxSuggestions: 20);
        SearchResult withSegment = await SearchAsync(query: "spec", maxSuggestions: 20, segment: "seg");

        Assert.Multiple(() =>
        {
            Assert.That(withoutSegment.Suggestions, Is.Null);
            Assert.That(withSegment.Suggestions, Does.Contain("special"));
        });
    }

    [Test]
    public async Task DoesNotMatchTypos()
    {
        SearchResult result = await SearchAsync(query: "boqt", maxSuggestions: 20);

        Assert.That(result.Suggestions, Is.Null);
    }

    private async Task<SearchResult> SearchAsync(
        string? query = null,
        IEnumerable<Filter>? filters = null,
        string? culture = null,
        string? segment = null,
        AccessContext? accessContext = null,
        int maxSuggestions = 0)
    {
        IAzureSearchSearcher searcher = GetRequiredService<IAzureSearchSearcher>();
        SearchResult result = await searcher.SearchAsync(
            IndexAlias,
            query: query,
            filters: filters,
            culture: culture,
            segment: segment,
            accessContext: accessContext,
            take: 100,
            maxSuggestions: maxSuggestions
        );

        Assert.That(result, Is.Not.Null);
        return result;
    }

    private Task AddCultureDocumentAsync(string englishTitle, string danishTitle)
        => AddVariantDocumentAsync(
            new IndexField(FieldTitle, new IndexValue { TextsR1 = [englishTitle] }, Culture: "en-US", Segment: null),
            new IndexField(FieldTitle, new IndexValue { TextsR1 = [danishTitle] }, Culture: "da-DK", Segment: null));

    private Task AddSegmentDocumentAsync(string defaultTitle, string segmentTitle)
        => AddVariantDocumentAsync(
            new IndexField(FieldTitle, new IndexValue { TextsR1 = [defaultTitle] }, Culture: null, Segment: null),
            new IndexField(FieldTitle, new IndexValue { TextsR1 = [segmentTitle] }, Culture: null, Segment: "seg"));

    // One document with a variation per field.
    private async Task AddVariantDocumentAsync(params IndexField[] fields)
    {
        IAzureSearchIndexer indexer = GetRequiredService<IAzureSearchIndexer>();
        await indexer.AddOrUpdateAsync(
            IndexAlias,
            Guid.NewGuid(),
            UmbracoObjectTypes.Document,
            fields.Select(f => new Variation(f.Culture, f.Segment)).ToArray(),
            fields,
            null
        );
    }

    private async Task AddDocumentAsync(string title, string? tag = null, ContentProtection? protection = null)
    {
        IAzureSearchIndexer indexer = GetRequiredService<IAzureSearchIndexer>();
        var fields = new List<IndexField>
        {
            new(FieldTitle, new IndexValue { TextsR1 = [title] }, Culture: null, Segment: null)
        };
        if (tag is not null)
        {
            fields.Add(new IndexField(FieldTag, new IndexValue { Keywords = [tag] }, Culture: null, Segment: null));
        }

        await indexer.AddOrUpdateAsync(
            IndexAlias,
            Guid.NewGuid(),
            UmbracoObjectTypes.Document,
            [new Variation(Culture: null, Segment: null)],
            fields,
            protection
        );
    }

    private string ResolvedName()
        => GetRequiredService<IIndexAliasResolver>().Resolve(IndexAlias);

    private async Task RecreateIndexAsync()
    {
        await DeleteIndex(ResolvedName());
        await GetRequiredService<IAzureSearchIndexManager>().EnsureAsync(IndexAlias);
    }
}
