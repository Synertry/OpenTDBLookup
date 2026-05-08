using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenTDBLookup.Models;

// ---------------------------------------------------------------------------
// All DTOs in this file map directly to the OpenTDB JSON responses. Naming
// in OpenTDB responses is snake_case; the System.Text.Json options used in
// OpenTdbClient apply JsonNamingPolicy.SnakeCaseLower, so C# property names
// stay PascalCase and serialize correctly.
// ---------------------------------------------------------------------------

/// <summary>One trivia category as returned by <c>api_category.php</c>.</summary>
public sealed record OpenTdbCategoryItem(int Id, string Name);

/// <summary>Wrapper for <c>api_category.php</c>.</summary>
public sealed record OpenTdbCategoriesResponse(
    [property: JsonPropertyName("trivia_categories")] List<OpenTdbCategoryItem> TriviaCategories);

/// <summary>Per-category counts returned by <c>api_count_global.php</c>.</summary>
public sealed record OpenTdbCountDetail(
    int TotalNumOfQuestions,
    int TotalNumOfPendingQuestions,
    int TotalNumOfVerifiedQuestions,
    int TotalNumOfRejectedQuestions);

/// <summary>Wrapper for <c>api_count_global.php</c>.</summary>
public sealed record OpenTdbCountGlobalResponse(
    OpenTdbCountDetail Overall,
    Dictionary<string, OpenTdbCountDetail> Categories);

/// <summary>Per-difficulty counts returned by <c>api_count.php?category=X</c>.</summary>
public sealed record OpenTdbCategoryQuestionCount(
    int TotalQuestionCount,
    int TotalEasyQuestionCount,
    int TotalMediumQuestionCount,
    int TotalHardQuestionCount);

/// <summary>Wrapper for <c>api_count.php?category=X</c>.</summary>
public sealed record OpenTdbCategoryCountResponse(
    [property: JsonPropertyName("category_id")] int CategoryId,
    [property: JsonPropertyName("category_question_count")] OpenTdbCategoryQuestionCount CategoryQuestionCount);

/// <summary>One question in the response from <c>api.php</c>; all string fields are base64-encoded.</summary>
public sealed record OpenTdbQuestionResult(
    string Category,
    string Type,
    string Difficulty,
    string Question,
    [property: JsonPropertyName("correct_answer")] string CorrectAnswer,
    [property: JsonPropertyName("incorrect_answers")] List<string> IncorrectAnswers);

/// <summary>Wrapper for <c>api.php</c>.</summary>
public sealed record OpenTdbQuestionsResponse(
    [property: JsonPropertyName("response_code")] int ResponseCode,
    List<OpenTdbQuestionResult> Results);

/// <summary>Wrapper for <c>api_token.php</c>.</summary>
public sealed record OpenTdbTokenResponse(
    [property: JsonPropertyName("response_code")] int ResponseCode,
    [property: JsonPropertyName("response_message")] string? ResponseMessage,
    string? Token);
