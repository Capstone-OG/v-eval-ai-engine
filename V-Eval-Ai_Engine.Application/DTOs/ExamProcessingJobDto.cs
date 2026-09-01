using System.Text.Json.Serialization;

namespace V_Eval_Ai_Engine.Application.DTOs;

/// <summary>
/// DTO theo dõi tiến trình của tiến trình nền (Background Job) xử lý đề thi
/// </summary>
public class ExamProcessingJobDto
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PROCESSING"; // "PROCESSING", "COMPLETED", "FAILED"

    [JsonPropertyName("current_step")]
    public string CurrentStep { get; set; } = "Đang khởi tạo tác vụ phân tích đề thi...";

    [JsonPropertyName("elapsed_seconds")]
    public int ElapsedSeconds { get; set; }

    [JsonPropertyName("result")]
    public ParsedExamDto? Result { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}
