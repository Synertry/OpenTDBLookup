using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;
using OpenTDBLookup.Services;

namespace OpenTDBLookup.Tests;

public sealed class QuestionMatcherTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        // Clean up the per-test temp JSON files we asked the repository to
        // write. SaveAsync is never called by these tests, but the
        // repository would create the path the moment it did - keeping the
        // dispose for symmetry and future-proofing.
        foreach (var path in _tempPaths)
        {
            try { if (File.Exists(path)) { File.Delete(path); } }
            catch (IOException) { /* best-effort */ }
        }
    }

    private (QuestionRepository repo, QuestionMatcher matcher) BuildRepoWith(params (string text, string answer)[] questions)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qmt-{Guid.NewGuid():N}.json");
        _tempPaths.Add(path);
        var repo = new QuestionRepository(NullLogger<QuestionRepository>.Instance, path);
        var batch = new List<Question>();
        foreach (var (text, answer) in questions)
        {
            var hash = HashHelper.ComputeHash(TextNormalizer.Normalize(text));
            batch.Add(new Question(hash, 1, "General", "easy", "multiple", text, answer, []));
        }
        repo.Merge(batch);
        return (repo, new QuestionMatcher(repo));
    }

    [Fact]
    public void Match_returns_question_on_exact_normalized_match()
    {
        var (_, matcher) = BuildRepoWith(("What is the capital of France?", "Paris"));

        matcher.Match("What is the capital of France?")
            .Should().NotBeNull()
            .And.Subject.As<Question>().CorrectAnswer.Should().Be("Paris");
    }

    [Fact]
    public void Match_ignores_leading_and_trailing_whitespace_drift()
    {
        var (_, matcher) = BuildRepoWith(("What is 2 + 2?", "4"));

        matcher.Match("   What is 2 + 2?\n\n  ")!.CorrectAnswer.Should().Be("4");
    }

    [Fact]
    public void Match_handles_html_entity_drift_between_input_and_store()
    {
        var (_, matcher) = BuildRepoWith(("What's the deal?", "Easy"));

        matcher.Match("What&#039;s the deal?")!.CorrectAnswer.Should().Be("Easy");
    }

    [Fact]
    public void Match_falls_back_to_substring_when_input_has_extra_choices()
    {
        var (_, matcher) = BuildRepoWith(("Which planet is the largest in our solar system?", "Jupiter"));

        var input = "Which planet is the largest in our solar system?\nA) Earth\nB) Jupiter\nC) Mars\nD) Saturn";
        matcher.Match(input)!.CorrectAnswer.Should().Be("Jupiter");
    }

    [Fact]
    public void Match_returns_longest_substring_match_when_multiple_questions_appear_inside_input()
    {
        var (_, matcher) = BuildRepoWith(
            ("the capital of france", "Paris"),
            ("what is the capital of france", "Paris (long)"));

        matcher.Match("What is the capital of France?")!.CorrectAnswer.Should().Be("Paris (long)");
    }

    [Fact]
    public void Match_returns_null_when_no_question_matches()
    {
        var (_, matcher) = BuildRepoWith(("Random irrelevant question", "x"));

        matcher.Match("Something completely different that doesn't match").Should().BeNull();
    }

    [Fact]
    public void Match_returns_null_for_empty_input()
    {
        var (_, matcher) = BuildRepoWith(("Random question", "x"));

        matcher.Match(string.Empty).Should().BeNull();
        matcher.Match(null).Should().BeNull();
    }

    [Fact]
    public void Match_returns_null_for_whitespace_only_input()
    {
        var (_, matcher) = BuildRepoWith(("Random question", "x"));

        matcher.Match("   \t\n").Should().BeNull();
    }

    [Fact]
    public void Match_is_case_insensitive_via_normalization()
    {
        var (_, matcher) = BuildRepoWith(("What is the SQUARE ROOT of 81?", "9"));

        matcher.Match("what is the square root of 81?")!.CorrectAnswer.Should().Be("9");
    }
}
