using Azure.Search.Documents.Indexes.Models;
using UmbracoAzureSearch.Constants;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Services.IndexManager;

/// <summary>
/// The index fields that follow from the index definition rather than from documents: the per-culture copies of the
/// aggregate text fields, the suggestion fields and the relevance scoring profile.
/// </summary>
internal static class IndexSchema
{
    public const string RelevanceProfileName = "relevanceBoost";

    // The aggregate text fields and their weight in the relevance profile: R1 > R2 > R3 > base.
    public static readonly (string Name, double Weight)[] Aggregates =
    [
        (IndexConstants.FieldNames.AllTextsR1, 4.0),
        (IndexConstants.FieldNames.AllTextsR2, 3.0),
        (IndexConstants.FieldNames.AllTextsR3, 2.0),
        (IndexConstants.FieldNames.AllTexts, 1.0),
    ];

    // The text fields of one Umbraco field: all levels, then R1 to R3.
    private static readonly string[] TextPostfixes =
    [
        IndexConstants.FieldTypePostfix.Texts,
        IndexConstants.FieldTypePostfix.TextsR1,
        IndexConstants.FieldTypePostfix.TextsR2,
        IndexConstants.FieldTypePostfix.TextsR3,
    ];

    /// <summary>
    /// The text fields that get per-culture copies: the aggregate text fields, and the text fields of each searchable
    /// field opted in with PerCulture. Empty outside FieldPerCulture.
    /// </summary>
    public static IEnumerable<string> CopiedFields(IndexDefinition definition)
    {
        if (!definition.FieldPerCulture)
            return [];

        var perCultureFields = definition.Options.Fields.Keys
            .Where(name => definition.Resolve(name) is { PerCulture: true, Searchable: true })
            .SelectMany(name => TextPostfixes.Select(postfix => $"{name}{postfix}"));
        return Aggregates.Select(a => a.Name).Concat(perCultureFields);
    }

    /// <summary>The copies of the copied fields for one suffix: a culture, or invariant.</summary>
    public static IEnumerable<string> CopyNames(IndexDefinition definition, string suffix)
        => CopiedFields(definition).Select(name => IndexDefinition.CopyName(name, suffix));

    /// <summary>One copy of each copied field per mapped culture and for invariant, with the copy analyzer.</summary>
    public static IEnumerable<SearchField> CopyFields(IndexDefinition definition)
    {
        var copiedFields = CopiedFields(definition).ToArray();
        return definition.Copies().SelectMany(copy => copiedFields.Select(name =>
            new SearchField(IndexDefinition.CopyName(name, copy.Suffix), SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false,
                AnalyzerName = copy.Analyzer,
            }));
    }

    /// <summary>
    /// The shared suggestion field and, with suggestion copies per culture, one copy per mapped culture and for invariant.
    /// Empty when suggestions are off.
    /// </summary>
    public static IEnumerable<SearchField> SuggestionFields(IndexDefinition definition)
    {
        if (!definition.Options.Suggestions.Enabled)
            yield break;

        // A fixed suggestion analyzer applies to the shared field in FieldPerCulture and IndexPerCulture. Otherwise the
        // shared field uses the text analyzer of the index: the culture analyzer of an IndexPerCulture index, or standard.
        var fixedAnalyzer = !string.Equals(definition.Options.Analyzers.SuggestionAnalyzer, AnalyzerOptions.PerCulture, StringComparison.OrdinalIgnoreCase);
        var sharedAnalyzer = (definition.FieldPerCulture || definition.IndexPerCulture) && fixedAnalyzer
            ? definition.Options.Analyzers.SuggestionAnalyzer
            : definition.TextAnalyzer ?? LexicalAnalyzerName.StandardLucene.ToString();
        yield return SuggestionField(IndexConstants.FieldNames.Suggestion, sharedAnalyzer);

        if (!definition.SuggestionsPerCulture)
            yield break;

        foreach (var (suffix, analyzer) in definition.Copies())
        {
            yield return SuggestionField(IndexDefinition.CopyName(IndexConstants.FieldNames.Suggestion, suffix), analyzer);
        }
    }

    /// <summary>The relevance profile over the aggregate text fields and those of their copies that the index has.</summary>
    public static ScoringProfile RelevanceProfile(IEnumerable<string> fieldNames)
    {
        var names = fieldNames.ToHashSet();
        var weights = new Dictionary<string, double>();
        foreach (var (aggregate, weight) in Aggregates)
        {
            foreach (var name in names.Where(n => n == aggregate || n.StartsWith($"{aggregate}_", StringComparison.Ordinal)))
            {
                weights[name] = weight;
            }
        }

        return new ScoringProfile(RelevanceProfileName) { TextWeights = new TextWeights(weights) };
    }

    /// <summary>
    /// Adds the copy fields the index lacks, and updates the relevance profile for them. Suggestion fields are left
    /// out: Azure adds fields to a suggester only when it creates the suggester. Returns true when the index changed.
    /// </summary>
    public static bool AddMissingCopies(SearchIndex index, IndexDefinition definition)
    {
        var existing = index.Fields.Select(f => f.Name).ToHashSet();
        var missing = CopyFields(definition).Where(f => !existing.Contains(f.Name)).ToArray();
        if (missing.Length == 0)
            return false;

        foreach (var field in missing)
        {
            index.Fields.Add(field);
        }

        // Replace only the profile this package created; other profiles are left as they are.
        var profile = index.ScoringProfiles.FirstOrDefault(p => p.Name == RelevanceProfileName);
        if (profile is not null)
        {
            index.ScoringProfiles.Remove(profile);
            index.ScoringProfiles.Add(RelevanceProfile(index.Fields.Select(f => f.Name)));
        }

        return true;
    }

    // Azure only allows a suggester on fields added in the same request, so the fields and the suggester always go together.
    public static void AddSuggestions(SearchIndex index, IndexDefinition definition)
    {
        var fields = SuggestionFields(definition).ToArray();
        foreach (var field in fields)
        {
            index.Fields.Add(field);
        }

        index.Suggesters.Add(new SearchSuggester(IndexConstants.SuggesterName, fields.Select(f => f.Name).ToArray()));
    }

    // A null name keeps the Azure default (standard.lucene); the implicit string conversion would throw on null.
    private static LexicalAnalyzerName? Analyzer(string? name) => name is null ? (LexicalAnalyzerName?)null : new LexicalAnalyzerName(name);

    private static SearchField SuggestionField(string name, string analyzer)
        => new(name, SearchFieldDataType.String)
        {
            IsSearchable = true,
            IsFilterable = false,
            IsFacetable = false,
            AnalyzerName = analyzer,
        };

    // The index definition in code; EnsureAsync, ResetAsync and the indexer create indexes from it.
    public static SearchIndex Build(string indexName, IndexDefinition definition)
    {
        var newIndex = new SearchIndex(indexName, [
            new SearchField(IndexConstants.FieldNames.Id,  SearchFieldDataType.String)
            {
                IsKey = true,
                IsSortable = true
            },
            new SearchField(IndexConstants.FieldNames.Key, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsSortable = true
            },
            new SearchField(IndexConstants.FieldNames.ObjectType, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.Culture, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.Segment, SearchFieldDataType.String)
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AccessKeys, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsFilterable = true
            },
            new SearchField(IndexConstants.FieldNames.AllTexts, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false,
                AnalyzerName = Analyzer(definition.TextAnalyzer)
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR1, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false,
                AnalyzerName = Analyzer(definition.TextAnalyzer)
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR2, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false,
                AnalyzerName = Analyzer(definition.TextAnalyzer)
            },
            new SearchField(IndexConstants.FieldNames.AllTextsR3, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable = true,
                IsFilterable = false,
                IsFacetable = false,
                AnalyzerName = Analyzer(definition.TextAnalyzer)
            }
        ]);

        foreach (var copy in CopyFields(definition))
        {
            newIndex.Fields.Add(copy);
        }

        if (definition.Options.Suggestions.Enabled)
        {
            AddSuggestions(newIndex, definition);
        }

        // Scoring profile to boost text relevance fields (R1 > R2 > R3 > base), copies included
        newIndex.ScoringProfiles.Add(RelevanceProfile(newIndex.Fields.Select(f => f.Name)));
        newIndex.DefaultScoringProfile = RelevanceProfileName;

        return newIndex;
    }
}
