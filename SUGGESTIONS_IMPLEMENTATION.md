# Suggestions Implementation

Implement the `maxSuggestions` parameter in `AzureSearchSearcher.SearchAsync` to populate `SearchResult.Suggestions` using Azure AI Search's native [Suggester / autocomplete](https://learn.microsoft.com/en-us/azure/search/index-add-suggesters) feature.

## Background

`Umbraco.Cms.Search.Core` alpha.8 added a `maxSuggestions` parameter to `ISearcher.SearchAsync` and a `Suggestions` (`IEnumerable<string>?`) property to `SearchResult`. The parameter is currently accepted but ignored.

Azure AI Search supports suggestions via `SearchClient.SuggestAsync`, but this requires:
- A `SearchSuggester` defined on the index schema
- The suggester must reference simple `Edm.String` fields — **not** `Collection(Edm.String)`

The existing `allTexts` / `allTextsR1` etc. fields are collections, so a dedicated suggestion field is needed.

## Changes Required

### 1. `Constants/IndexConstants.cs`
Add a new field name constant:
```csharp
public const string Suggestion = "suggestion";
```

### 2. `Services/IndexManager/AzureSearchIndexManager.cs`
In `EnsureAsync`, add the new field to the index schema:
```csharp
new SearchField(IndexConstants.FieldNames.Suggestion, SearchFieldDataType.String)
{
    IsSearchable = true
},
```
And register a suggester after the scoring profile:
```csharp
newIndex.Suggesters.Add(new SearchSuggester("sg", [IndexConstants.FieldNames.Suggestion]));
```

### 3. `Services/Indexer/DocumentMapper.cs`
In `MapVariationToDocument`, populate the suggestion field with joined text values:
```csharp
[IndexConstants.FieldNames.Suggestion] = allTexts.Length > 0
    ? string.Join(" ", allTexts.Take(10))
    : null,
```

### 4. `Services/Searcher/AzureSearchSearcher.cs`
In `SearchAsync`, before the return statement, call `SuggestAsync` when requested:
```csharp
IEnumerable<string>? suggestions = null;
if (maxSuggestions > 0 && !string.IsNullOrWhiteSpace(query))
{
    var suggestOptions = new SuggestOptions { Size = maxSuggestions };
    if (baseFilterClauses.Any())
        suggestOptions.Filter = string.Join(" and ", baseFilterClauses);

    var suggestResponse = await searchClient.SuggestAsync<SearchDocument>(query, "sg", suggestOptions);
    suggestions = suggestResponse.Value.Results.Select(r => r.Text).ToArray();
}
return new SearchResult(result.Value.TotalCount ?? 0, documents.ToArray(), facetResults.ToArray(), suggestions);
```

## Notes

- The `suggestion` field is only added to **newly created** indexes (i.e. when `EnsureAsync` runs on a fresh index). Existing indexes will need to be reset (`ResetAsync`) to gain the new field and suggester.
- Suggestions are only returned when `maxSuggestions > 0` **and** `query` is non-empty — Azure Search requires at least one character.
- The Elasticsearch provider also has this as `// TODO: implement suggestions`, so this is not yet implemented there either.