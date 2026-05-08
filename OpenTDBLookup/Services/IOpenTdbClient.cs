using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Minimal HTTP wrapper over the OpenTDB API. Implementations are responsible
/// for enforcing the documented 5-second between-request rate limit and for
/// transparent HTTP-level retries on transient failures.
/// </summary>
public interface IOpenTdbClient : IDisposable
{
    /// <summary>List every category exposed by <c>api_category.php</c>.</summary>
    Task<IReadOnlyList<OpenTdbCategoryItem>> GetCategoriesAsync(CancellationToken cancellationToken);

    /// <summary>Returns a category-id-to-verified-question-count map sourced from <c>api_count_global.php</c>.</summary>
    Task<IReadOnlyDictionary<int, int>> GetVerifiedCountsAsync(CancellationToken cancellationToken);

    /// <summary>Requests a fresh session token from <c>api_token.php</c>.</summary>
    Task<string> RequestTokenAsync(CancellationToken cancellationToken);

    /// <summary>Resets an existing session token via <c>api_token.php?command=reset</c>.</summary>
    Task ResetTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches up to <paramref name="amount"/> base64-encoded questions. The
    /// response code is returned alongside the result list so callers can
    /// react to OpenTDB-specific outcomes (token-empty, no-results, etc.).
    /// </summary>
    Task<(int responseCode, IReadOnlyList<OpenTdbQuestionResult> results)> FetchQuestionsAsync(
        int amount,
        int? categoryId,
        string? difficulty,
        string? token,
        CancellationToken cancellationToken);
}
