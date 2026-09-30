using System.Collections.Concurrent;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Quản lý tiến trình ngầm nạp tri thức SGK trong bộ nhớ (In-Memory) với khả năng ghi nhớ Checkpoint và Resume
/// </summary>
public class InMemoryTextbookJobManager : ITextbookJobManager
{
    private class JobEntry
    {
        public TextbookProcessingJobDto Dto { get; set; } = new();
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _hashToJobMap = new();

    public TextbookProcessingJobDto CreateJob(string fileName, string fileHash = "", int totalPages = 0, int startPage = 1)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new TextbookProcessingJobDto
        {
            JobId = jobId,
            FileName = fileName,
            FileHash = fileHash,
            TotalPages = totalPages,
            StartPage = startPage,
            CurrentPage = startPage - 1,
            IsResumed = startPage > 1,
            Status = "PROCESSING",
            ProgressPercent = totalPages > 0 ? (int)((float)(startPage - 1) / totalPages * 100) : 5,
            CurrentStep = startPage > 1 
                ? $"Phát hiện checkpoint dở dang. Đang tiếp tục nạp từ trang {startPage}/{totalPages}..."
                : "Đã tiếp nhận tệp. Đang khởi tạo bộ nạp SGK...",
            ElapsedSeconds = 0
        };

        _jobs[jobId] = new JobEntry
        {
            Dto = job,
            StartedAt = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(fileHash))
        {
            _hashToJobMap[fileHash] = jobId;
        }

        return job;
    }

    public void UpdateJobProgress(string jobId, string step, int progressPercent, int currentPage = 0)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.Dto.CurrentStep = step;
            entry.Dto.ProgressPercent = Math.Clamp(progressPercent, 0, 99);
            if (currentPage > 0)
            {
                entry.Dto.CurrentPage = currentPage;
            }
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
            entry.Dto.CurrentPage = result.TotalPages;
            entry.Dto.TotalPages = result.TotalPages;
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

    public TextbookProcessingJobDto? GetJobByFileHash(string fileHash)
    {
        if (!string.IsNullOrWhiteSpace(fileHash) && _hashToJobMap.TryGetValue(fileHash, out var jobId))
        {
            return GetJob(jobId);
        }
        return null;
    }

    public TextbookProcessingJobDto? GetActiveJob()
    {
        var active = _jobs.Values
            .Where(e => e.Dto.Status == "PROCESSING")
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefault();

        if (active != null)
        {
            active.Dto.ElapsedSeconds = (int)(DateTime.UtcNow - active.StartedAt).TotalSeconds;
            return active.Dto;
        }
        return null;
    }

    public List<TextbookProcessingJobDto> GetAllJobs()
    {
        return _jobs.Values.Select(e =>
        {
            if (e.Dto.Status == "PROCESSING")
            {
                e.Dto.ElapsedSeconds = (int)(DateTime.UtcNow - e.StartedAt).TotalSeconds;
            }
            return e.Dto;
        }).ToList();
    }
}
