using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng trích xuất chữ thuần local & nạp tri thức SGK
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
        CancellationToken cancellationToken = default);
}
