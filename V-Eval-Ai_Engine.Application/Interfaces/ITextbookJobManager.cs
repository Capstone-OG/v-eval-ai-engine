using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng quản lý tiến trình nền (Background Job) nạp tri thức SGK
/// </summary>
public interface ITextbookJobManager
{
    TextbookProcessingJobDto CreateJob(string fileName, string fileHash = "", int totalPages = 0, int startPage = 1);
    void UpdateJobProgress(string jobId, string step, int progressPercent, int currentPage = 0);
    void CompleteJob(string jobId, TextbookIngestResultDto result);
    void FailJob(string jobId, string error);
    TextbookProcessingJobDto? GetJob(string jobId);
    TextbookProcessingJobDto? GetJobByFileHash(string fileHash);
    TextbookProcessingJobDto? GetActiveJob();
    List<TextbookProcessingJobDto> GetAllJobs();
}
