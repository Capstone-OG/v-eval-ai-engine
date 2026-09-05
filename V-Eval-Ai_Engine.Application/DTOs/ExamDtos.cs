using System.Text.Json.Serialization;

namespace V_Eval_Ai_Engine.Application.DTOs;

/// <summary>
/// DTO chứa toàn bộ cấu trúc đề thi sau khi được AI bóc tách từ file PDF
/// </summary>
public class ParsedExamDto
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "V-ACT Exam";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("total_passages")]
    public int TotalPassages { get; set; }

    [JsonPropertyName("total_single_questions")]
    public int TotalSingleQuestions { get; set; }

    [JsonPropertyName("passages")]
    public List<ParsedPassageDto> Passages { get; set; } = new();

    [JsonPropertyName("single_questions")]
    public List<ParsedQuestionDto> SingleQuestions { get; set; } = new();

    [JsonPropertyName("pdf_url")]
    public string? PdfUrl { get; set; }
}

/// <summary>
/// DTO đại diện cho một chùm câu hỏi đọc hiểu / ngữ cảnh dùng chung (Passage)
/// </summary>
public class ParsedPassageDto
{
    [JsonPropertyName("start_question")]
    public int StartQuestion { get; set; }

    [JsonPropertyName("end_question")]
    public int EndQuestion { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("questions")]
    public List<ParsedQuestionDto> Questions { get; set; } = new();
}

/// <summary>
/// DTO đại diện cho một câu hỏi trắc nghiệm
/// </summary>
public class ParsedQuestionDto
{
    [JsonPropertyName("question_number")]
    public int QuestionNumber { get; set; }

    [JsonPropertyName("page_number")]
    public int PageNumber { get; set; } = 1;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("suggested_skill_name")]
    public string SuggestedSkillName { get; set; } = "Trắc nghiệm tổng hợp";

    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; set; } = new();

    [JsonPropertyName("correct_option")]
    public string CorrectOption { get; set; } = "A";

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("difficulty_level")]
    public int DifficultyLevel { get; set; } = 2;

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}
