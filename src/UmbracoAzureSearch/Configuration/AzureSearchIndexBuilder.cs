using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// Sets the options of one index in code. Runs after appsettings, so a value set here wins.
/// </summary>
public sealed class AzureSearchIndexBuilder
{
    private readonly AzureSearchIndexOptions _options;

    internal AzureSearchIndexBuilder(AzureSearchIndexOptions options) => _options = options;

    /// <summary>Turns suggestions on or off for the index. Same as <c>Suggestions:Enabled</c>.</summary>
    public AzureSearchIndexBuilder Suggestions(bool enabled)
    {
        _options.Suggestions.Enabled = enabled;
        return this;
    }

    /// <summary>The Umbraco fields whose texts feed suggestions. Same as <c>Suggestions:Fields</c>.</summary>
    public AzureSearchIndexBuilder SuggestionFields(params string[] fields)
    {
        _options.Suggestions.Fields = fields;
        return this;
    }

    /// <summary>The tags <c>SuggestAsync</c> puts around a match. Same as <c>Suggestions:HighlightPreTag</c> and <c>HighlightPostTag</c>.</summary>
    public AzureSearchIndexBuilder SuggestionHighlight(string preTag, string postTag)
    {
        _options.Suggestions.HighlightPreTag = preTag;
        _options.Suggestions.HighlightPostTag = postTag;
        return this;
    }

    /// <summary>
    /// Rebuilds into a second Azure index behind an Azure alias and swaps when the rebuild succeeds.
    /// Same as <c>ZeroDowntimeRebuild: true</c>.
    /// </summary>
    public AzureSearchIndexBuilder ZeroDowntimeRebuild(Action<AzureSearchRebuildBuilder>? configure = null)
    {
        _options.ZeroDowntimeRebuild = true;
        configure?.Invoke(new AzureSearchRebuildBuilder(_options));
        return this;
    }

    /// <summary>A rebuild clears the cached umbracoIndexDocument rows. Same as <c>ClearDocumentCacheOnRebuild</c>.</summary>
    public AzureSearchIndexBuilder ClearDocumentCacheOnRebuild(bool clear = true)
    {
        _options.ClearDocumentCacheOnRebuild = clear;
        return this;
    }

    /// <summary>The scoring profile a query uses when it names none. Same as <c>DefaultScoringProfile</c>.</summary>
    public AzureSearchIndexBuilder DefaultScoringProfile(string profileName)
    {
        _options.DefaultScoringProfile = profileName;
        return this;
    }

    /// <summary>Turns on spell check per culture. Same as <c>SpellCheck</c>.</summary>
    public AzureSearchIndexBuilder SpellCheck(Action<AzureSearchSpellCheckBuilder> configure)
    {
        configure(new AzureSearchSpellCheckBuilder(_options.SpellCheck));
        return this;
    }

    /// <summary>Writes every field (on, the default) or only the opted-in fields (off). Same as <c>AutomaticFields</c>.</summary>
    public AzureSearchIndexBuilder AutomaticFields(bool enabled)
    {
        _options.AutomaticFields = enabled;
        return this;
    }

    /// <summary>Sets the analyzers of the index. Same as <c>Analyzers</c>.</summary>
    public AzureSearchIndexBuilder Analyzers(Action<AzureSearchAnalyzerBuilder> configure)
    {
        configure(new AzureSearchAnalyzerBuilder(_options.Analyzers));
        return this;
    }

    /// <summary>Opts in a field and sets its attributes. Same as <c>Fields:&lt;field&gt;</c>; attributes set here win.</summary>
    public AzureSearchIndexBuilder Field(string fieldName, Action<AzureSearchFieldBuilder>? configure = null)
    {
        if (!_options.Fields.TryGetValue(fieldName, out var field))
        {
            field = new FieldOptions();
            _options.Fields[fieldName] = field;
        }

        configure?.Invoke(new AzureSearchFieldBuilder(field));
        return this;
    }

    /// <summary>
    /// Changes the Azure index definition after the package builds it: custom analyzers, scoring profiles, extra fields.
    /// Runs for every Azure index of the alias, at creation and at startup. The package does not validate the changes. Code only.
    /// </summary>
    public AzureSearchIndexBuilder ConfigureIndex(Action<SearchIndex> configure)
        => ConfigureIndex((searchIndex, _) => configure(searchIndex));

    /// <summary>
    /// Changes the Azure index definition after the package builds it. The context names the Azure index, its culture
    /// and whether it is the fallback index. The package does not validate the changes. Code only.
    /// </summary>
    public AzureSearchIndexBuilder ConfigureIndex(Action<SearchIndex, AzureSearchIndexContext> configure)
    {
        _options.IndexConfigurations = [.. _options.IndexConfigurations, configure];
        return this;
    }

    /// <summary>
    /// Changes each Azure query after the package sets its own search options. Add to <c>Filter</c> and <c>SearchFields</c>;
    /// do not replace them, or culture and access filtering breaks. The package does not validate the changes. Code only.
    /// </summary>
    public AzureSearchIndexBuilder ConfigureSearch(Action<SearchOptions, AzureSearchRequest> configure)
    {
        _options.SearchConfigurations = [.. _options.SearchConfigurations, configure];
        return this;
    }

    /// <summary>Holds only documents of these content types (document, media or member type aliases). Code only.</summary>
    public AzureSearchIndexBuilder ContentTypes(params string[] contentTypeAliases)
    {
        _options.ContentTypes = contentTypeAliases;
        return this;
    }

    /// <summary>
    /// Holds only these cultures, plus invariant documents. A key matches the culture or its language part,
    /// so <c>pt</c> holds <c>pt-BR</c> and <c>pt-PT</c>. Code only.
    /// </summary>
    public AzureSearchIndexBuilder Cultures(params string[] cultures)
    {
        _options.Cultures = cultures;
        return this;
    }

    /// <summary>
    /// Runs an <see cref="IContentIndexer"/> for this index only. Do not also register it as <see cref="IContentIndexer"/>,
    /// or it runs for every index. The package resolves it from the container, or creates it with its dependencies. Code only.
    /// </summary>
    public AzureSearchIndexBuilder Indexer<TIndexer>() where TIndexer : class, IContentIndexer
    {
        if (!_options.Indexers.Contains(typeof(TIndexer)))
        {
            _options.Indexers = [.. _options.Indexers, typeof(TIndexer)];
        }

        return this;
    }
}

/// <summary>Sets the attributes of one opted-in field.</summary>
public sealed class AzureSearchFieldBuilder
{
    private readonly FieldOptions _field;

    internal AzureSearchFieldBuilder(FieldOptions field) => _field = field;

    /// <summary>The texts of the field are searchable, and feed the free-text query on all fields.</summary>
    public AzureSearchFieldBuilder Searchable(bool value = true) => Set(() => _field.Searchable = value);

    /// <summary>The values of the field can be used in filters.</summary>
    public AzureSearchFieldBuilder Filterable(bool value = true) => Set(() => _field.Filterable = value);

    /// <summary>The values of the field can be used in facets.</summary>
    public AzureSearchFieldBuilder Facetable(bool value = true) => Set(() => _field.Facetable = value);

    /// <summary>The field can be used to sort results.</summary>
    public AzureSearchFieldBuilder Sortable(bool value = true) => Set(() => _field.Sortable = value);

    /// <summary>The texts of the field feed suggestions. Needs <see cref="Searchable"/>.</summary>
    public AzureSearchFieldBuilder Suggestable(bool value = true) => Set(() => _field.Suggestable = value);

    /// <summary>The Azure analyzer of the searchable texts, e.g. <c>en.microsoft</c>.</summary>
    public AzureSearchFieldBuilder Analyzer(string analyzerName) => Set(() => _field.Analyzer = analyzerName);

    /// <summary>
    /// Gives the searchable texts one copy per mapped culture with the culture analyzer, plus an invariant copy.
    /// Has an effect only with the <c>FieldPerCulture</c> analyzer mode.
    /// </summary>
    public AzureSearchFieldBuilder PerCulture(bool value = true) => Set(() => _field.PerCulture = value);

    /// <summary>The relevance boost of the field, in the generated umbraco-boosts scoring profile. Greater than 0.</summary>
    public AzureSearchFieldBuilder Boost(double boost) => Set(() => _field.Boost = boost);

    private AzureSearchFieldBuilder Set(Action set)
    {
        set();
        return this;
    }
}

/// <summary>Sets the zero-downtime rebuild of one index.</summary>
public sealed class AzureSearchRebuildBuilder
{
    private readonly AzureSearchIndexOptions _options;

    internal AzureSearchRebuildBuilder(AzureSearchIndexOptions options) => _options = options;

    /// <summary>Skips the swap when the new index has fewer than this percentage of the old index's documents. 0 turns it off.</summary>
    public AzureSearchRebuildBuilder MinimumDocumentPercent(int percent)
    {
        _options.RebuildMinimumDocumentPercent = percent;
        return this;
    }

    /// <summary>The wait between the swap and the delete of the old index.</summary>
    public AzureSearchRebuildBuilder DeleteDelay(TimeSpan delay)
    {
        _options.RebuildDeleteDelaySeconds = (int)delay.TotalSeconds;
        return this;
    }
}

/// <summary>Sets the spell check of one index.</summary>
public sealed class AzureSearchSpellCheckBuilder
{
    private readonly SpellCheckOptions _spellCheck;

    internal AzureSearchSpellCheckBuilder(SpellCheckOptions spellCheck) => _spellCheck = spellCheck;

    /// <summary>
    /// Corrects queries in a culture (for example <c>en</c> or <c>pt-BR</c>) with the lexicon speller of a query language.
    /// Same as <c>SpellCheck:Cultures:&lt;culture&gt;</c>.
    /// </summary>
    public AzureSearchSpellCheckBuilder Culture(string culture, QueryLanguage queryLanguage)
    {
        _spellCheck.Cultures[culture] = queryLanguage.ToString();
        return this;
    }
}

/// <summary>Sets the analyzers of one index.</summary>
public sealed class AzureSearchAnalyzerBuilder
{
    private readonly AnalyzerOptions _analyzers;

    internal AzureSearchAnalyzerBuilder(AnalyzerOptions analyzers) => _analyzers = analyzers;

    /// <summary>Shared text fields with <c>standard.lucene</c>, as before. Same as <c>Mode: None</c>.</summary>
    public AzureSearchAnalyzerBuilder None() => Set(() => _analyzers.Mode = AnalyzerMode.None);

    /// <summary>One copy of the text fields per mapped culture, plus an invariant copy. Same as <c>Mode: FieldPerCulture</c>.</summary>
    public AzureSearchAnalyzerBuilder FieldPerCulture() => Set(() => _analyzers.Mode = AnalyzerMode.FieldPerCulture);

    /// <summary>
    /// One physical index per mapped culture, plus a fallback index for languages outside the map.
    /// Same as <c>Mode: IndexPerCulture</c>.
    /// </summary>
    public AzureSearchAnalyzerBuilder IndexPerCulture() => Set(() => _analyzers.Mode = AnalyzerMode.IndexPerCulture);

    /// <summary>Maps a culture key (for example <c>fr</c> or <c>pt-BR</c>) to an analyzer. Same as <c>Cultures:&lt;key&gt;</c>.</summary>
    public AzureSearchAnalyzerBuilder Culture(string culture, LexicalAnalyzerName analyzer)
        => Set(() => _analyzers.Cultures[culture] = analyzer.ToString());

    /// <summary>The analyzer of the invariant copy. Same as <c>Invariant</c>.</summary>
    public AzureSearchAnalyzerBuilder Invariant(LexicalAnalyzerName analyzer) => Set(() => _analyzers.Invariant = analyzer.ToString());

    /// <summary>One shared suggestion field with this analyzer, instead of copies per culture. Same as <c>SuggestionAnalyzer</c>.</summary>
    public AzureSearchAnalyzerBuilder SuggestionAnalyzer(LexicalAnalyzerName analyzer)
        => Set(() => _analyzers.SuggestionAnalyzer = analyzer.ToString());

    /// <summary>Suggestion copies that follow the culture analyzers (the default). Same as <c>SuggestionAnalyzer: PerCulture</c>.</summary>
    public AzureSearchAnalyzerBuilder SuggestionAnalyzerPerCulture()
        => Set(() => _analyzers.SuggestionAnalyzer = AnalyzerOptions.PerCulture);

    private AzureSearchAnalyzerBuilder Set(Action set)
    {
        set();
        return this;
    }
}
