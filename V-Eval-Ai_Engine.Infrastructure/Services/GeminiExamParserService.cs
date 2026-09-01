using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ bóc tách đề thi PDF sử dụng Google Gemini 2.5 Flash Multimodal API trực tiếp từ C#
/// </summary>
public class GeminiExamParserService : IExamParserService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiExamParserService> _logger;

    private const string GeminiPrompt = @"Bạn là một chuyên gia chuyển đổi tài liệu đề thi sang cấu trúc JSON chuẩn.
Hãy đọc toàn bộ tài liệu đề thi ĐGNL (Đánh giá năng lực) PDF này và trích xuất tất cả các câu hỏi.
Yêu cầu:
1. Trích xuất đúng cấu trúc câu hỏi đơn lẻ (single_questions) và chùm câu hỏi đọc hiểu (passages).
2. Mọi công thức toán học, vật lý, hóa học, phương trình, biến số, hằng số (kể cả các ký hiệu chữ đơn lẻ như x, y, z, m, T, t) BẮT BUỘC phải được bao bọc bởi một cặp dấu đô-la đơn $...$ (ví dụ: viết $y = -x^3 + 3(m-1)x^2 + 6mx + 1$, $z = 3 - i$ hoặc $\int_0^1 x dx$). Đảm bảo viết đúng các công thức phân số (dùng \frac{a}{b}), chỉ số dưới (dùng m_0, t_0), chỉ số trên (dùng x^2), tránh viết rời rạc vô nghĩa. KHÔNG được để trống hoặc dùng chữ thường không có dấu $ cho công thức.
3. Nếu trang có đồ thị, biểu đồ, hoặc sơ đồ hình vẽ, hãy thêm mô tả bằng chữ chi tiết về hình vẽ đó ngay dưới nội dung câu hỏi hoặc passage tương ứng (ví dụ: *([Hình vẽ]: Đồ thị parabol...)*) để người học nắm được thông tin.
4. Điền đầy đủ bốn phương án A, B, C, D vào thuộc tính options. Đảm bảo toàn bộ câu hỏi đều được trích xuất đầy đủ từ trang đầu đến trang cuối.
5. Phân tích nội dung từng câu hỏi để gợi ý tên dạng bài / kỹ năng tương ứng (suggested_skill_name), ví dụ: 'Thì động từ', 'Biện pháp tu từ', 'Phóng xạ hạt nhân', 'Cực trị hàm số', 'Đọc hiểu biểu đồ'...";

    public GeminiExamParserService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<GeminiExamParserService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ParsedExamDto> ParsePdfAsync(Stream pdfStream, string fileName, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration.GetSection("AiSettings")["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
        {
            throw new InvalidOperationException("Chưa cấu hình 'AiSettings:GeminiApiKey' hợp lệ trong appsettings.");
        }

        _logger.LogInformation("Bắt đầu đọc dữ liệu PDF '{FileName}' và mã hóa sang Base64...", fileName);

        // 1. Đọc stream PDF thành Base64
        byte[] pdfBytes;
        if (pdfStream is MemoryStream ms)
        {
            pdfBytes = ms.ToArray();
        }
        else
        {
            using var memoryStream = new MemoryStream();
            await pdfStream.CopyToAsync(memoryStream, cancellationToken);
            pdfBytes = memoryStream.ToArray();
        }

        string base64Data = Convert.ToBase64String(pdfBytes);
        _logger.LogInformation("Mã hóa Base64 thành công ({SizeKb:F1} KB). Đang gửi payload sang Gemini API...", pdfBytes.Length / 1024.0);

        // 2. Xây dựng JSON Schema cho Gemini
        var schema = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["passages"] = new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JsonObject
                        {
                            ["start_question"] = new JsonObject { ["type"] = "INTEGER" },
                            ["end_question"] = new JsonObject { ["type"] = "INTEGER" },
                            ["content"] = new JsonObject { ["type"] = "STRING" },
                            ["questions"] = new JsonObject
                            {
                                ["type"] = "ARRAY",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "OBJECT",
                                    ["properties"] = new JsonObject
                                    {
                                        ["question_number"] = new JsonObject { ["type"] = "INTEGER" },
                                        ["page_number"] = new JsonObject { ["type"] = "INTEGER" },
                                        ["content"] = new JsonObject { ["type"] = "STRING" },
                                        ["suggested_skill_name"] = new JsonObject { ["type"] = "STRING" },
                                        ["options"] = new JsonObject
                                        {
                                            ["type"] = "OBJECT",
                                            ["properties"] = new JsonObject
                                            {
                                                ["A"] = new JsonObject { ["type"] = "STRING" },
                                                ["B"] = new JsonObject { ["type"] = "STRING" },
                                                ["C"] = new JsonObject { ["type"] = "STRING" },
                                                ["D"] = new JsonObject { ["type"] = "STRING" }
                                            },
                                            ["required"] = new JsonArray { "A", "B", "C", "D" }
                                        }
                                    },
                                    ["required"] = new JsonArray { "question_number", "content", "options", "suggested_skill_name" }
                                }
                            }
                        },
                        ["required"] = new JsonArray { "start_question", "end_question", "content", "questions" }
                    }
                },
                ["single_questions"] = new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JsonObject
                        {
                            ["question_number"] = new JsonObject { ["type"] = "INTEGER" },
                            ["page_number"] = new JsonObject { ["type"] = "INTEGER" },
                            ["content"] = new JsonObject { ["type"] = "STRING" },
                            ["suggested_skill_name"] = new JsonObject { ["type"] = "STRING" },
                            ["options"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["A"] = new JsonObject { ["type"] = "STRING" },
                                    ["B"] = new JsonObject { ["type"] = "STRING" },
                                    ["C"] = new JsonObject { ["type"] = "STRING" },
                                    ["D"] = new JsonObject { ["type"] = "STRING" }
                                },
                                ["required"] = new JsonArray { "A", "B", "C", "D" }
                            }
                        },
                        ["required"] = new JsonArray { "question_number", "content", "options", "suggested_skill_name" }
                    }
                }
            },
            ["required"] = new JsonArray { "passages", "single_questions" }
        };

        // 3. Chuẩn bị Request Payload
        var requestPayload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["inlineData"] = new JsonObject
                            {
                                ["mimeType"] = "application/pdf",
                                ["data"] = base64Data
                            }
                        },
                        new JsonObject
                        {
                            ["text"] = GeminiPrompt
                        }
                    }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = schema
            }
        };

        var configuredModel = _configuration.GetSection("AiSettings")["GeminiModel"];
        var candidateModels = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            candidateModels.Add(configuredModel);
        }
        // Ưu tiên theo đúng yêu cầu: gemini-2.5-flash đầu tiên, rồi sang 3.5 và các model khác
        candidateModels.AddRange(new[] { 
            "gemini-2.5-flash", 
            "gemini-3.5-flash", 
            "gemini-flash-latest", 
            "gemini-flash-lite-latest", 
            "gemini-3.6-flash", 
            "gemini-3.7-flash"
        });

        string responseContent = string.Empty;
        bool isSuccess = false;

        foreach (var modelName in candidateModels.Distinct())
        {
            string requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = new StringContent(requestPayload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            _logger.LogInformation("Đang gửi yêu cầu tới mô hình '{ModelName}'...", modelName);
            try
            {
                var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
                responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Mô hình '{ModelName}' phản hồi thành công!", modelName);
                    isSuccess = true;
                    break;
                }

                _logger.LogWarning("Mô hình '{ModelName}' trả về lỗi HTTP {StatusCode}. Thử mô hình kế tiếp...", modelName, response.StatusCode);
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // Chỉ dừng nếu 400 Bad Request (lỗi cấu trúc payload)
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Lỗi kết nối tới mô hình '{ModelName}'. Thử mô hình kế tiếp...", modelName);
            }
        }

        if (!isSuccess)
        {
            _logger.LogWarning("Tất cả mô hình Gemini đều quá tải hoặc gặp sự cố (503/404). Tự động kích hoạt Local Fallback Parser để không làm gián đoạn người dùng...");
            return await RunLocalFallbackParserAsync(pdfBytes, fileName, cancellationToken);
        }

        // 5. Trích xuất Text JSON từ phản hồi của Gemini
        using var doc = JsonDocument.Parse(responseContent);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            _logger.LogWarning("Gemini không trả về candidates. Tự động chuyển sang Local Fallback Parser...");
            return await RunLocalFallbackParserAsync(pdfBytes, fileName, cancellationToken);
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.GetArrayLength() == 0)
        {
            _logger.LogWarning("Gemini không xuất được parts. Tự động chuyển sang Local Fallback Parser...");
            return await RunLocalFallbackParserAsync(pdfBytes, fileName, cancellationToken);
        }

        string rawJsonText = parts[0].GetProperty("text").GetString() ?? "{}";

        // 6. Deserialize nội dung JSON thành DTO
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var parsedResult = JsonSerializer.Deserialize<ParsedExamDto>(rawJsonText, options)
            ?? new ParsedExamDto();

        parsedResult.Format = "V-ACT Exam";
        parsedResult.FileName = fileName;
        parsedResult.TotalPassages = parsedResult.Passages.Count;
        parsedResult.TotalSingleQuestions = parsedResult.SingleQuestions.Count;

        // Ước tính tổng số trang dựa trên thuộc tính page_number lớn nhất
        int maxPage = 1;
        foreach (var q in parsedResult.SingleQuestions)
            if (q.PageNumber > maxPage) maxPage = q.PageNumber;
        foreach (var p in parsedResult.Passages)
            foreach (var q in p.Questions)
                if (q.PageNumber > maxPage) maxPage = q.PageNumber;

        parsedResult.TotalPages = maxPage;

        _logger.LogInformation("Bóc tách đề thi '{FileName}' bằng Gemini thành công: {Passages} chùm câu hỏi, {Questions} câu hỏi đơn lẻ.",
            fileName, parsedResult.TotalPassages, parsedResult.TotalSingleQuestions);

        return parsedResult;
    }

    /// <summary>
    /// Bộ bóc tách dự phòng cục bộ (Local Fallback Parser) khi Gemini bị lỗi 503 hoặc quá tải
    /// </summary>
    private async Task<ParsedExamDto> RunLocalFallbackParserAsync(byte[] pdfBytes, string fileName, CancellationToken cancellationToken)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        try
        {
            await File.WriteAllBytesAsync(tempFile, pdfBytes, cancellationToken);

            string[] candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Parsers", "exam_parser.py"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "V-Eval-Ai_Engine.Infrastructure", "Parsers", "exam_parser.py"),
                Path.Combine(Directory.GetCurrentDirectory(), "Parsers", "exam_parser.py"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "V-Eval-Ai_Engine.Infrastructure", "Parsers", "exam_parser.py"),
                Path.Combine(Directory.GetCurrentDirectory(), "archive", "legacy_python_parsers", "exam_parser.py"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "archive", "legacy_python_parsers", "exam_parser.py")
            };

            string? scriptPath = candidates.FirstOrDefault(File.Exists);
            if (scriptPath == null)
            {
                throw new FileNotFoundException("Không tìm thấy script exam_parser.py dự phòng cục bộ.");
            }

            _logger.LogInformation("Đang thực thi Local Fallback Parser với script: '{ScriptPath}'...", scriptPath);

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "python";
            process.StartInfo.Arguments = $"\"{scriptPath}\" \"{tempFile}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var localResult = JsonSerializer.Deserialize<ParsedExamDto>(output, options) ?? new ParsedExamDto();
                localResult.Format = "V-ACT Exam (Local Fallback Parser - AI Quá tải)";
                localResult.FileName = fileName;
                localResult.TotalPassages = localResult.Passages.Count;
                localResult.TotalSingleQuestions = localResult.SingleQuestions.Count;

                int maxPage = 1;
                foreach (var q in localResult.SingleQuestions)
                    if (q.PageNumber > maxPage) maxPage = q.PageNumber;
                foreach (var p in localResult.Passages)
                    foreach (var q in p.Questions)
                        if (q.PageNumber > maxPage) maxPage = q.PageNumber;
                localResult.TotalPages = maxPage;

                _logger.LogInformation("Local Fallback Parser hoàn tất xuất sắc: {Questions} câu hỏi, {Passages} chùm câu hỏi.",
                    localResult.TotalSingleQuestions, localResult.TotalPassages);

                return localResult;
            }

            string err = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Lỗi chạy Local Fallback Parser: {err}");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
