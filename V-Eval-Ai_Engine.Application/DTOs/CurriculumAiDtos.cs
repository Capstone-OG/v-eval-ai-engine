using System.Text.Json.Serialization;

namespace V_Eval_Ai_Engine.Application.DTOs;

public class ImportCurriculumRequestDto
{
    [JsonPropertyName("domainId")]
    public string DomainId { get; set; } = string.Empty;

    [JsonPropertyName("domainName")]
    public string DomainName { get; set; } = string.Empty;

    [JsonPropertyName("chapters")]
    public List<ImportChapterDto> Chapters { get; set; } = new();

    [JsonPropertyName("processingTimeSeconds")]
    public double ProcessingTimeSeconds { get; set; }

    [JsonPropertyName("isRealExtraction")]
    public bool IsRealExtraction { get; set; } = true;

    [JsonPropertyName("sourceFileName")]
    public string? SourceFileName { get; set; }

    [JsonPropertyName("totalPagesProcessed")]
    public int TotalPagesProcessed { get; set; }

    [JsonPropertyName("totalCharactersExtracted")]
    public int TotalCharactersExtracted { get; set; }
}

public class ImportChapterDto
{
    [JsonPropertyName("chapterName")]
    public string ChapterName { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 0.25;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("lessons")]
    public List<ImportLessonDto> Lessons { get; set; } = new();
}

public class ImportLessonDto
{
    [JsonPropertyName("lessonName")]
    public string LessonName { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 0.1;

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("patterns")]
    public List<ImportPatternDto> Patterns { get; set; } = new();
}

public class ImportPatternDto
{
    [JsonPropertyName("patternName")]
    public string PatternName { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 0.05;

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }

    [JsonPropertyName("coreSkill")]
    public string? CoreSkill { get; set; }

    [JsonPropertyName("theoryRecap")]
    public string? TheoryRecap { get; set; }

    [JsonPropertyName("solutionMethod")]
    public string? SolutionMethod { get; set; }

    [JsonPropertyName("videoUrl")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 600;

    [JsonPropertyName("questions")]
    public List<ImportQuestionDto> Questions { get; set; } = new();
}

public class ImportQuestionDto
{
    [JsonPropertyName("difficultyLevel")]
    public int DifficultyLevel { get; set; } = 2;

    [JsonPropertyName("contentLatex")]
    public string ContentLatex { get; set; } = string.Empty;

    [JsonPropertyName("optionA")]
    public string OptionA { get; set; } = string.Empty;

    [JsonPropertyName("optionB")]
    public string OptionB { get; set; } = string.Empty;

    [JsonPropertyName("optionC")]
    public string OptionC { get; set; } = string.Empty;

    [JsonPropertyName("optionD")]
    public string OptionD { get; set; } = string.Empty;

    [JsonPropertyName("correctOption")]
    public string CorrectOption { get; set; } = "A";

    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}

public class AnalyzePatternRequestDto
{
    [JsonPropertyName("pattern_name")]
    public string PatternName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_name")]
    public string? LessonName { get; set; }

    [JsonPropertyName("domain_name")]
    public string? DomainName { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }
}

public class PatternExemplarQuestionDto
{
    [JsonPropertyName("difficulty_level")]
    public int DifficultyLevel { get; set; } = 2;

    [JsonPropertyName("content_latex")]
    public string ContentLatex { get; set; } = string.Empty;

    [JsonPropertyName("option_a")]
    public string OptionA { get; set; } = string.Empty;

    [JsonPropertyName("option_b")]
    public string OptionB { get; set; } = string.Empty;

    [JsonPropertyName("option_c")]
    public string OptionC { get; set; } = string.Empty;

    [JsonPropertyName("option_d")]
    public string OptionD { get; set; } = string.Empty;

    [JsonPropertyName("correct_option")]
    public string CorrectOption { get; set; } = "A";

    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}

public class AnalyzePatternResponseDto
{
    [JsonPropertyName("pattern_name")]
    public string PatternName { get; set; } = string.Empty;

    [JsonPropertyName("classification")]
    public string Classification { get; set; } = string.Empty;

    [JsonPropertyName("core_skill")]
    public string CoreSkill { get; set; } = string.Empty;

    [JsonPropertyName("bloom_level")]
    public int BloomLevel { get; set; } = 2;

    [JsonPropertyName("theory_recap")]
    public string TheoryRecap { get; set; } = string.Empty;

    [JsonPropertyName("solution_method")]
    public string SolutionMethod { get; set; } = string.Empty;

    [JsonPropertyName("exam_traps")]
    public string ExamTraps { get; set; } = string.Empty;

    [JsonPropertyName("casio_tricks")]
    public string CasioTricks { get; set; } = string.Empty;

    [JsonPropertyName("sample_question")]
    public PatternExemplarQuestionDto? SampleQuestion { get; set; }

    [JsonPropertyName("processing_time_seconds")]
    public double ProcessingTimeSeconds { get; set; }
}

public class GeneratePatternQuestionsRequestDto
{
    [JsonPropertyName("pattern_name")]
    public string PatternName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_name")]
    public string? LessonName { get; set; }

    [JsonPropertyName("domain_name")]
    public string? DomainName { get; set; }

    [JsonPropertyName("theory_recap")]
    public string? TheoryRecap { get; set; }

    [JsonPropertyName("solution_method")]
    public string? SolutionMethod { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; } = 2;
}

public class GeneratePatternQuestionsResponseDto
{
    [JsonPropertyName("questions")]
    public List<ImportQuestionDto> Questions { get; set; } = new();

    [JsonPropertyName("processing_time_seconds")]
    public double ProcessingTimeSeconds { get; set; }
}

