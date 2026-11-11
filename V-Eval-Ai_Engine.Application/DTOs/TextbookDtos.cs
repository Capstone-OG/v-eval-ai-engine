namespace V_Eval_Ai_Engine.Application.DTOs;

/// <summary>
/// Đại diện cho 1 đoạn chunk văn bản tri thức SGK
/// </summary>
public class TextbookChunkDto
{
    public int ChunkIndex { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public int CharCount { get; set; }
}

/// <summary>
/// Kết quả nạp tri thức SGK hoàn tất
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
    public TextbookIngestResultDto? Result { get; set; }
    public string? ErrorMessage { get; set; }
}
