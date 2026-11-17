using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng trích xuất chữ thuần local, OCR Vision Batch DPI thấp & nạp tri thức SGK có cấu trúc
/// </summary>
public interface ITextbookParserService
{
    Task<TextbookIngestResultDto> ParseAndIngestTextbookAsync(
        Stream pdfStream,
        string fileName,
        string domainId,
        string skillId,
        string docType,
        string ocrMode = "TEXT_HUMANITIES",
        bool forceReingest = false,
        Action<string, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        int targetDpi = 48,
        int batchSize = 2);

    ImportCurriculumRequestDto SynthesizeCurriculumFromChunks(
        string fileName,
        string domainId,
        List<TextbookChunkDto> chunks);

    Task<TextbookSkeletonDto> ScanTextbookSkeletonAsync(
        Stream pdfStream,
        string fileName,
        string? hintSubjectCode = null,
        int? hintGrade = null,
        string? hintBookEdition = null,
        CancellationToken cancellationToken = default);

    Task<List<GeneratedExerciseDto>> GeneratePatternExercisesAsync(
        GenerateExercisesRequestDto request,
        CancellationToken cancellationToken = default);
}
