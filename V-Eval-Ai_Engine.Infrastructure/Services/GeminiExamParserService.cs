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

    private const string GeminiPrompt = @"BẠN LÀ MỘT HỆ THỐNG SCAN & CHUYỂN TỰ QUANG HỌC ĐỘ CHÍNH XÁC CAO (HIGH-PRECISION VERBATIM OCR) DÀNH CHO ĐỀ THI ĐGNL V-ACT.
Nhiệm vụ: Đọc trực quan hình ảnh tài liệu PDF và sao chép NGUYÊN VĂN 100% từng câu hỏi, từng ký tự sang JSON, TUYỆT ĐỐI KHÔNG ĐƯỢC SUY DIỄN HOẶC TỰ Ý SỬA ĐỔI.

QUY TẮC BẮT BUỘC ĐỂ ĐẢM BẢO ĐỘ TRUNG THỰC (ZERO-TOLERANCE RULES):
1. NGUYÊN TẮC TRUNG THỰC NGUYÊN VĂN (100% VERBATIM):
   - Đọc chính xác từng con số, từng chữ số, từng biến số, từng số mũ, từng dấu cộng trừ (+, -), từng dấu chấm phẩy và dấu ngoặc.
   - TUYỆT ĐỐI KHÔNG BỎ SÓT HỆ SỐ ĐỨNG NGAY SAU DẤU BẰNG: Ví dụ trong biểu thức 'y = 2x^3', số 2 đứng ngay sau dấu bằng, BẮT BUỘC phải ghi đúng '$y = 2x^3$', TUYỆT ĐỐI KHÔNG ĐƯỢC bỏ mất hệ số 2 thành '$y = x^3$'.
   - TUYỆT ĐỐI KHÔNG ĐỔI DẤU PHÉP TÍNH: Nếu trong đề là '3(m+1)' thì BẮT BUỘC giữ nguyên dấu cộng '$3(m+1)$', KHÔNG ĐƯỢC tự ý đổi thành '$3(m-1)$'.
   - TUYỆT ĐỐI KHÔNG TỰ BỊA ĐẶT THÊM HỆ SỐ: Nếu phương án là 'x + y + z - 3 = 0' thì ghi đúng '$x + y + z - 3 = 0$', KHÔNG ĐƯỢC tự thêm số 3 vào z thành '$x + y + 3z - 3 = 0$'. Nếu là 'x + 2y + z - 4 = 0' thì ghi đúng '$x + 2y + z - 4 = 0$', KHÔNG ĐƯỢC đổi thành '$x + 2y - 2z - 4 = 0$'.
   - TẤT CẢ các phương án A, B, C, D phải được đọc trực tiếp từng chữ số từ hình ảnh của trang đề, chép đúng nguyên trạng từng tọa độ, phương trình và giá trị.
2. ĐẶC BIỆT LƯU Ý VỀ CÁC PHƯƠNG ÁN GÂY NHIỄU (BẪY TOÁN HỌC TRẮC NGHIỆM):
   - Trong đề thi trắc nghiệm, tác giả thường cố tình tạo ra các PHƯƠNG ÁN SAI / BẪY TOÁN HỌC để thử thách học sinh (Ví dụ: đưa dấu giá trị tuyệt đối ra bên NGOÀI tích phân: \left| \int_{-1}^1 (x^3 - x) dx \right|).
   - BẠN LÀ MÁY QUÉT, KHÔNG ĐƯỢC 'SỬA SAI GIÙM TÁC GIẢ'! Thấy hai thanh gạch dọc đứng |...| ở hai đầu tích phân thì BẮT BUỘC chép đúng dấu giá trị tuyệt đối: \left| \int_{-1}^1 (x^3 - x) dx \right|, TUYỆT ĐỐI KHÔNG ĐƯỢC tự ý đổi thành ngoặc đơn hay tự giải toán chia tách tích phân ra các cận [-1;0] và [0;1].
3. CHUYỂN ĐỔI CÔNG THỨC TOÁN HỌC SANG LATEX:
   - Mọi biểu thức toán học, hàm số, phương trình, tọa độ, biến số (kể cả chữ đơn lẻ như x, y, z, m) BẮT BUỘC phải được bao bọc bởi một cặp dấu đô-la đơn $...$.
   - Sử dụng đúng cú pháp LaTeX chuẩn: \le, \ge, \frac{a}{b}, x^2, m_0, \log_2, \int, \pi...
4. CÂU HỎI TIẾNG ANH TÌM LỖI SAI (GẠCH CHÂN):
   - Ở các câu hỏi gạch chân tương ứng với lựa chọn A, B, C, D, hãy bao bọc từ/cụm từ được gạch chân bằng thẻ <u>...</u> kèm ký hiệu tương ứng (Ví dụ: <u>word</u> (A)).
5. HÌNH VẼ / BIỂU ĐỒ / ĐỒ THỊ:
   - Nếu có đồ thị, biểu đồ hoặc sơ đồ hình vẽ, hãy thêm mô tả chi tiết bằng chữ về hình vẽ đó ngay dưới nội dung câu hỏi hoặc passage tương ứng (ví dụ: *([Hình vẽ]: Đồ thị parabol...)*).
6. CHÙM BÀI ĐỌC (PASSAGES) & CÂU ĐỘC LẬP (SINGLE QUESTIONS):
   - Đoạn văn ngữ cảnh dùng chung đưa vào content của `passages` kèm start_question và end_question.
   - Câu hỏi riêng lẻ đưa vào `single_questions`. Đảm bảo trích xuất đầy đủ toàn bộ câu hỏi từ trang đầu đến trang cuối.
7. PHÂN TÍCH DẠNG BÀI:
   - Gợi ý tên dạng bài / kỹ năng tương ứng (suggested_skill_name) ngắn gọn, chuẩn xác.";

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

        // 3. Chuẩn bị Request Payload: Render từng trang PDF sang JPEG 150 DPI để loại bỏ hoàn toàn lỗi font ngầm và tình trạng nhòe ảnh
        var requestParts = new JsonArray();
        int pageCount = 0;
        try
        {
            var renderOptions = new PDFtoImage.RenderOptions { Dpi = 150 };
            foreach (var skBitmap in PDFtoImage.Conversion.ToImages(pdfBytes, options: renderOptions))
            {
                using (skBitmap)
                {
                    using var imageStream = new MemoryStream();
                    skBitmap.Encode(imageStream, SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
                    var pageBase64 = Convert.ToBase64String(imageStream.ToArray());
                    requestParts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = "image/jpeg",
                            ["data"] = pageBase64
                        }
                    });
                    pageCount++;
                }
            }
            _logger.LogInformation("Đã render thành công {PageCount} trang PDF sang ảnh JPEG sắc nét (150 DPI) để gửi sang Gemini Vision.", pageCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Không thể render PDF sang JPEG: {Message}. Tự động fallback sang gửi file PDF trực tiếp.", ex.Message);
            requestParts.Clear();
            requestParts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = "application/pdf",
                    ["data"] = base64Data
                }
            });
        }

        // Thêm Prompt kiểm duyệt nghiêm ngặt
        requestParts.Add(new JsonObject
        {
            ["text"] = GeminiPrompt
        });

        var requestPayload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = requestParts
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = schema,
                ["temperature"] = 0.0
            }
        };

        var configuredModel = _configuration.GetSection("AiSettings")["GeminiModel"];
        var candidateModels = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            candidateModels.Add(configuredModel);
        }
        // Các model Flash Vision hoạt động nhanh và ổn định nhất theo kiểm thử thực tế
        candidateModels.AddRange(new[] { 
            "gemini-flash-lite-latest", 
            "gemini-3.1-flash-lite", 
            "gemini-3.5-flash", 
            "gemini-3.6-flash", 
            "gemini-flash-latest"
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
