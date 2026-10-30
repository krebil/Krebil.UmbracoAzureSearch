using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Constants;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>
/// Sends document actions to Azure. A service that is short of resources answers 503 or 429, for the whole request or for
/// single documents. The Azure SDK retries a throttled request 3 times within a few seconds; after that, the request or
/// the throttled documents are sent again after 2, 4, 8, 16 and 32 seconds. Then the last answer counts.
/// </summary>
internal sealed class DocumentWriter(ILogger<DocumentWriter> logger)
{
    /// <summary>The attempts of one send: the first, and the retries after throttling.</summary>
    public const int Attempts = 6;

    /// <summary>The wait before retry n (1 based): 2, 4, 8, 16 and 32 seconds.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = retry => TimeSpan.FromSeconds(2 * Math.Pow(2, retry - 1));

    public static bool IsThrottled(int status) => status is 429 or 503;

    /// <summary>
    /// Sends the actions and returns a result per document. Per-document failures other than throttling are in the results;
    /// a request that Azure rejects throws.
    /// </summary>
    public async Task<IReadOnlyList<IndexingResult>> SendAsync(
        SearchClient searchClient, IReadOnlyList<IndexDocumentsAction<SearchDocument>> actions)
    {
        var results = new List<IndexingResult>();
        var pending = actions;
        for (var attempt = 1; ; attempt++)
        {
            string reason;
            try
            {
                var response = await searchClient.IndexDocumentsAsync(
                    IndexDocumentsBatch.Create(pending.ToArray()), new IndexDocumentsOptions { ThrowOnAnyError = false });
                var throttled = response.Value.Results.Where(r => IsThrottled(r.Status) && attempt < Attempts).Select(r => r.Key).ToHashSet();
                results.AddRange(response.Value.Results.Where(r => !throttled.Contains(r.Key)));
                if (throttled.Count == 0)
                    return results;

                pending = pending.Where(a => throttled.Contains(Key(a))).ToArray();
                reason = $"{throttled.Count} document(s) throttled";
            }
            catch (RequestFailedException ex) when (IsThrottled(ex.Status) && attempt < Attempts)
            {
                reason = $"request throttled ({ex.Status})";
            }

            var delay = RetryDelay(attempt);
            logger.LogWarning(
                "Azure AI Search throttled a write to {IndexName}: {Reason}. Retry {Retry} of {Retries} in {Delay}",
                searchClient.IndexName, reason, attempt, Attempts - 1, delay);
            await Task.Delay(delay);
        }
    }

    private static string? Key(IndexDocumentsAction<SearchDocument> action)
        => action.Document.TryGetValue(IndexConstants.FieldNames.Id, out var id) ? id?.ToString() : null;
}
