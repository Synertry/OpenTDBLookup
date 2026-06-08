using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTDBLookup.Services;
using RichardSzalay.MockHttp;

namespace OpenTDBLookup.Tests;

public sealed class OpenTdbClientTests
{
    private const string BaseUrl = "https://opentdb.com/";

    private static string ToB64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static OpenTdbClient ClientFor(MockHttpMessageHandler handler, TimeSpan? minimumInterval = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        return new OpenTdbClient(http, NullLogger<OpenTdbClient>.Instance, minimumInterval ?? TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task FetchQuestionsAsync_decodes_base64_question_and_correct_answer()
    {
        var handler = new MockHttpMessageHandler();
        var question = "What is 7 x 8?";
        var correct = "56";
        var wrong1 = "54";
        var wrong2 = "63";
        var wrong3 = "72";
        handler.When(BaseUrl + "api.php*")
            .Respond("application/json", $$"""
                {
                  "response_code": 0,
                  "results": [{
                    "category": "{{ToB64("Math")}}",
                    "type": "{{ToB64("multiple")}}",
                    "difficulty": "{{ToB64("easy")}}",
                    "question": "{{ToB64(question)}}",
                    "correct_answer": "{{ToB64(correct)}}",
                    "incorrect_answers": ["{{ToB64(wrong1)}}", "{{ToB64(wrong2)}}", "{{ToB64(wrong3)}}"]
                  }]
                }
                """);

        var client = ClientFor(handler);

        var (code, results) = await client.FetchQuestionsAsync(1, null, null, null, CancellationToken.None);

        code.Should().Be(0);
        results.Should().HaveCount(1);
        results[0].Question.Should().Be(question);
        results[0].CorrectAnswer.Should().Be(correct);
        results[0].IncorrectAnswers.Should().BeEquivalentTo(new[] { wrong1, wrong2, wrong3 });
    }

    [Fact]
    public async Task FetchQuestionsAsync_passes_through_response_code_1_with_empty_results()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(BaseUrl + "api.php*").Respond("application/json", """{"response_code":1,"results":[]}""");

        var client = ClientFor(handler);

        var (code, results) = await client.FetchQuestionsAsync(1, null, null, null, CancellationToken.None);

        code.Should().Be(1);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCategoriesAsync_retries_on_500_then_succeeds()
    {
        var handler = new MockHttpMessageHandler();
        var seq = handler.When(BaseUrl + "api_category.php");
        seq.Respond(HttpStatusCode.InternalServerError);
        seq.Respond("application/json", """
            {"trivia_categories":[{"id":9,"name":"General Knowledge"},{"id":10,"name":"Books"}]}
            """);

        var client = ClientFor(handler);

        var categories = await client.GetCategoriesAsync(CancellationToken.None);

        categories.Should().HaveCount(2);
        categories.Should().Contain(c => c.Id == 9 && c.Name == "General Knowledge");
    }

    [Fact]
    public async Task EnforcesMinimumInterval_between_consecutive_requests()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(BaseUrl + "api_category.php")
            .Respond("application/json", """{"trivia_categories":[{"id":1,"name":"X"}]}""");

        // Use a longer interval and assert against half of it so the test is
        // robust on slow CI runners; the gate fires on a Stopwatch whose
        // baseline is taken before the first request returns, so any interval
        // shorter than ~100ms is hard to assert against without flakiness.
        const int IntervalMs = 400;
        var client = ClientFor(handler, TimeSpan.FromMilliseconds(IntervalMs));

        await client.GetCategoriesAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await client.GetCategoriesAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(IntervalMs / 2),
            "the second request must wait for at least half of the configured interval since the first");
    }

    [Fact]
    public async Task GetVerifiedCountsAsync_maps_per_category_verified_counts()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(BaseUrl + "api_count_global.php")
            .Respond("application/json", """
                {
                  "overall": {"total_num_of_questions":1000,"total_num_of_pending_questions":10,"total_num_of_verified_questions":900,"total_num_of_rejected_questions":90},
                  "categories": {
                    "9":  {"total_num_of_questions":300,"total_num_of_pending_questions":3,"total_num_of_verified_questions":280,"total_num_of_rejected_questions":17},
                    "10": {"total_num_of_questions":200,"total_num_of_pending_questions":2,"total_num_of_verified_questions":190,"total_num_of_rejected_questions":8}
                  }
                }
                """);

        var client = ClientFor(handler);

        var counts = await client.GetVerifiedCountsAsync(CancellationToken.None);

        counts[9].Should().Be(280);
        counts[10].Should().Be(190);
    }

    [Fact]
    public async Task GetCategoryDifficultyTotalsAsync_returns_per_difficulty_counts()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(BaseUrl + "api_count.php*")
            .Respond("application/json", """
                {
                  "category_id": 13,
                  "category_question_count": {
                    "total_question_count": 35,
                    "total_easy_question_count": 10,
                    "total_medium_question_count": 14,
                    "total_hard_question_count": 11
                  }
                }
                """);

        var client = ClientFor(handler);

        var totals = await client.GetCategoryDifficultyTotalsAsync(13, CancellationToken.None);

        totals.Easy.Should().Be(10);
        totals.Medium.Should().Be(14);
        totals.Hard.Should().Be(11);
        totals.Total.Should().Be(35);
    }
}
