using System.Collections.Concurrent;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Quản lý tiến trình ngầm nạp tri thức SGK trong bộ nhớ (In-Memory)
/// </summary>
public class InMemoryTextbookJobManager : ITextbookJobManager
{
    private class JobEntry
    {
        public TextbookProcessingJobDto Dto { get; set; } = new();
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

    public TextbookProcessingJobDto CreateJob(string fileName)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new TextbookProcessingJobDto
        {
            JobId = jobId,
            FileName = fileName,
            Status = "PROCESSING",
            ProgressPercent = 5,
            CurrentStep = "Đã tiếp nhận tệp. Đang khởi tạo bộ nạp SGK...",
            ElapsedSeconds = 0
        };

        _jobs[jobId] = new JobEntry
        {
            Dto = job,
            StartedAt = DateTime.UtcNow
        };

        return job;
    }

    public void UpdateJobProgress(string jobId, string step, int progressPercent)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.Dto.CurrentStep = step;
            entry.Dto.ProgressPercent = Math.Clamp(progressPercent, 0, 99);
            entry.Dto.ElapsedSeconds = (int)(DateTime.UtcNow - entry.StartedAt).TotalSeconds;
        }
    }

    public void CompleteJob(string jobId, TextbookIngestResultDto result)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.Dto.Status = "COMPLETED";
            entry.Dto.ProgressPercent = 100;
            entry.Dto.CurrentStep = "Hoàn tất trích xuất và nạp tri thức SGK thành công!";
            entry.Dto.Result = result;
            entry.Dto.ElapsedSeconds = (int)(DateTime.UtcNow - entry.StartedAt).TotalSeconds;
        }
    }

    public void FailJob(string jobId, string error)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.Dto.Status = "FAILED";
            entry.Dto.CurrentStep = "Tác vụ gặp lỗi trong quá trình xử lý.";
            entry.Dto.ErrorMessage = error;
            entry.Dto.ElapsedSeconds = (int)(DateTime.UtcNow - entry.StartedAt).TotalSeconds;
        }
    }

    public TextbookProcessingJobDto? GetJob(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            if (entry.Dto.Status == "PROCESSING")
            {
                entry.Dto.ElapsedSeconds = (int)(DateTime.UtcNow - entry.StartedAt).TotalSeconds;
            }
            return entry.Dto;
        }
        return null;
    }
}
