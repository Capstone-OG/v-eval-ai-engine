using System.Collections.Concurrent;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Quản lý trạng thái Background Job phân tích đề thi trong bộ nhớ
/// </summary>
public class InMemoryExamJobManager : IExamJobManager
{
    private class JobEntry
    {
        public ExamProcessingJobDto Dto { get; set; } = new();
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

    public ExamProcessingJobDto CreateJob(string fileName)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new ExamProcessingJobDto
        {
            JobId = jobId,
            FileName = fileName,
            Status = "PROCESSING",
            CurrentStep = "Đã tiếp nhận file. Đang chuẩn bị gửi sang Gemini AI...",
            ElapsedSeconds = 0
        };

        _jobs[jobId] = new JobEntry
        {
            Dto = job,
            StartedAt = DateTime.UtcNow
        };

        return job;
    }

    public void UpdateJobProgress(string jobId, string step)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.Dto.CurrentStep = step;
        }
    }

    public void CompleteJob(string jobId, ParsedExamDto result)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.Dto.Status = "COMPLETED";
            entry.Dto.CurrentStep = "Hoàn tất trích xuất cấu trúc đề thi thành công!";
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

    public ExamProcessingJobDto? GetJob(string jobId)
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
