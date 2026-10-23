using System.Reflection;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Options;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// Checks the definition of one index. <see cref="AzureSearchIndexStartupCheck"/> runs it for every configured index at startup.
/// </summary>
internal sealed class AzureSearchIndexOptionsValidator(IOptions<UmbracoAzureSearchOptions> azureSearchOptions)
    : IValidateOptions<AzureSearchIndexOptions>
{
    // The fields every index has: id, key, objectType, culture, segment, accessKeys and the four allTexts fields.
    internal const int BaseFieldCount = 10;

    // The Umbraco system fields: ten keyword, number or date fields with a sortable copy, and Umb_Name with four text fields and a sortable copy.
    internal const int SystemFieldCount = 25;

    private static readonly HashSet<string> KnownAnalyzers = typeof(LexicalAnalyzerName)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(LexicalAnalyzerName))
        .Select(p => p.GetValue(null)!.ToString()!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, AzureSearchIndexOptions options)
    {
        var failures = new List<string>();
        var definition = new IndexDefinition(options);

        foreach (var (fieldName, field) in options.Fields)
        {
            var resolved = definition.Resolve(fieldName)!;
            if (resolved.Suggestable && !resolved.Searchable)
            {
                failures.Add($"The field \"{fieldName}\" is suggestable but not searchable. Make it searchable, or remove Suggestable.");
            }

            if (field.Analyzer is not null && !KnownAnalyzers.Contains(field.Analyzer))
            {
                failures.Add($"The field \"{fieldName}\" uses the analyzer \"{field.Analyzer}\", which Azure AI Search does not have. "
                             + "Use a built-in analyzer name such as \"standard.lucene\" or \"en.microsoft\".");
            }
        }

        if (!options.AutomaticFields)
        {
            var fieldCount = CountFields(options, definition);
            var maxFields = azureSearchOptions.Value.MaxFieldsPerIndex;
            if (fieldCount > maxFields)
            {
                failures.Add($"The index needs {fieldCount} Azure fields, but the service allows {maxFields} "
                             + $"({UmbracoAzureSearchOptions.Name}:MaxFieldsPerIndex). Opt in fewer fields or fewer attributes.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    // A searchable field has four text fields, a filterable or facetable field one value field, and a sortable field one sort field.
    internal static int CountFields(AzureSearchIndexOptions options, IndexDefinition definition)
    {
        var count = BaseFieldCount + SystemFieldCount + (options.Suggestions.Enabled ? 1 : 0);
        foreach (var fieldName in options.Fields.Keys.Where(f => !IndexDefinition.IsSystemField(f)))
        {
            var field = definition.Resolve(fieldName)!;
            count += (field.Searchable ? 4 : 0)
                     + (field.Filterable || field.Facetable ? 1 : 0)
                     + (field.Sortable ? 1 : 0);
        }

        return count;
    }
}
