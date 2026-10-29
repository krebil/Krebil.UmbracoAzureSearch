using Azure.Search.Documents.Indexes.Models;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>The attributes a field gets in the index.</summary>
internal sealed record ResolvedField(
    bool Searchable,
    bool Filterable,
    bool Facetable,
    bool Sortable,
    bool Suggestable,
    string? Analyzer,
    bool PerCulture = false);

/// <summary>
/// Applies the index definition (<see cref="AzureSearchIndexOptions"/>) to fields and cultures. The indexer applies it on
/// every path, so documents and the fields they add to the schema follow the same rules.
/// </summary>
internal sealed class IndexDefinition(
    AzureSearchIndexOptions options,
    string? textAnalyzer = null,
    IReadOnlyList<ScoringProfile>? providerProfiles = null)
{
    /// <summary>The profile the package generates from field boosts.</summary>
    public const string BoostsProfileName = "umbraco-boosts";

    /// <summary>The scoring profiles of the scoring profile providers for this index.</summary>
    public IReadOnlyList<ScoringProfile> ProviderProfiles { get; } = providerProfiles ?? [];

    /// <summary>The definition with the profiles of the scoring profile providers.</summary>
    public IndexDefinition WithProviderProfiles(IReadOnlyList<ScoringProfile> profiles) => new(Options, TextAnalyzer, profiles);

    /// <summary>True when an opted-in field has a boost, so the index gets the umbraco-boosts profile.</summary>
    public bool HasBoosts => Options.Fields.Values.Any(f => f.Boost is not null);

    /// <summary>The scoring profile a query uses when it names none.</summary>
    public string DefaultScoringProfile
        => Options.DefaultScoringProfile ?? (HasBoosts ? BoostsProfileName : "relevanceBoost");

    // An automatic field keeps the attributes the package always gave it: searchable texts, filterable and facetable values, sortable.
    private static readonly ResolvedField Automatic = new(true, true, true, true, false, null);

    /// <summary>The suffix of the invariant copies, as in <c>allTexts_inv</c>.</summary>
    public const string InvariantSuffix = "inv";

    public AzureSearchIndexOptions Options { get; } = options;

    /// <summary>The attributes of a field, or null when the field is not in the index.</summary>
    public ResolvedField? Resolve(string fieldName)
    {
        Options.Fields.TryGetValue(fieldName, out var field);

        // System fields are part of the Umbraco Search contract: deletes, the backoffice and the Delivery API filter on them.
        var automatic = Options.AutomaticFields || IsSystemField(fieldName);
        if (field is null)
            return automatic ? Automatic with { Analyzer = TextAnalyzer } : null;

        bool Attribute(bool? value, bool automaticValue) => value ?? (automatic && automaticValue);
        return new ResolvedField(
            Attribute(field.Searchable, Automatic.Searchable),
            Attribute(field.Filterable, Automatic.Filterable),
            Attribute(field.Facetable, Automatic.Facetable),
            Attribute(field.Sortable, Automatic.Sortable),
            field.Suggestable ?? false,
            field.Analyzer ?? TextAnalyzer,
            field.PerCulture ?? false);
    }

    /// <summary>
    /// The text analyzer of the Azure index this definition writes to: the culture analyzer of an IndexPerCulture index.
    /// Null uses <c>standard.lucene</c>. It applies to the aggregate text fields, the suggestion field and fields without an analyzer.
    /// </summary>
    public string? TextAnalyzer { get; } = textAnalyzer;

    /// <summary>The definition for one Azure index of the alias, with its text analyzer.</summary>
    public IndexDefinition For(string? textAnalyzer) => new(Options, textAnalyzer, ProviderProfiles);

    /// <summary>True when the alias has one Azure index per culture.</summary>
    public bool IndexPerCulture => Options.Analyzers.Mode == AnalyzerMode.IndexPerCulture;

    /// <summary>True when the index keeps copies of its text fields per culture.</summary>
    public bool FieldPerCulture => Options.Analyzers.Mode == AnalyzerMode.FieldPerCulture;

    /// <summary>True when the suggestion field has copies per culture.</summary>
    public bool SuggestionsPerCulture
        => FieldPerCulture && string.Equals(Options.Analyzers.SuggestionAnalyzer, AnalyzerOptions.PerCulture, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The copy suffix of a culture: its mapped key, in lowercase with "-" as "_", or null for an unmapped culture,
    /// for invariant (null), and outside <see cref="AnalyzerMode.FieldPerCulture"/>.
    /// </summary>
    public string? CultureSuffix(string? culture)
        => FieldPerCulture && culture is not null && MatchKey(Options.Analyzers.Cultures.Keys, culture) is { } key
            ? Suffix(key)
            : null;

    /// <summary>The copy suffix and analyzer of every mapped culture, then the invariant copy.</summary>
    public IEnumerable<(string Suffix, string Analyzer)> Copies()
    {
        if (!FieldPerCulture)
            yield break;

        foreach (var (key, analyzer) in Options.Analyzers.Cultures)
        {
            yield return (Suffix(key), analyzer);
        }

        yield return (InvariantSuffix, Options.Analyzers.Invariant);
    }

    /// <summary>The query language of the speller for a culture, or null when the culture has no spell check.</summary>
    public string? QueryLanguage(string? culture)
        => culture is not null && MatchKey(Options.SpellCheck.Cultures.Keys, culture) is { } key
            ? Options.SpellCheck.Cultures[key]
            : null;

    /// <summary>The analyzer of the copies with this suffix.</summary>
    public string? CopyAnalyzer(string suffix)
        => Copies().Where(c => c.Suffix == suffix).Select(c => c.Analyzer).FirstOrDefault();

    /// <summary>The name of a per-culture copy: the field name, "_", and the suffix.</summary>
    public static string CopyName(string fieldName, string suffix) => $"{fieldName}_{suffix}";

    private static string Suffix(string key) => key.ToLowerInvariant().Replace('-', '_');

    // An exact key wins over a key for the language part, so "pt-BR" wins over "pt" for pt-BR, and "pt" covers pt-PT.
    internal static string? MatchKey(IEnumerable<string> keys, string culture)
    {
        var keyList = keys as IReadOnlyCollection<string> ?? keys.ToArray();
        var language = culture.Split('-')[0];
        return keyList.FirstOrDefault(key => string.Equals(key, culture, StringComparison.OrdinalIgnoreCase))
               ?? keyList.FirstOrDefault(key => string.Equals(key, language, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSystemField(string fieldName)
        => fieldName.StartsWith("Umb_", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the index holds this culture. Invariant (null) is always held.</summary>
    public bool HoldsCulture(string? culture)
    {
        if (culture is null || Options.Cultures is not { Count: > 0 } cultures)
            return true;

        return MatchKey(cultures, culture) is not null;
    }
}
