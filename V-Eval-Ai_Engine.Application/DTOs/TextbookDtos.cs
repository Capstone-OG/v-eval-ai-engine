using System.Text.Json.Serialization;

namespace V_Eval_Ai_Engine.Application.DTOs;

/// <summary>
/// Thông tin điểm đánh dấu hình ảnh / sơ đồ / đồ thị cần FE cắt hoặc bổ sung
/// </summary>
public class DiagramPlaceholderDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("diagramType")]
    public string DiagramType { get; set; } = "FIGURE"; // GRAPH_2D, GEOMETRY_3D, LAB_DIAGRAM, CHART, FIGURE

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("suggestedAction")]
    public string SuggestedAction { get; set; } = "FE_CROP_OR_SUPPLEMENT";
}

/// <summary>
/// Đại diện cho 1 đoạn chunk văn bản tri thức SGK đã được gán tọa độ Chương - Bài - Dạng bài & KaTeX
/// </summary>
public class TextbookChunkDto
{
    public int ChunkIndex { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public int CharCount { get; set; }

    // Tọa độ Ma trận Tri thức GDPT 2018 (Lưu trong cột metadata::jsonb của KnowledgeVectorChunks, 0% thay đổi DB)
    public string SubjectCode { get; set; } = "MATH";
    public string SubjectName { get; set; } = "Toán học";
    public int Grade { get; set; } = 12;
    public string BookEdition { get; set; } = "Chân trời sáng tạo";
    public string ChapterName { get; set; } = string.Empty;
    public string LessonName { get; set; } = string.Empty;
    public string PatternName { get; set; } = string.Empty;
    public string SectionType { get; set; } = "THEORY"; // THEORY, FORMULA, EXAMPLE, EXERCISE, PASSAGE
    public bool HasDiagram { get; set; }
    public List<DiagramPlaceholderDto> ImagePlaceholders { get; set; } = new();
    public int DpiUsed { get; set; } = 56;
}

/// <summary>
/// Kết quả nạp tri thức SGK hoàn tất kèm cây cấu trúc Chương - Bài - Dạng bài liên kết
/// </summary>
public class TextbookIngestResultDto
{
    public string DocumentId { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string DomainId { get; set; } = string.Empty;
    public string SkillId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "TEXTBOOK";
    public int TotalPages { get; set; }
    public int TotalChars { get; set; }
    public int TotalChunks { get; set; }
    public string PdfUrl { get; set; } = string.Empty;

    // Metadata nhận diện tổng thể cuốn sách
    public string DetectedSubjectCode { get; set; } = "MATH";
    public string DetectedSubjectName { get; set; } = "Toán học";
    public int DetectedGrade { get; set; } = 12;
    public string DetectedBookEdition { get; set; } = "Chân trời sáng tạo";
    public bool IsInDgnlScope { get; set; } = true;
    public int DpiUsed { get; set; } = 56;
    public int TotalDiagramsDetected { get; set; }
    public List<string> ChaptersDetected { get; set; } = new();
    public List<string> LessonsDetected { get; set; } = new();
    public List<string> PatternsDetected { get; set; } = new();
    public ImportCurriculumRequestDto? SynthesizedCurriculum { get; set; }

    public List<TextbookChunkDto> Chunks { get; set; } = new();
}

/// <summary>
/// Trạng thái tiến trình nạp SGK ngầm (Background Job)
/// </summary>
public class TextbookProcessingJobDto
{
    public string JobId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Status { get; set; } = "PROCESSING"; // PROCESSING, COMPLETED, FAILED, PAUSED
    public int ProgressPercent { get; set; } = 0;
    public string CurrentStep { get; set; } = "Đang chuẩn bị...";
    public int ElapsedSeconds { get; set; } = 0;
    public int CurrentPage { get; set; } = 0;
    public int TotalPages { get; set; } = 0;
    public int StartPage { get; set; } = 1;
    public bool IsResumed { get; set; } = false;
    public int TargetDpi { get; set; } = 56;
    public TextbookIngestResultDto? Result { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Thông tin tóm tắt SGK đã nạp để UI Giáo trình chung đối chiếu hiển thị tick xanh và nội dung chi tiết
/// </summary>
public class IngestedTextbookSummaryDto
{
    public string SourceId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string DomainId { get; set; } = string.Empty;
    public string Status { get; set; } = "COMPLETED";
    public int TotalPages { get; set; }
    public int TotalChunks { get; set; }
    public string SubjectCode { get; set; } = "MATH";
    public string SubjectName { get; set; } = "Toán học";
    public int Grade { get; set; } = 12;
    public string BookEdition { get; set; } = "Chân trời sáng tạo";
    public int TotalDiagrams { get; set; }
    public List<string> Chapters { get; set; } = new();
    public List<string> Lessons { get; set; } = new();
    public List<string> Patterns { get; set; } = new();
    public List<TextbookChunkDto> Chunks { get; set; } = new();
}

public class TextbookSkeletonDto
{
    public string FileName { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public string SubjectCode { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public int Grade { get; set; } = 10;
    public string BookEdition { get; set; } = "Chân trời sáng tạo";
    public string VersionTag { get; set; } = "2025-2026";
    public bool IsVersionLocked { get; set; } = false;
    public List<SkeletonChapterDto> Chapters { get; set; } = new();
}

public class SkeletonChapterDto
{
    public int ChapterNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public int PageStart { get; set; }
    public int PageEnd { get; set; }
    public List<SkeletonLessonDto> Lessons { get; set; } = new();
}

public class SkeletonLessonDto
{
    public int LessonNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public int PageStart { get; set; }
    public int PageEnd { get; set; }
    public List<string> KeyFormulas { get; set; } = new();
    public List<SkeletonPatternDto> Patterns { get; set; } = new();
}

public class SkeletonPatternDto
{
    public string PatternCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int BloomLevel { get; set; } = 2; // 1..6 (Remember..Create)
    public string Description { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public List<string> RecognitionSigns { get; set; } = new();
    public List<GeneratedExerciseDto> Exercises { get; set; } = new();
}

public class GenerateExercisesRequestDto
{
    public string Subject { get; set; } = string.Empty;
    public int Grade { get; set; } = 10;
    public string Chapter { get; set; } = string.Empty;
    public string Lesson { get; set; } = string.Empty;
    public string PatternName { get; set; } = string.Empty;
    public int BloomLevel { get; set; } = 2;
    public string TheoryContext { get; set; } = string.Empty;
    public int Count { get; set; } = 3;
}

public class GeneratedExerciseDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Stem { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
    public int CorrectIndex { get; set; }
    public int BloomLevel { get; set; } = 2;
    public string Solution { get; set; } = string.Empty;
}
