using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>The attributes a field gets in the index.</summary>
internal sealed record ResolvedField(
    bool Searchable,
    bool Filterable,
    bool Facetable,
    bool Sortable,
    bool Suggestable,
    string? Analyzer);

/// <summary>
/// Applies the index definition (<see cref="AzureSearchIndexOptions"/>) to fields and cultures. The indexer applies it on
/// every path, so documents and the fields they add to the schema follow the same rules.
/// </summary>
internal sealed class IndexDefinition(AzureSearchIndexOptions options)
{
    // An automatic field keeps the attributes Krebil always gave it: searchable texts, filterable and facetable values, sortable.
    private static readonly ResolvedField Automatic = new(true, true, true, true, false, null);

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
            field.Analyzer);
    }

    public static bool IsSystemField(string fieldName)
        => fieldName.StartsWith("Umb_", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the index holds this culture. Invariant (null) is always held.</summary>
    public bool HoldsCulture(string? culture)
    {
        if (culture is null || Options.Cultures is not { Count: > 0 } cultures)
            return true;

        var language = culture.Split('-')[0];
        return cultures.Any(key =>
            string.Equals(key, culture, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, language, StringComparison.OrdinalIgnoreCase));
    }
}
