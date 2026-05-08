using System;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Default <see cref="IQuestionMatcher"/>: hashes the normalized input and
/// looks it up against <see cref="IQuestionRepository.ByHash"/>; on miss,
/// falls back to a longest-substring scan of the cached questions.
/// </summary>
public sealed class QuestionMatcher : IQuestionMatcher
{
    private readonly IQuestionRepository _repository;

    public QuestionMatcher(IQuestionRepository repository)
    {
        _repository = repository;
    }

    public Question? Match(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var normalized = TextNormalizer.Normalize(input);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var hash = HashHelper.ComputeHash(normalized);
        if (_repository.ByHash.TryGetValue(hash, out var exact))
        {
            return exact;
        }

        Question? best = null;
        var bestLength = -1;
        var normalizedByHash = _repository.NormalizedQuestionByHash;
        foreach (var question in _repository.All)
        {
            if (!normalizedByHash.TryGetValue(question.Hash, out var candidate) || candidate.Length == 0)
            {
                continue;
            }

            if (candidate.Length <= bestLength)
            {
                continue;
            }

            if (normalized.Contains(candidate, StringComparison.Ordinal))
            {
                best = question;
                bestLength = candidate.Length;
            }
        }

        return best;
    }

    public string? NormalizeForPreview(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : TextNormalizer.Normalize(input);
}
