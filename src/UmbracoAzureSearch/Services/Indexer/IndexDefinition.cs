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
internal sealed class IndexDefinition(AzureSearchIndexOptions options)
{
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
            return automatic ? Automatic : null;

        bool Attribute(bool? value, bool automaticValue) => value ?? (automatic && automaticValue);
        return new ResolvedField(
            Attribute(field.Searchable, Automatic.Searchable),
            Attribute(field.Filterable, Automatic.Filterable),
            Attribute(field.Facetable, Automatic.Facetable),
            Attribute(field.Sortable, Automatic.Sortable),
            field.Suggestable ?? false,
            field.Analyzer,
            field.PerCulture ?? false);
    }

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
