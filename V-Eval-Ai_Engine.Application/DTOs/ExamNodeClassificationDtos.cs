using System.Text.Json.Serialization;

namespace V_Eval_Ai_Engine.Application.DTOs;

/// <summary>
/// Yêu cầu phân loại hàng loạt câu hỏi đề thi vào các nút của Cây Khung Năng Lực & Khung Chương Trình
/// </summary>
public class ClassifyExamNodesRequestDto
{
    [JsonPropertyName("subject_code")]
    public string? SubjectCode { get; set; }

    [JsonPropertyName("exam_title")]
    public string? ExamTitle { get; set; }

    [JsonPropertyName("taxonomy_nodes")]
    public List<ExamTaxonomyNodeRefDto> TaxonomyNodes { get; set; } = new();

    [JsonPropertyName("questions")]
    public List<ExamQuestionClassifyInputDto> Questions { get; set; } = new();
}

public class ExamTaxonomyNodeRefDto
{
    [JsonPropertyName("skill_id")]
    public string SkillId { get; set; } = string.Empty;

    [JsonPropertyName("domain_id")]
    public string? DomainId { get; set; }

    [JsonPropertyName("domain_name")]
    public string DomainName { get; set; } = string.Empty;

    [JsonPropertyName("chapter_id")]
    public string? ChapterId { get; set; }

    [JsonPropertyName("chapter_name")]
    public string ChapterName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_id")]
    public string? LessonId { get; set; }

    [JsonPropertyName("lesson_name")]
    public string LessonName { get; set; } = string.Empty;

    [JsonPropertyName("pattern_id")]
    public string? PatternId { get; set; }

    [JsonPropertyName("pattern_name")]
    public string PatternName { get; set; } = string.Empty;
}

public class ExamQuestionClassifyInputDto
{
    [JsonPropertyName("question_number")]
    public int QuestionNumber { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("passage_excerpt")]
    public string? PassageExcerpt { get; set; }

    [JsonPropertyName("suggested_skill_name")]
    public string? SuggestedSkillName { get; set; }
}

public class ClassifyExamNodesResponseDto
{
    [JsonPropertyName("classified_count")]
    public int ClassifiedCount { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "LexicalFallback";

    [JsonPropertyName("mappings")]
    public List<ExamQuestionNodeMappingDto> Mappings { get; set; } = new();
}

public class ExamQuestionNodeMappingDto
{
    [JsonPropertyName("question_number")]
    public int QuestionNumber { get; set; }

    [JsonPropertyName("skill_id")]
    public string SkillId { get; set; } = string.Empty;

    [JsonPropertyName("domain_id")]
    public string? DomainId { get; set; }

    [JsonPropertyName("domain_name")]
    public string DomainName { get; set; } = string.Empty;

    [JsonPropertyName("chapter_id")]
    public string? ChapterId { get; set; }

    [JsonPropertyName("chapter_name")]
    public string ChapterName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_id")]
    public string? LessonId { get; set; }

    [JsonPropertyName("lesson_name")]
    public string LessonName { get; set; } = string.Empty;

    [JsonPropertyName("pattern_id")]
    public string? PatternId { get; set; }

    [JsonPropertyName("pattern_name")]
    public string PatternName { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("bloom_level")]
    public int? BloomLevel { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

