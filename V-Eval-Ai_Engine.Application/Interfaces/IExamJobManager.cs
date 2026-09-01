using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng quản lý các tác vụ nền (Background Jobs) phân tích đề thi
/// </summary>
public interface IExamJobManager
{
    ExamProcessingJobDto CreateJob(string fileName);
    void UpdateJobProgress(string jobId, string step);
    void CompleteJob(string jobId, ParsedExamDto result);
    void FailJob(string jobId, string error);
    ExamProcessingJobDto? GetJob(string jobId);
}
