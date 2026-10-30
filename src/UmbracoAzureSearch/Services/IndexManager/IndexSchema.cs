using System.ClientModel.Primitives;
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

    private static void SetProfile(SearchIndex index, ScoringProfile profile)
    {
        var existing = index.ScoringProfiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing is not null)
        {
            index.ScoringProfiles.Remove(existing);
        }

        index.ScoringProfiles.Add(profile);
    }

    /// <summary>
    /// umbraco-boosts: the relevance weights, and the boost of each opted-in field on its text fields. With FieldPerCulture,
    /// the per-culture copies of a PerCulture field get the boost too, because a culture query searches the copies.
    /// </summary>
    public static ScoringProfile BoostsProfile(IEnumerable<string> fieldNames, IndexDefinition definition)
    {
        var profile = RelevanceProfile(fieldNames);
        var weights = new Dictionary<string, double>(profile.TextWeights.Weights);
        var suffixes = definition.Copies().Select(c => c.Suffix).ToArray();
        foreach (var (fieldName, field) in definition.Options.Fields)
        {
            if (field.Boost is not { } boost || definition.Resolve(fieldName) is not { Searchable: true } resolved)
                continue;

            foreach (var postfix in TextPostfixes)
            {
                weights[$"{fieldName}{postfix}"] = boost;
                if (resolved.PerCulture)
                {
                    foreach (var suffix in suffixes)
                    {
                        weights[IndexDefinition.CopyName($"{fieldName}{postfix}", suffix)] = boost;
                    }
                }
            }
        }

        return new ScoringProfile(IndexDefinition.BoostsProfileName) { TextWeights = new TextWeights(weights) };
    }

    /// <summary>The fields a profile uses: the text weight fields, and the field of each function.</summary>
    public static IEnumerable<(string Field, ScoringFunction? Function)> ProfileFields(ScoringProfile profile)
        => (profile.TextWeights?.Weights.Keys ?? []).Select(field => (field, (ScoringFunction?)null))
            .Concat(profile.Functions.Select(function => (function.FieldName, (ScoringFunction?)function)));

    // A field the package names after an Umbraco field and a value type. Functions need it filterable.
    private static void AddProfileFields(SearchIndex index, IndexDefinition definition)
    {
        var names = index.Fields.Select(f => f.Name).ToHashSet();
        foreach (var (fieldName, function) in index.ScoringProfiles.SelectMany(ProfileFields))
        {
            if (names.Contains(fieldName) || TypedField(fieldName, definition, filterable: function is not null) is not { } field)
                continue;

            index.Fields.Add(field);
            names.Add(fieldName);
        }
    }

    /// <summary>
    /// The field definition of a field name that follows the package's naming (<c>&lt;field&gt;&lt;value type postfix&gt;</c>),
    /// with the attributes the index definition gives the Umbraco field. Null for any other name.
    /// </summary>
    public static SearchField? TypedField(string fieldName, IndexDefinition definition, bool filterable)
    {
        if (ParseField(fieldName) is not { } parsed)
            return null;

        var resolved = definition.Resolve(parsed.FieldName) ?? new ResolvedField(false, false, false, false, false, null);
        var postfix = fieldName[parsed.FieldName.Length..];
        var valueType = postfix.Replace(IndexConstants.FieldTypePostfix.Sortable, string.Empty);
        var type = valueType switch
        {
            IndexConstants.FieldTypePostfix.Integers => SearchFieldDataType.Int64,
            IndexConstants.FieldTypePostfix.Decimals => SearchFieldDataType.Double,
            IndexConstants.FieldTypePostfix.DateTimeOffsets => SearchFieldDataType.DateTimeOffset,
            _ => SearchFieldDataType.String,
        };

        return new SearchField(fieldName, parsed.Sort ? type : SearchFieldDataType.Collection(type))
        {
            IsSearchable = parsed.Text,
            IsFilterable = filterable || (!parsed.Sort && resolved.Filterable),
            IsFacetable = !parsed.Sort && !parsed.Text && resolved.Facetable,
            IsSortable = parsed.Sort,
            AnalyzerName = parsed.Text ? Analyzer(resolved.Analyzer) : null,
        };
    }

    /// <summary>
    /// The problems Azure would reject a profile for: a field the index does not have, or a field of the wrong type or
    /// without the attribute the profile needs.
    /// </summary>
    public static IEnumerable<string> ProfileProblems(ScoringProfile profile, SearchIndex index)
    {
        var fields = index.Fields.ToDictionary(f => f.Name);
        foreach (var (fieldName, function) in ProfileFields(profile))
        {
            if (!fields.TryGetValue(fieldName, out var field))
            {
                yield return $"the scoring profile {profile.Name} uses the field {fieldName}, which the index does not have";
                continue;
            }

            var type = field.Type.ToString();
            var problem = function switch
            {
                null when field.IsSearchable is not true => "a text weight needs a searchable field",
                FreshnessScoringFunction when type != SearchFieldDataType.DateTimeOffset.ToString() => "a freshness function needs a single date field (Edm.DateTimeOffset)",
                MagnitudeScoringFunction when type != SearchFieldDataType.Int32.ToString() && type != SearchFieldDataType.Int64.ToString() && type != SearchFieldDataType.Double.ToString()
                    => "a magnitude function needs a single number field (Edm.Int32, Edm.Int64 or Edm.Double)",
                DistanceScoringFunction when type != SearchFieldDataType.GeographyPoint.ToString() => "a distance function needs an Edm.GeographyPoint field",
                TagScoringFunction when type != SearchFieldDataType.String.ToString() && type != SearchFieldDataType.Collection(SearchFieldDataType.String).ToString()
                    => "a tag function needs an Edm.String or Collection(Edm.String) field",
                not null when field.IsFilterable is not true => "a scoring function needs a filterable field",
                _ => null,
            };
            if (problem is not null)
            {
                yield return $"the scoring profile {profile.Name} uses the field {fieldName} ({type}), but {problem}";
            }
        }
    }

    /// <summary>
    /// Applies to a live index the parts of the index built from code that Azure changes without a rebuild: new fields,
    /// scoring profiles and the default scoring profile. Returns true when the live index changed. Other differences,
    /// such as analyzers, need a rebuild.
    /// </summary>
    public static bool ApplyLiveChanges(SearchIndex live, SearchIndex desired) => ApplyLiveChanges(live, desired, out _);

    /// <summary>
    /// As <see cref="ApplyLiveChanges(SearchIndex, SearchIndex)"/>. <paramref name="skipped"/> names the profiles left out
    /// because the live index has a field they need without the right type or attribute; a rebuild fixes those.
    /// </summary>
    public static bool ApplyLiveChanges(SearchIndex live, SearchIndex desired, out List<string> skipped)
    {
        skipped = [];
        var changed = false;
        var liveFields = live.Fields.Select(f => f.Name).ToHashSet();
        // Suggestion fields belong to the suggester, which Azure only creates with its fields.
        foreach (var field in desired.Fields.Where(f => !liveFields.Contains(f.Name) && !IsSuggestionField(f.Name)))
        {
            live.Fields.Add(field);
            changed = true;
        }

        foreach (var profile in desired.ScoringProfiles)
        {
            var existing = live.ScoringProfiles.FirstOrDefault(p => p.Name == profile.Name);
            if (existing is not null && Json(existing) == Json(profile))
                continue;

            var problems = ProfileProblems(profile, live).ToArray();
            if (problems.Length > 0)
            {
                skipped.AddRange(problems);
                continue;
            }

            if (existing is not null)
            {
                live.ScoringProfiles.Remove(existing);
            }

            live.ScoringProfiles.Add(profile);
            changed = true;
        }

        // The package's own profile goes when no field has a boost any more.
        if (desired.ScoringProfiles.All(p => p.Name != IndexDefinition.BoostsProfileName)
            && live.ScoringProfiles.FirstOrDefault(p => p.Name == IndexDefinition.BoostsProfileName) is { } boosts)
        {
            live.ScoringProfiles.Remove(boosts);
            changed = true;
        }

        var defaultProfile = live.ScoringProfiles.Any(p => p.Name == desired.DefaultScoringProfile) ? desired.DefaultScoringProfile : null;
        if (defaultProfile != live.DefaultScoringProfile && (defaultProfile is not null || live.DefaultScoringProfile == IndexDefinition.BoostsProfileName))
        {
            live.DefaultScoringProfile = defaultProfile;
            changed = true;
        }

        return changed;
    }

    /// <summary>The profiles on a live index that no source returns. The package leaves them.</summary>
    public static string[] LeftoverProfiles(SearchIndex live, SearchIndex desired)
        => live.ScoringProfiles.Select(p => p.Name).Except(desired.ScoringProfiles.Select(p => p.Name)).ToArray();

    // The value and sort postfixes of an Umbraco field, longest first so "_texts_r1" wins over "_texts".
    private static readonly (string Postfix, bool Text, bool Sort)[] FieldPostfixes =
    [
        (IndexConstants.FieldTypePostfix.TextsR1, true, false),
        (IndexConstants.FieldTypePostfix.TextsR2, true, false),
        (IndexConstants.FieldTypePostfix.TextsR3, true, false),
        ($"{IndexConstants.FieldTypePostfix.Texts}{IndexConstants.FieldTypePostfix.Sortable}", false, true),
        ($"{IndexConstants.FieldTypePostfix.Keywords}{IndexConstants.FieldTypePostfix.Sortable}", false, true),
        ($"{IndexConstants.FieldTypePostfix.Integers}{IndexConstants.FieldTypePostfix.Sortable}", false, true),
        ($"{IndexConstants.FieldTypePostfix.Decimals}{IndexConstants.FieldTypePostfix.Sortable}", false, true),
        ($"{IndexConstants.FieldTypePostfix.DateTimeOffsets}{IndexConstants.FieldTypePostfix.Sortable}", false, true),
        (IndexConstants.FieldTypePostfix.Texts, true, false),
        (IndexConstants.FieldTypePostfix.Keywords, false, false),
        (IndexConstants.FieldTypePostfix.Integers, false, false),
        (IndexConstants.FieldTypePostfix.Decimals, false, false),
        (IndexConstants.FieldTypePostfix.DateTimeOffsets, false, false),
    ];

    /// <summary>
    /// The differences between a live index and the index built from code that Azure cannot apply to a live index:
    /// analyzers of existing fields, suggester fields, attributes of existing fields, fields that automatic fields off or
    /// a field opt-in leaves out, and analyzers or other text analysis that ConfigureIndex adds.
    /// </summary>
    public static IEnumerable<string> RebuildRequired(SearchIndex live, SearchIndex desired, IndexDefinition definition)
    {
        var desiredFields = desired.Fields.ToDictionary(f => f.Name);
        foreach (var field in live.Fields)
        {
            if (desiredFields.TryGetValue(field.Name, out var wanted))
            {
                if (AnalyzerChanged(field, wanted))
                    yield return $"the analyzer of field {field.Name} is {AnalyzerOf(field)}, and the definition sets {AnalyzerOf(wanted)}";
                continue;
            }

            // A field of an Umbraco field, added as documents brought it: compare it with what the definition gives it now.
            if (ParseField(field.Name) is not { } parsed)
                continue;

            var resolved = definition.Resolve(parsed.FieldName);
            if (resolved is null)
            {
                yield return $"field {field.Name} is in the index, and the definition leaves {parsed.FieldName} out (automatic fields off, not opted in)";
                continue;
            }

            if (parsed.Sort ? !resolved.Sortable : parsed.Text ? !resolved.Searchable : !resolved.Filterable && !resolved.Facetable)
            {
                yield return $"field {field.Name} is in the index, and the definition no longer gives {parsed.FieldName} that attribute";
                continue;
            }

            if (parsed.Text && AnalyzerOf(field) != (resolved.Analyzer ?? LexicalAnalyzerName.StandardLucene.ToString()))
                yield return $"the analyzer of field {field.Name} is {AnalyzerOf(field)}, and the definition sets {resolved.Analyzer ?? "standard.lucene"}";
            else if (!parsed.Sort && !parsed.Text && (field.IsFilterable != resolved.Filterable || field.IsFacetable != resolved.Facetable))
                yield return $"field {field.Name} is filterable {field.IsFilterable} and facetable {field.IsFacetable}, and the definition sets {resolved.Filterable} and {resolved.Facetable}";
        }

        var liveSuggester = live.Suggesters.FirstOrDefault(s => s.Name == IndexConstants.SuggesterName);
        var desiredSuggester = desired.Suggesters.FirstOrDefault(s => s.Name == IndexConstants.SuggesterName);
        if (liveSuggester is not null && desiredSuggester is not null
            && !liveSuggester.SourceFields.Order().SequenceEqual(desiredSuggester.SourceFields.Order()))
        {
            yield return $"the suggester fields are {string.Join(", ", liveSuggester.SourceFields)}, and the definition sets {string.Join(", ", desiredSuggester.SourceFields)}";
        }

        foreach (var name in Missing(live.Analyzers.Select(a => a.Name), desired.Analyzers.Select(a => a.Name))
                     .Concat(Missing(live.Tokenizers.Select(t => t.Name), desired.Tokenizers.Select(t => t.Name)))
                     .Concat(Missing(live.TokenFilters.Select(t => t.Name), desired.TokenFilters.Select(t => t.Name)))
                     .Concat(Missing(live.CharFilters.Select(c => c.Name), desired.CharFilters.Select(c => c.Name))))
        {
            yield return $"the text analysis component {name} is new in the definition";
        }

        static IEnumerable<string> Missing(IEnumerable<string> live, IEnumerable<string> desired) => desired.Except(live);
    }

    private static (string FieldName, bool Text, bool Sort)? ParseField(string physicalName)
    {
        foreach (var (postfix, text, sort) in FieldPostfixes)
        {
            if (physicalName.Length > postfix.Length && physicalName.EndsWith(postfix, StringComparison.Ordinal))
                return (physicalName[..^postfix.Length], text, sort);
        }

        return null;
    }

    private static string AnalyzerOf(SearchField field)
        => field.IsSearchable is true ? field.AnalyzerName?.ToString() ?? LexicalAnalyzerName.StandardLucene.ToString() : "none";

    // Only the analyzer of a field the definition makes searchable matters. Azure makes a string field searchable when
    // the request leaves "searchable" out, so the key and filter fields of older indexes have standard.lucene: no change.
    private static bool AnalyzerChanged(SearchField live, SearchField desired)
    {
        if (desired.IsSearchable is not true)
            return false;

        var liveAnalyzer = AnalyzerOf(live);
        var desiredAnalyzer = AnalyzerOf(desired);
        return liveAnalyzer != desiredAnalyzer
               && !(liveAnalyzer == "none" && desiredAnalyzer == LexicalAnalyzerName.StandardLucene.ToString());
    }

    private static bool IsSuggestionField(string name)
        => name == IndexConstants.FieldNames.Suggestion || name.StartsWith($"{IndexConstants.FieldNames.Suggestion}_", StringComparison.Ordinal);

    private static string Json<T>(T model) where T : IPersistableModel<T> => ModelReaderWriter.Write(model).ToString();

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

    /// <summary>The field definition of a field that documents bring: the lazy path adds it when the first document has it.</summary>
    public static SearchField CreateField(IndexFieldMapping mapping)
        => new(mapping.FieldName, mapping.IsCollection ? SearchFieldDataType.Collection(mapping.FieldType) : mapping.FieldType)
        {
            IsFilterable = mapping.IsFilterable ?? !mapping.IsSortable,
            IsSortable = mapping.IsSortable,
            IsFacetable = mapping.IsFacetable,
            IsSearchable = mapping.IsSearchable,
            AnalyzerName = Analyzer(mapping.AnalyzerName),
        };

    // The index definition in code; EnsureAsync, ResetAsync and the indexer create indexes from it.
    public static SearchIndex Build(PhysicalIndex physicalIndex, string indexAlias, IndexDefinition definition, bool runCallbacks = true)
    {
        var indexName = physicalIndex.Name;
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

        // Scoring profiles, in source order; on a name collision the later source wins:
        // relevanceBoost (R1 > R2 > R3 > base, copies included), umbraco-boosts, the providers, then ConfigureIndex.
        newIndex.ScoringProfiles.Add(RelevanceProfile(newIndex.Fields.Select(f => f.Name)));
        if (definition.HasBoosts)
        {
            SetProfile(newIndex, BoostsProfile(newIndex.Fields.Select(f => f.Name), definition));
        }

        foreach (var profile in definition.ProviderProfiles)
        {
            SetProfile(newIndex, profile);
        }

        newIndex.DefaultScoringProfile = definition.DefaultScoringProfile;

        // The consumer's changes run last, on every Azure index of the alias. The package does not validate them.
        var context = new AzureSearchIndexContext(indexAlias, indexName, physicalIndex.Culture, physicalIndex.IsFallback);
        foreach (var configure in runCallbacks ? definition.Options.IndexConfigurations : [])
        {
            configure(newIndex, context);
        }

        // Fields that documents add later, but that a profile needs now. The name says the type.
        AddProfileFields(newIndex, definition);
        return newIndex;
    }
}
