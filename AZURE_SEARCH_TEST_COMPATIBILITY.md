# Azure Search Test Compatibility Report

## Summary

- **Total Tests:** 199
- **Passing:** 166 (83%)
- **Failing:** 33 (17%)
- **Original Failures:** 142
- **Tests Fixed:** 109 (77% of original failures)

## Remaining Test Failures

The 33 remaining test failures fall into three categories:

### Category 1: Document Ordering (28 tests)

**Issue:** Tests expect documents returned in a specific order (matching insertion order 1, 2, 3...) when no explicit sorting is provided. Azure Search returns documents in relevance score order, which is undefined for filter-only queries where all documents have equal relevance.

**Root Cause:** The tests use `Is.EqualTo(...).AsCollection` which requires exact ordering. Without a dedicated sort field that captures insertion order, Azure Search cannot guarantee the expected ordering.

**Affected Tests:**
- `CanFilterAllDocumentsByKeyword`
- `CanFilterAllDocumentsByWildcardText`
- `CanFilterDocumentsByKeywordNegated`
- `CanFilterDocumentsByIntegerRangeNegated`
- `CanFilterDocumentsByDecimalRangeNegated`
- `CanFilterDocumentsByDateTimeOffsetExactNegated`
- `CanFilterDocumentsByDateTimeOffsetRangeNegated`
- `CanFilterDocumentsBySpecificTextNegated`
- `CanFilterDocumentsBySpecificTextR1Negated`
- `CanFilterDocumentsBySpecificTextR2Negated`
- `CanFilterDocumentsBySpecificTextR3Negated`
- `CanFilterDocumentsByCommonTextNegated(True)`
- `CanFilterDocumentsByCommonTextNegated(False)`
- `CanFilterMultipleDocumentsByKeyword(True)`
- `CanFilterMultipleDocumentsByKeyword(False)`
- `CanFilterMultipleDocumentsByCommonText(True)`
- `CanFilterMultipleDocumentsByCommonText(False)`
- `CanFilterMultipleDocumentsByIntegerExact`
- `CanFilterMultipleDocumentsByDecimalExact`
- `CanFilterMultipleDocumentsByDateTimeOffsetExact`
- `CanFilterMultipleDocumentsBySpecificText`
- `CanFilterMultipleDocumentsBySpecificTextR1`
- `CanFilterMultipleDocumentsBySpecificTextR2`
- `CanFilterMultipleDocumentsBySpecificTextR3`
- `CanMixRegularAndNegatedFilters`
- `CanQueryMultipleDocuments`
- `CanQueryMultipleDocumentsByCommonWord(True)`
- `CanQueryMultipleDocumentsByCommonWord(False)`
- `CanRetrieveObjectTypes`

**Recommended Fix:** Change tests to use `Is.EquivalentTo(...)` instead of `Is.EqualTo(...).AsCollection` for unordered comparison, OR add an explicit sorter to tests that require specific ordering.

### Category 2: TextFilter Relevance Scoring (2 tests)

**Issue:** Tests expect documents filtered by `TextFilter` to be sorted by relevance based on which text field (TextsR1 > TextsR2 > TextsR3 > Texts) contains the match. However, the `TextFilter` implementation uses `search.ismatch()` on field-specific `_texts` fields, which don't benefit from the global scoring profile configured for `AllTextsR1`, `AllTextsR2`, etc.

**Root Cause:** The scoring profile boosts global `AllTexts*` fields, but `TextFilter` searches field-specific `{fieldName}_texts` fields which are not included in the scoring profile.

**Affected Tests:**
- `CanFilterAllDocumentsByWildcardTextSortedByTextualRelevanceScore(True)`
- `CanFilterAllDocumentsByWildcardTextSortedByTextualRelevanceScore(False)`

**Recommended Fix:** Either:
1. Update the scoring profile to include all field-specific text fields with appropriate boosts
2. Or modify the `TextFilter` implementation to search across global `AllTexts*` fields
3. Or accept that `TextFilter` doesn't support relevance-based sorting (only `ScoreSorter` with full-text queries)

### Category 3: Multiple Facet Types on Same Field (1 test)

**Issue:** Azure Search does not allow multiple facet specifications on the same field in a single query. The test expects both `IntegerExactFacet` and `IntegerRangeFacet` on the same field to return separate results.

**Root Cause:** Azure Search returns error "Duplicate facet specifications are specified for field" when attempting to add multiple facet expressions for the same field.

**Affected Tests:**
- `CanHaveSameTypeFacetsWithinFields`

**Recommended Fix:** Either:
1. Accept this as an Azure Search limitation and skip/modify the test
2. Or implement a complex solution that runs separate queries for each facet type on the same field and merges results

## Implementation Notes

### Features Successfully Implemented

1. **Full-text search** with prefix matching and `SearchMode.All` for AND matching
2. **All filter types:** KeywordFilter, IntegerExactFilter, IntegerRangeFilter, DecimalExactFilter, DecimalRangeFilter, DateTimeOffsetExactFilter, DateTimeOffsetRangeFilter, TextFilter
3. **All sorter types:** IntegerSorter, DecimalSorter, DateTimeOffsetSorter, KeywordSorter, TextSorter, ScoreSorter
4. **All facet types:** IntegerExactFacet, IntegerRangeFacet, DecimalExactFacet, DecimalRangeFacet, DateTimeOffsetExactFacet, DateTimeOffsetRangeFacet, KeywordFacet
5. **Scoring profile** for text relevance boosting (AllTextsR1 > AllTextsR2 > AllTextsR3 > AllTexts)
6. **Same-field filter/facet coordination** - runs separate queries to ensure facet counts reflect full dataset when filter and facet target the same field
7. **Culture and segment filtering** for multi-language/variant support
8. **Pagination** with skip/take support

### Azure Search Limitations

1. **No guaranteed ordering without explicit sort** - Unlike some databases, Azure Search doesn't maintain insertion order
2. **No multiple facets on same field** - Azure Search rejects duplicate facet specifications
3. **No per-facet filtering** - All facets in a query share the same filter (worked around with separate queries)
4. **Scoring profiles only affect searchable fields** - Filter-based queries don't use scoring profiles for relevance