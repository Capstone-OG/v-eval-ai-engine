using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng bóc tách tài liệu đề thi từ định dạng PDF sang đối tượng JSON chuẩn
/// </summary>
public interface IExamParserService
{
    /// <summary>
    /// Bóc tách tệp PDF đề thi thành dữ liệu cấu trúc
    /// </summary>
    /// <param name="pdfStream">Stream dữ liệu của tệp PDF</param>
    /// <param name="fileName">Tên gốc của tệp</param>
    /// <param name="cancellationToken">Token hủy tác vụ</param>
    /// <returns>Đối tượng ParsedExamDto chứa các passages và questions</returns>
    Task<ParsedExamDto> ParsePdfAsync(Stream pdfStream, string fileName, CancellationToken cancellationToken = default);
}
