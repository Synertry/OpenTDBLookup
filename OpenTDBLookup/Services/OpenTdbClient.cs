using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// HTTP wrapper around opentdb.com. Enforces the 5-second between-request gate
/// across every endpoint and applies a small exponential-backoff retry for
/// transient HTTP failures.
/// </summary>
public sealed partial class OpenTdbClient : IOpenTdbClient
{
    private bool _disposed;
    private const string BaseUrl = "https://opentdb.com/";
    private static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(10);
    private static readonly int[] RetryDelaysMs = [1000, 2000, 4000];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenTdbClient> _logger;
    private readonly TimeSpan _minimumInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLastRequest = Stopwatch.StartNew();
    private bool _firstRequest = true;

    public OpenTdbClient(HttpClient httpClient, ILogger<OpenTdbClient> logger)
        : this(httpClient, logger, DefaultMinimumInterval)
    {
    }

    /// <summary>
    /// Test-only constructor that overrides the rate-limit interval. Production
    /// code uses the public ctor which keeps the documented 5-second gate.
    /// </summary>
    internal OpenTdbClient(HttpClient httpClient, ILogger<OpenTdbClient> logger, TimeSpan minimumInterval)
    {
        _httpClient = httpClient;
        _logger = logger;
        _minimumInterval = minimumInterval;
        _httpClient.BaseAddress ??= new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<OpenTdbCategoryItem>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<OpenTdbCategoriesResponse>("api_category.php", cancellationToken).ConfigureAwait(false);
        return response.TriviaCategories;
    }

    public async Task<IReadOnlyDictionary<int, int>> GetVerifiedCountsAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<OpenTdbCountGlobalResponse>("api_count_global.php", cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<int, int>();
        foreach (var (idText, detail) in response.Categories)
        {
            if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                result[id] = detail.TotalNumOfVerifiedQuestions;
            }
        }
        return result;
    }

    public async Task<CategoryDifficultyTotals> GetCategoryDifficultyTotalsAsync(int categoryId, CancellationToken cancellationToken)
    {
        var path = "api_count.php?category=" + categoryId.ToString(CultureInfo.InvariantCulture);
        var response = await GetJsonAsync<OpenTdbCategoryCountResponse>(path, cancellationToken).ConfigureAwait(false);
        var c = response.CategoryQuestionCount;
        return new CategoryDifficultyTotals(
            c.TotalEasyQuestionCount,
            c.TotalMediumQuestionCount,
            c.TotalHardQuestionCount);
    }

    public async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<OpenTdbTokenResponse>("api_token.php?command=request", cancellationToken).ConfigureAwait(false);
        if (response.ResponseCode != 0 || string.IsNullOrEmpty(response.Token))
        {
            throw new InvalidOperationException($"Token request failed: code={response.ResponseCode} message={response.ResponseMessage ?? "(none)"}");
        }
        return response.Token;
    }

    public async Task ResetTokenAsync(string token, CancellationToken cancellationToken)
    {
        var path = "api_token.php?command=reset&token=" + WebUtility.UrlEncode(token);
        var response = await GetJsonAsync<OpenTdbTokenResponse>(path, cancellationToken).ConfigureAwait(false);
        if (response.ResponseCode != 0)
        {
            _logger.LogWarning("Token reset returned response_code {ResponseCode}", response.ResponseCode);
        }
    }

    public Task<(int responseCode, IReadOnlyList<OpenTdbQuestionResult> results)> FetchQuestionsAsync(
        int amount,
        int? categoryId,
        string? difficulty,
        string? token,
        CancellationToken cancellationToken) =>
        FetchQuestionsCoreAsync(amount, categoryId, difficulty, token, allowRateLimitRetry: true, cancellationToken);

    private async Task<(int responseCode, IReadOnlyList<OpenTdbQuestionResult> results)> FetchQuestionsCoreAsync(
        int amount,
        int? categoryId,
        string? difficulty,
        string? token,
        bool allowRateLimitRetry,
        CancellationToken cancellationToken)
    {
        var query = new StringBuilder("api.php?amount=");
        query.Append(amount.ToString(CultureInfo.InvariantCulture));
        query.Append("&encode=base64");
        if (categoryId is { } cid)
        {
            query.Append("&category=").Append(cid.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(difficulty))
        {
            query.Append("&difficulty=").Append(WebUtility.UrlEncode(difficulty));
        }
        if (!string.IsNullOrEmpty(token))
        {
            query.Append("&token=").Append(WebUtility.UrlEncode(token));
        }

        var response = await GetJsonAsync<OpenTdbQuestionsResponse>(query.ToString(), cancellationToken).ConfigureAwait(false);

        if (response.ResponseCode == 5)
        {
            // Defensive: the 5-second gate should make this impossible, but if
            // OpenTDB ever returns code 5 we wait and retry exactly once. A
            // sustained rate-limit response will surface as code 5 to the
            // caller and the orchestration layer will log + skip the bucket.
            if (!allowRateLimitRetry)
            {
                _logger.LogWarning("OpenTDB still returning response_code 5 after backoff; surfacing to caller");
                return (5, []);
            }
            _logger.LogWarning("OpenTDB returned response_code 5 (rate limit) - waiting {Backoff}s and retrying once", RateLimitBackoff.TotalSeconds);
            await Task.Delay(RateLimitBackoff, cancellationToken).ConfigureAwait(false);
            return await FetchQuestionsCoreAsync(amount, categoryId, difficulty, token, allowRateLimitRetry: false, cancellationToken).ConfigureAwait(false);
        }

        var decoded = new List<OpenTdbQuestionResult>(response.Results.Count);
        foreach (var raw in response.Results)
        {
            decoded.Add(DecodeQuestion(raw));
        }
        return (response.ResponseCode, decoded);
    }

    private static OpenTdbQuestionResult DecodeQuestion(OpenTdbQuestionResult raw)
    {
        var incorrect = new List<string>(raw.IncorrectAnswers.Count);
        foreach (var s in raw.IncorrectAnswers)
        {
            incorrect.Add(DecodeBase64(s));
        }
        return new OpenTdbQuestionResult(
            DecodeBase64(raw.Category),
            DecodeBase64(raw.Type),
            DecodeBase64(raw.Difficulty),
            DecodeBase64(raw.Question),
            DecodeBase64(raw.CorrectAnswer),
            incorrect);
    }

    private static string DecodeBase64(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            // Defensive: if the API ever returns a non-base64 field (it should
            // not, given encode=base64), pass through verbatim rather than
            // throwing inside a deeply nested decode.
            return value;
        }
    }

    private async Task<T> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        await EnforceRateLimitAsync(cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            try
            {
                var url = relativePath;
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    throw new HttpRequestException($"Upstream {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
                }
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Empty JSON response from {relativePath}");
                _logger.LogDebug("OpenTDB GET {Path} -> {Status}", RedactToken(relativePath), (int)response.StatusCode);
                return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                lastError = ex;
                if (attempt == RetryDelaysMs.Length)
                {
                    break;
                }
                var delay = TimeSpan.FromMilliseconds(RetryDelaysMs[attempt]);
                _logger.LogWarning(ex, "OpenTDB GET {Path} failed (attempt {Attempt}/{Max}); retrying in {Delay}ms", RedactToken(relativePath), attempt + 1, RetryDelaysMs.Length, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await EnforceRateLimitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException($"OpenTDB GET {relativePath} failed after {RetryDelaysMs.Length + 1} attempts", lastError);
    }

    private static string RedactToken(string relativePath) =>
        // Session tokens identify a user's view into OpenTDB and showing them
        // in any persisted log is unnecessary leakage. Strip the value but
        // preserve the parameter so log readers see that a token was used.
        TokenQueryRegex().Replace(relativePath, "token=***");

    [System.Text.RegularExpressions.GeneratedRegex(@"token=[^&]*", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex TokenQueryRegex();

    private async Task EnforceRateLimitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_firstRequest)
            {
                _firstRequest = false;
                _sinceLastRequest.Restart();
                return;
            }

            var elapsed = _sinceLastRequest.Elapsed;
            if (elapsed < _minimumInterval)
            {
                var wait = _minimumInterval - elapsed;
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            _sinceLastRequest.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _gate.Dispose();
    }
}
