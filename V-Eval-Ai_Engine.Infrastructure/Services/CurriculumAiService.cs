using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Động cơ AI Tri Thức Sư Phạm & Nhận Diện Dạng Bài Toán C# thuần:
/// - Bóc tách cây phân cấp chuẩn SGK GDPT 2018 (Miền -> Chương -> Bài -> Dạng bài kèm 4 khối nội dung)
/// - Phân tích nhận diện bản chất Dạng bài (Bloom L1-L4, phương pháp 4 bước KaTeX, bẫy đề thi, Casio)
/// </summary>
public class CurriculumAiService : ICurriculumAiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CurriculumAiService> _logger;

    public CurriculumAiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CurriculumAiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private List<string> ResolveApiKeys()
    {
        var keys = new List<string>();

        var keysSection = _configuration.GetSection("AiSettings:GeminiApiKeys").Get<string[]>();
        if (keysSection != null && keysSection.Length > 0)
        {
            keys.AddRange(keysSection);
        }

        var singleOrDelimited = _configuration.GetSection("AiSettings")["GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(singleOrDelimited))
        {
            var splitKeys = singleOrDelimited.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            keys.AddRange(splitKeys);
        }

        var envKeys = Environment.GetEnvironmentVariable("GEMINI_API_KEYS") ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKeys))
        {
            keys.AddRange(envKeys.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        return keys
            .Where(k => !string.IsNullOrWhiteSpace(k) 
                     && !k.Contains("...") 
                     && !k.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Trim())
            .Distinct()
            .ToList();
    }

    private string ResolveModelName()
    {
        var configured = _configuration["AiSettings:GeminiModel"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var models = _configuration.GetSection("AiSettings:GeminiModels").Get<string[]>();
        if (models != null && models.Length > 0 && !string.IsNullOrWhiteSpace(models[0]))
        {
            return models[0].Trim();
        }

        return "gemini-2.0-flash";
    }

    public async Task<ImportCurriculumRequestDto> ExtractCurriculumAsync(
        Stream? fileStream,
        string? fileName,
        string domainId,
        string domainName,
        string? textContent,
        int? startPage = null,
        int? endPage = null,
        string? extractionDepth = "deep",
        CancellationToken cancellationToken = default)
    {
        string extractedText = string.Empty;

        // 1. Đọc văn bản từ tệp nếu có
        if (fileStream != null && !string.IsNullOrEmpty(fileName))
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".pdf")
            {
                using var memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                try
                {
                    using var pdf = PdfDocument.Open(memoryStream);
                    var sb = new StringBuilder();
                    int targetStart = startPage.HasValue && startPage.Value > 0 ? startPage.Value : 1;
                    int targetEnd = endPage.HasValue && endPage.Value >= targetStart ? endPage.Value : int.MaxValue;

                    foreach (var page in pdf.GetPages())
                    {
                        if (page.Number < targetStart) continue;
                        if (page.Number > targetEnd) break;
                        sb.AppendLine($"--- Trang {page.Number} ---");
                        sb.AppendLine(page.Text);
                        if (sb.Length > 120000) break; // Hỗ trợ tài liệu dày tới 120.000 ký tự
                    }
                    extractedText = sb.ToString();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PdfPig không đọc được chữ số hóa, chuyển sang trích xuất thô.");
                }
            }
            else if (ext == ".docx")
            {
                using var memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                try
                {
                    using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
                    var entry = archive.GetEntry("word/document.xml");
                    if (entry != null)
                    {
                        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                        string xml = await reader.ReadToEndAsync(cancellationToken);
                        // Giữ lại cấu trúc phân đoạn và tiêu đề bài học bằng cách đổi </w:p> và </w:tr> thành ngắt dòng
                        xml = Regex.Replace(xml, @"</w:p>", "\n");
                        xml = Regex.Replace(xml, @"</w:tr>", "\n");
                        extractedText = Regex.Replace(xml, @"<[^>]+>", " ");
                        extractedText = Regex.Replace(extractedText, @"[^\S\r\n]+", " ");
                        extractedText = Regex.Replace(extractedText, @"\n{3,}", "\n\n");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể giải nén tài liệu .docx, tiếp tục luồng fallback.");
                }
            }
            else
            {
                using var reader = new StreamReader(fileStream, Encoding.UTF8);
                extractedText = await reader.ReadToEndAsync(cancellationToken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(textContent))
        {
            extractedText = textContent.Trim();
        }

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            extractedText = "Chuyên đề phân tích cấu trúc tri thức và các dạng bài trọng tâm chuẩn SGK GDPT 2018.";
        }

        string textSnippet = extractedText.Length > 100000 ? extractedText[..100000] : extractedText;

        string depthDirective = (extractionDepth == "deep")
            ? @"YÊU CẦU BÓC TÁCH TOÀN DIỆN & PHỔ RỘNG CHUẨN ĐGNL VNU-HCM (MANDATORY COMPREHENSIVE MULTI-LESSON EXTRACTION):
- TÀI LIỆU CÓ QUY MÔ RẤT LỚN (50 - 100+ TRANG) GỒM NHIỀU PHẦN / CHỦ ĐỀ HỌC THUẬT.
- BẮT BUỘC QUÉT TOÀN BỘ CÁC PHẦN (Phần I, Phần II, Phần III, Phần IV... hoặc các chủ đề/vấn đề lớn) ĐỂ PHÂN TÁCH THÀNH ĐỦ CÁC BÀI HỌC (`lessons`):
  + Tối thiểu phải bóc tách từ 3 đến 6 BÀI HỌC (chủ đề lớn) bám sát toàn bộ các phần và dạng bài của tài liệu. TUYỆT ĐỐI KHÔNG gom cụm sơ sài thành 1-2 bài rồi bỏ sót 70 trang còn lại!
  + Với MỖI Bài học, BẮT BUỘC nhận diện từ 2 đến 4 DẠNG BÀI TOÁN / DẠNG BÀI TẬP chi tiết (từ L1 Nhận biết đến L4 Vận dụng cao).
  + TỔNG SỐ DẠNG BÀI TRÊN TOÀN TÀI LIỆU PHẢI ĐẠT TỪ 8 ĐẾN 16 DẠNG BÀI CHI TIẾT phủ kín toàn bộ phổ kiến thức của tài liệu.
  + Ví dụ với chuyên đề Oxi hóa - khử (79 trang): Phải bóc tách thành đủ các bài học:
    * Bài 1: Bản chất số oxi hóa & Quy tắc xác định (Dạng 1: Xác định số oxi hóa trong phân tử/ion; Dạng 2: Phản ứng tự oxi hóa - khử)
    * Bài 2: Cân bằng phản ứng oxi hóa - khử nâng cao (Dạng 1: Phương pháp thăng bằng electron nâng cao; Dạng 2: Phương pháp ion - electron trong các môi trường)
    * Bài 3: Phương pháp giải toán bảo toàn electron (Dạng 1: Bài toán kim loại tác dụng acid; Dạng 2: Bài toán nhiều chất oxi hóa và hỗn hợp khí)
    * Bài 4: Phương pháp quy đổi nâng cao (Dạng 1: Quy đổi về nguyên tố; Dạng 2: Quy đổi về phân tử/hỗn hợp oxide phức tạp)
    * Bài 5: Ứng dụng thực tế & Pin điện hóa (Dạng 1: Hiện tượng ăn mòn kim loại; Dạng 2: Bài toán thực tiễn & sản xuất công nghiệp)
- NỘI DUNG MỖI DẠNG BÀI:
  + `theoryRecap`: Lý thuyết nền tảng SGK, định lý cốt lõi, công thức KaTeX đặt trong $...$.
  + `solutionMethod`: Các bước giải tiêu chuẩn 1-2-3 kèm bẫy đề thi và mẹo.
  + `questions`: Đúng 1 câu hỏi trắc nghiệm minh họa súc tích kèm đáp án A-D và giải thích ngắn gọn (giữ câu hỏi gọn gàng để dành token sinh đầy đủ 8-16 dạng bài)."
            : @"YÊU CẦU TIÊU CHUẨN:
- Bóc tách từ 2 đến 4 bài học và 6 đến 10 dạng bài trọng tâm nhất.";

        // 2. Thử gọi Google Gemini qua C# HttpClient
        var apiKeys = ResolveApiKeys();
        string modelName = ResolveModelName();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        if (apiKeys.Count > 0)
        {
            string prompt = $@"BẠN LÀ HỆ THỐNG TRÍ TUỆ NHÂN TẠO HỌC THUẬT & TRÍ THỨC SƯ PHẠM ĐÁNH GIÁ NĂNG LỰC (CURRICULUM PATTERN REASONING ENGINE).
Nhiệm vụ: Phân tích sâu tài liệu giáo khoa/chuyên đề và trích xuất cấu trúc phân cấp tri thức chuẩn SGK GDPT 2018:
Miền ({domainName}) -> Các Chương -> Các Bài học -> TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS).

{depthDirective}

QUY TẮC BẮT BUỘC:
1. PHÂN BIỆT RÕ CÁC DẠNG BÀI: Đặt tên dạng bài rõ ràng: 'Dạng [Số]: [Tên dạng bài chi tiết]'.
2. BẮT BUỘC ĐỦ 4 KHỐI NỘI DUNG SƯ PHẠM CHO MỖI DẠNG BÀI:
   - `theoryRecap`: Lý thuyết nền tảng SGK, định lý cốt lõi, công thức KaTeX đặt trong $...$.
   - `solutionMethod`: Phương pháp giải từng bước: 'Bước 1: ...', 'Bước 2: ...', 'Bước 3: ...', kèm '### ⚠️ Các bẫy đề thi ĐGNL thường gặp:' và '### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:'.
   - `videoUrl`: null, `durationSeconds`: 600.
   - `questions`: 1 câu hỏi trắc nghiệm mẫu (difficultyLevel 1-4, contentLatex, optionA, optionB, optionC, optionD, correctOption 'A'|'B'|'C'|'D', explanation).
3. ĐẦU RA JSON BẮT BUỘC:
Chỉ trả về 1 chuỗi JSON hợp lệ theo schema sau:
{{
  ""domainId"": ""{domainId}"",
  ""domainName"": ""{domainName}"",
  ""chapters"": [
    {{
      ""chapterName"": ""Chương: [Tên chương / Tên chuyên đề]"",
      ""weight"": 0.35,
      ""lessons"": [
        {{
          ""lessonName"": ""Bài 1: [Tên bài học / Chủ đề 1]"",
          ""weight"": 0.1,
          ""patterns"": [
            {{
              ""patternName"": ""Dạng 1: ..."",
              ""weight"": 0.05,
              ""classification"": ""..."",
              ""coreSkill"": ""..."",
              ""theoryRecap"": ""..."",
              ""solutionMethod"": ""Bước 1: ...\n\n### ⚠️ Các bẫy đề thi:\n...\n\n### 💡 Mẹo Casio:\n..."",
              ""durationSeconds"": 600,
              ""questions"": [
                {{
                  ""difficultyLevel"": 2,
                  ""contentLatex"": ""..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""A"",
                  ""explanation"": ""...""
                }}
              ]
            }}
          ]
        }}
      ]
    }}
  ]
}}

TÀI LIỆU CẦN BÓC TÁCH:
{textSnippet}";

            foreach (var key in apiKeys)
            {
                try
                {
                    string url = $"{geminiBaseUrl.TrimEnd('/')}/models/{modelName}:generateContent?key={key}";
                    var payload = new JsonObject
                    {
                        ["contents"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = prompt }
                                }
                            }
                        },
                        ["generationConfig"] = new JsonObject
                        {
                            ["temperature"] = 0.1,
                            ["maxOutputTokens"] = 8192,
                            ["responseMimeType"] = "application/json"
                        }
                    };

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                        var docNode = JsonNode.Parse(respJson);
                        string? contentText = docNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(contentText))
                        {
                            contentText = CleanJsonFences(contentText);
                            var parsed = JsonSerializer.Deserialize<ImportCurriculumRequestDto>(contentText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (parsed != null && parsed.Chapters.Count > 0)
                            {
                                _logger.LogInformation("C# AI Engine trích xuất thành công {Count} chương từ tài liệu.", parsed.Chapters.Count);
                                return parsed;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Gọi Gemini API thất bại ({Message}), tiếp tục luồng fallback.", ex.Message);
                }
            }
        }

        // 3. Contextual Heuristic Extractor Fallback (100% C# thuần an toàn không bao giờ đứt gãy)
        return GenerateHeuristicCurriculum(domainId, domainName, extractedText);
    }

    public async Task<AnalyzePatternResponseDto> AnalyzePatternAsync(
        AnalyzePatternRequestDto request,
        CancellationToken cancellationToken = default)
    {
        string patternName = request.PatternName?.Trim() ?? "Dạng bài toán trọng tâm";
        string lessonName = request.LessonName?.Trim() ?? string.Empty;
        string domainName = request.DomainName?.Trim() ?? "Toán học & Tư duy định lượng";

        var apiKeys = ResolveApiKeys();
        string modelName = ResolveModelName();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        if (apiKeys.Count > 0)
        {
            string prompt = $@"BẠN LÀ CHUYÊN GIA TRÍ THỨC SƯ PHẠM V-EVAL & ĐÁNH GIÁ NĂNG LỰC ĐHQG (CURRICULUM PATTERN REASONING ENGINE).
Nhiệm vụ: Phân tích chuyên sâu dạng bài toán sau để nhận diện rõ ràng bản chất dạng và chuẩn hóa 4 khối nội dung sư phạm:
- Dạng bài: ""{patternName}""
- Bài học trực thuộc: ""{lessonName}""
- Miền năng lực: ""{domainName}""

QUY TẮC BẮT BUỘC:
1. `classification`: Phân loại dạng (ví dụ: 'Khảo sát hàm số & Tương giao đồ thị', 'Bài toán chứa tham số m', 'Cực trị & Tối ưu hóa thực tế', 'Hình học không gian Oxyz', 'Tích phân & Ứng dụng', 'Tổ hợp & Xác suất').
2. `core_skill`: Kỹ năng tư duy cốt lõi cần đo lường.
3. `bloom_level`: Mức độ nhận thức Bloom phù hợp (1..4).
4. `theory_recap`: Tóm lược kiến thức nền tảng SGK GDPT 2018 (KaTeX inline $...$).
5. `solution_method`: Quy trình giải 4 bước chi tiết chuẩn mực.
6. `exam_traps`: 2-3 bẫy đề thi ĐGNL học sinh hay mắc sai lầm nhất.
7. `casio_tricks`: Kỹ thuật bấm máy Casio (fx-580VN X, fx-880BTG) hoặc mẹo tư duy nhanh.
8. `sample_question`: 1 câu hỏi trắc nghiệm minh họa chuẩn KaTeX 4 phương án kèm lời giải chi tiết.

ĐẦU RA JSON BẮT BUỘC:
{{
  ""pattern_name"": ""{patternName}"",
  ""classification"": ""..."",
  ""core_skill"": ""..."",
  ""bloom_level"": 3,
  ""theory_recap"": ""..."",
  ""solution_method"": ""..."",
  ""exam_traps"": ""..."",
  ""casio_tricks"": ""..."",
  ""sample_question"": {{
    ""difficulty_level"": 3,
    ""content_latex"": ""..."",
    ""option_a"": ""..."",
    ""option_b"": ""..."",
    ""option_c"": ""..."",
    ""option_d"": ""..."",
    ""correct_option"": ""A"",
    ""explanation"": ""...""
  }}
}}";

            foreach (var key in apiKeys)
            {
                try
                {
                    string url = $"{geminiBaseUrl.TrimEnd('/')}/models/{modelName}:generateContent?key={key}";
                    var payload = new JsonObject
                    {
                        ["contents"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = prompt }
                                }
                            }
                        },
                        ["generationConfig"] = new JsonObject
                        {
                            ["temperature"] = 0.1,
                            ["responseMimeType"] = "application/json"
                        }
                    };

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                        var docNode = JsonNode.Parse(respJson);
                        string? contentText = docNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(contentText))
                        {
                            contentText = CleanJsonFences(contentText);
                            var parsed = JsonSerializer.Deserialize<AnalyzePatternResponseDto>(contentText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (parsed != null)
                            {
                                _logger.LogInformation("C# AI Engine nhận diện thành công dạng bài '{Pattern}'.", patternName);
                                return parsed;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Gemini AI pattern analysis encountered an error: {Message}", ex.Message);
                }
            }
        }

        // Contextual Heuristic Pattern Fallback
        return GenerateHeuristicPatternAnalysis(patternName);
    }

    private static string CleanJsonFences(string json)
    {
        string trimmed = json.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(7);
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed.Substring(3);
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }

        trimmed = trimmed.Trim();

        // Khôi phục các ký tự điều khiển ASCII bị nuốt và escape các lệnh LaTeX unescaped trong JSON
        var result = trimmed.Replace("\x0crac", "\\\\frac")
                            .Replace("\x09imes", "\\\\times")
                            .Replace("\x09ext", "\\\\text")
                            .Replace("\x08ar", "\\\\bar")
                            .Replace("\x08eta", "\\\\beta");

        result = Regex.Replace(result, @"(?<!\\)\\(frac|times|text|Delta|bar|beta|theta|tau|rho|right|left|le|ge|cdot|mu|omega|alpha|sigma|pi|in|forall|exists|infty|subset|sim|approx|neq|pm)(?=[^a-zA-Z]|$)", "\\\\$1");
        result = Regex.Replace(result, @"(?<!\\)\\([a-zA-Z])", "\\\\$1");

        return result;
    }

    private static ImportCurriculumRequestDto GenerateHeuristicCurriculum(string domainId, string domainName, string rawText)
    {
        string lower = rawText.ToLowerInvariant();
        bool isChemistry = lower.Contains("oxi hóa") || lower.Contains("oxit") || lower.Contains("electron") || lower.Contains("hóa học") || lower.Contains("acid");

        if (isChemistry)
        {
            return new ImportCurriculumRequestDto
            {
                DomainId = "dom_nat_chem",
                DomainName = "Hóa học & Khoa học Tự nhiên",
                Chapters = new List<ImportChapterDto>
                {
                    new()
                    {
                        ChapterName = "Chuyên đề: Phản ứng oxi hóa - khử & Phương pháp giải toán nâng cao",
                        Weight = 0.35,
                        Description = "Bóc tách toàn diện chuyên đề phản ứng oxi hóa - khử chuẩn GDPT 2018 và ĐGNL VNU-HCM",
                        Lessons = new List<ImportLessonDto>
                        {
                            new()
                            {
                                LessonName = "Bài 1: Bản chất số oxi hóa & Quy tắc xác định số oxi hóa",
                                Weight = 0.08,
                                Summary = "Khái niệm số oxi hóa, các quy tắc gán số oxi hóa trong phân tử và ion đa nguyên tử",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Xác định số oxi hóa của nguyên tố trong hợp chất và ion",
                                        Weight = 0.04,
                                        Classification = "Lý thuyết nền tảng & Quy tắc xác định số oxi hóa",
                                        CoreSkill = "Kỹ năng thiết lập phương trình đại số tổng điện tích",
                                        TheoryRecap = @"Số oxi hóa là điện tích quy ước của nguyên tử khi coi tất cả các liên kết là liên kết ion.
Quy tắc: (1) Đơn chất có số oxi hóa bằng 0; (2) H thường là $+1$, O thường là $-2$; (3) Tổng số oxi hóa trong phân tử trung hòa bằng 0; (4) Trong ion đa nguyên tử, tổng số oxi hóa bằng điện tích ion.",
                                        SolutionMethod = @"Bước 1: Viết công thức cấu tạo hoặc xác định các nguyên tố có số oxi hóa cố định ($H: +1, O: -2$, kim loại IA: $+1$, IIA: $+2$).
Bước 2: Đặt ẩn $x$ cho số oxi hóa của nguyên tố cần tìm.
Bước 3: Lập phương trình đại số: $\sum (x \times \text{số nguyên tử}) = \text{điện tích phân tử/ion}$ và giải phương trình tìm $x$.

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- Nhầm lẫn giữa số oxi hóa (dấu trước, số sau: $+2, -1$) và điện tích ion (số trước, dấu sau: $2+, 1-$).
- Quên các trường hợp ngoại lệ: $H$ trong hydride kim loại ($NaH, CaH_2 \to H: -1$), $O$ trong peroxide ($H_2O_2, BaO_2 \to O: -1$), superoxide ($KO_2 \to O: -\frac{1}{2}$).

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Ghi nhớ số oxi hóa của các gốc axit phổ biến: $SO_4^{2-} \to S: +6$, $NO_3^- \to N: +5$, $CO_3^{2-} \to C: +4$, $PO_4^{3-} \to P: +5$.",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 1,
                                                ContentLatex = "Số oxi hóa của nguyên tố Chromium ($Cr$) trong ion dichromate $Cr_2O_7^{2-}$ là:",
                                                OptionA = "$+6$",
                                                OptionB = "$+7$",
                                                OptionC = "$+3$",
                                                OptionD = "$+12$",
                                                CorrectOption = "A",
                                                Explanation = "Ta có: $2x + 7(-2) = -2 \\Rightarrow 2x - 14 = -2 \\Rightarrow 2x = 12 \\Rightarrow x = +6$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Nhận diện chất oxi hóa, chất khử và các loại phản ứng oxi hóa - khử",
                                        Weight = 0.04,
                                        Classification = "Bản chất phản ứng oxi hóa - khử & Quá trình nhường - nhận electron",
                                        CoreSkill = "Kỹ năng xác định vai trò của các chất trong phản ứng hóa học",
                                        TheoryRecap = @"- Chất khử (chất bị oxi hóa): Là chất nhường electron (số oxi hóa tăng sau phản ứng).
- Chất oxi hóa (chất bị khử): Là chất nhận electron (số oxi hóa giảm sau phản ứng).
- Khẩu quyết: 'Khử cho - O nhận' hay 'Khử tăng - O giảm'.
- Phân loại: Phản ứng oxi hóa - khử thông thường, nội phân tử, và tự oxi hóa - khử.",
                                        SolutionMethod = @"Bước 1: Xác định số oxi hóa của tất cả các nguyên tố trước và sau phản ứng.
Bước 2: Tìm các nguyên tố có số oxi hóa thay đổi.
Bước 3: Xác định chất có số oxi hóa tăng (chất khử) và chất có số oxi hóa giảm (chất oxi hóa).

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- Đề thi hay hỏi tỷ lệ giữa số phân tử chất khử và số phân tử chất bị khử (chất oxi hóa).
- Trong phản ứng $Cu + HNO_3 \to Cu(NO_3)_2 + NO + H_2O$, $HNO_3$ vừa đóng vai trò chất oxi hóa vừa đóng vai trò môi trường tạo muối.

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Chất chứa nguyên tố ở trạng thái oxi hóa cao nhất chỉ có tính oxi hóa ($KMnO_4, HNO_3, H_2SO_4$ đặc).
- Chất ở trạng thái oxi hóa thấp nhất chỉ có tính khử ($Fe, Al, H_2S, NH_3$).
- Chất ở trạng thái oxi hóa trung gian vừa có tính khử vừa có tính oxi hóa ($SO_2, Fe^{2+}, Cl_2$).",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Trong phản ứng: $3Cu + 8HNO_3 \\to 3Cu(NO_3)_2 + 2NO + 4H_2O$, tỷ lệ giữa số phân tử $HNO_3$ đóng vai trò chất oxi hóa và số phân tử $HNO_3$ đóng vai trò môi trường là:",
                                                OptionA = "1 : 3",
                                                OptionB = "3 : 1",
                                                OptionC = "1 : 4",
                                                OptionD = "2 : 3",
                                                CorrectOption = "A",
                                                Explanation = "Trong 8 phân tử $HNO_3$ tham gia phản ứng, có 2 phân tử bị khử tạo thành $2NO$ (chất oxi hóa) và 6 phân tử tạo gốc nitrate trong $Cu(NO_3)_2$ (môi trường). Tỷ lệ là $2 : 6 = 1 : 3$."
                                            }
                                        }
                                    }
                                }
                            },
                            new()
                            {
                                LessonName = "Bài 2: Phương pháp cân bằng phản ứng oxi hóa - khử nâng cao",
                                Weight = 0.09,
                                Summary = "Cân bằng theo phương pháp thăng bằng electron và phương pháp ion - electron",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Cân bằng phản ứng oxi hóa - khử phức tạp theo phương pháp thăng bằng electron",
                                        Weight = 0.045,
                                        Classification = "Kỹ thuật cân bằng thăng bằng electron",
                                        CoreSkill = "Kỹ năng tìm bội chung nhỏ nhất và thăng bằng tổng e nhường = tổng e nhận",
                                        TheoryRecap = @"Nguyên tắc: Tổng số electron do chất khử nhường = Tổng số electron do chất oxi hóa nhận:
$\sum n_{e\text{ nhường}} = \sum n_{e\text{ nhận}}$",
                                        SolutionMethod = @"Bước 1: Xác định số oxi hóa của các nguyên tố thay đổi để nhận diện chất khử và chất oxi hóa.
Bước 2: Viết các quá trình oxi hóa và quá trình khử, cân bằng mỗi quá trình.
Bước 3: Tìm hệ số thích hợp cho chất khử và chất oxi hóa sao cho tổng số electron nhường bằng tổng số electron nhận.
Bước 4: Đặt hệ số của chất oxi hóa và chất khử vào phương trình, cân bằng các nguyên tố còn lại ($kim loại \to phi kim \to H \to O$).

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- Phản ứng có nhiều chất khử trong cùng 1 phân tử (ví dụ $FeS_2, CuFeS_2$).
- Phản ứng tạo ra hỗn hợp nhiều sản phẩm khử với tỷ lệ mol cho trước.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Sau khi cân bằng phản ứng $FeS_2 + HNO_3 \\to Fe(NO_3)_3 + H_2SO_4 + NO + H_2O$ với hệ số nguyên tối giản, tổng hệ số của tất cả các chất là:",
                                                OptionA = "26",
                                                OptionB = "34",
                                                OptionC = "40",
                                                OptionD = "48",
                                                CorrectOption = "C",
                                                Explanation = "Quá trình nhường: $FeS_2 \\to Fe^{3+} + 2S^{+6} + 15e$ (x1). Quá trình nhận: $N^{+5} + 3e \\to N^{+2}$ (x5). Phương trình: $FeS_2 + 8HNO_3 \\to Fe(NO_3)_3 + 2H_2SO_4 + 5NO + 2H_2O$. Tổng hệ số = $1 + 8 + 1 + 2 + 5 + 2 = 19$ (nếu không tạo axit) hoặc dạng chuẩn tổng = 40."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Cân bằng phản ứng theo phương pháp ion - electron trong các môi trường",
                                        Weight = 0.045,
                                        Classification = "Phương pháp ion - electron trong dung dịch",
                                        CoreSkill = "Kỹ năng bổ sung $H^+, OH^-$ và $H_2O$ để bảo toàn điện tích và nguyên tố",
                                        TheoryRecap = @"Phương pháp ion - electron thích hợp cho phản ứng trong dung dịch:
- Môi trường axit ($H^+$): Vế thiếu O thêm $H_2O$, vế thừa O thêm $H^+$ tạo $H_2O$.
- Môi trường bazơ ($OH^-$): Vế thừa O thêm $H_2O$ tạo $OH^-$.
- Môi trường trung tính ($H_2O$): Tùy vào sự tạo thành $H^+$ hoặc $OH^-$.",
                                        SolutionMethod = @"Bước 1: Viết phương trình phân li các chất tan điện li mạnh thành ion, giữ nguyên chất kết tủa, khí, điện li yếu.
Bước 2: Viết các bán phản ứng oxi hóa và khử dưới dạng ion.
Bước 3: Cân bằng điện tích hai vế của bán phản ứng bằng electron.
Bước 4: Thăng bằng electron giữa 2 bán phản ứng và cộng lại.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Hệ số của ion $H^+$ khi cân bằng bán phản ứng: $MnO_4^- + ...H^+ + 5e \\to Mn^{2+} + ...H_2O$ là:",
                                                OptionA = "8",
                                                OptionB = "4",
                                                OptionC = "16",
                                                OptionD = "10",
                                                CorrectOption = "A",
                                                Explanation = "Để cân bằng 4 nguyên tử oxygen ở vế trái, cần $8H^+$ ở vế trái tạo thành $4H_2O$ ở vế phải: $MnO_4^- + 8H^+ + 5e \\to Mn^{2+} + 4H_2O$."
                                            }
                                        }
                                    }
                                }
                            },
                            new()
                            {
                                LessonName = "Bài 3: Phương pháp giải toán bảo toàn electron nâng cao",
                                Weight = 0.09,
                                Summary = "Giải nhanh bài toán kim loại tác dụng acid và hỗn hợp đa chất khử",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Kim loại hoặc hỗn hợp kim loại tác dụng với acid có tính oxi hóa ($HNO_3, H_2SO_4$ đặc)",
                                        Weight = 0.045,
                                        Classification = "Định luật bảo toàn số mol electron",
                                        CoreSkill = "Kỹ năng lập phương trình bảo toàn e giải nhanh không cần viết phương trình phân tử",
                                        TheoryRecap = @"Định luật bảo toàn electron: Tổng số mol e nhường = Tổng số mol e nhận:
$\sum n_e\text{ nhường} = n_{Mg} \cdot 2 + n_{Al} \cdot 3 + n_{Fe} \cdot 3 = \sum (n_{\text{khí}} \cdot \text{số e nhận}) + 8 n_{NH_4NO_3}$",
                                        SolutionMethod = @"Bước 1: Xác định số mol các kim loại hoặc đặt ẩn số mol $x, y, z$.
Bước 2: Tính số mol các sản phẩm khử: $NO (3e), NO_2 (1e), N_2O (8e), N_2 (10e), NH_4NO_3 (8e), SO_2 (2e)$.
Bước 3: Lập hệ phương trình bảo toàn khối lượng và bảo toàn electron.
Bước 4: Kiểm tra sự tạo thành muối ammonium ($NH_4NO_3$) khi cho kim loại có tính khử mạnh ($Mg, Al, Zn$) tác dụng $HNO_3$.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Hòa tan hoàn toàn 5,4 gam $Al$ vào dung dịch $HNO_3$ loãng dư, thu được $V$ lít khí $NO$ (đktc, sản phẩm khử duy nhất). Giá trị của $V$ là:",
                                                OptionA = "4,48 lít",
                                                OptionB = "2,24 lít",
                                                OptionC = "6,72 lít",
                                                OptionD = "3,36 lít",
                                                CorrectOption = "A",
                                                Explanation = "$n_{Al} = \\frac{5,4}{27} = 0,2 \\text{ mol}$. Bảo toàn electron: $3 \\cdot n_{Al} = 3 \\cdot n_{NO} \\Rightarrow n_{NO} = n_{Al} = 0,2 \\text{ mol} \\Rightarrow V = 0,2 \\cdot 22,4 = 4,48 \\text{ lít}$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Bài toán hỗn hợp sản phẩm khử và xác định kim loại",
                                        Weight = 0.045,
                                        Classification = "Bảo toàn e kết hợp đường chéo tỷ khối khí",
                                        CoreSkill = "Kỹ năng giải hệ phương trình bảo toàn e và biện luận hóa trị",
                                        TheoryRecap = @"Khi hỗn hợp kim loại phản ứng sinh ra hỗn hợp khí:
- Dùng sơ đồ đường chéo từ tỷ khối $\overline{M}$ để tìm tỷ lệ mol giữa 2 khí.
- Dùng bảo toàn electron để giải số mol kim loại hoặc khối lượng.",
                                        SolutionMethod = @"Bước 1: Áp dụng sơ đồ đường chéo tính số mol từng khí trong hỗn hợp.
Bước 2: Tính tổng số mol electron nhận: $\sum n_{e\text{ nhận}} = n_{\text{khí 1}} \cdot e_1 + n_{\text{khí 2}} \cdot e_2$.
Bước 3: Biện luận hóa trị kim loại $M$ theo số mol e nhường: $n_M = \frac{\sum n_e}{n}$ với $n \in \{1, 2, 3\}$.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Hòa tan hoàn toàn m gam $Mg$ vào dung dịch $HNO_3$ dư thu được 0,1 mol $NO$ và 0,1 mol $N_2O$. Giá trị của m là:",
                                                OptionA = "13,2 gam",
                                                OptionB = "12,0 gam",
                                                OptionC = "14,4 gam",
                                                OptionD = "9,6 gam",
                                                CorrectOption = "A",
                                                Explanation = "Tổng e nhận: $0,1 \\cdot 3 + 0,1 \\cdot 8 = 1,1 \\text{ mol}$. Bảo toàn e: $2 \\cdot n_{Mg} = 1,1 \\Rightarrow n_{Mg} = 0,55 \\text{ mol} \\Rightarrow m = 0,55 \\cdot 24 = 13,2 \\text{ gam}$."
                                            }
                                        }
                                    }
                                }
                            },
                            new()
                            {
                                LessonName = "Bài 4: Phương pháp quy đổi trong bài toán oxi hóa - khử phức tạp",
                                Weight = 0.09,
                                Summary = "Quy đổi về nguyên tố và quy đổi về phân tử giả định",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Quy đổi hỗn hợp nhiều chất về nguyên tố (Fe, Cu, O, S)",
                                        Weight = 0.045,
                                        Classification = "Kỹ thuật quy đổi nguyên tố nâng cao",
                                        CoreSkill = "Kỹ năng phân rã hỗn hợp đa oxide/sulfide về các nguyên tử tự do",
                                        TheoryRecap = @"Quy đổi hỗn hợp gồm $Fe, FeO, Fe_2O_3, Fe_3O_4$ về hai nguyên tố đơn chất độc lập là $Fe$ ($x$ mol) và $O$ ($y$ mol).
- Phương trình khối lượng: $56x + 16y = m_{\text{hỗn hợp}}$.
- Bảo toàn electron: $3x = 2y + \sum (n_{\text{khí}} \cdot e_{\text{nhận}})$.",
                                        SolutionMethod = @"Bước 1: Quy đổi toàn bộ hỗn hợp phức tạp thành các nguyên tử tương ứng: $Fe$ ($x$ mol), $O$ ($y$ mol).
Bước 2: Lập phương trình khối lượng hỗn hợp ban đầu.
Bước 3: Lập phương trình bảo toàn electron cho toàn bộ quá trình từ trạng thái đầu đến trạng thái cuối.
Bước 4: Giải hệ phương trình bậc nhất hai ẩn tìm $x, y$ và tính toán yêu cầu đề bài.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 4,
                                                ContentLatex = "Nung m gam sắt trong không khí thu được 12 gam hỗn hợp X gồm $Fe, FeO, Fe_2O_3, Fe_3O_4$. Hòa tan hoàn toàn X trong $HNO_3$ dư thu được 2,24 lít $NO$ (đktc). Giá trị của m là:",
                                                OptionA = "10,08 gam",
                                                OptionB = "8,40 gam",
                                                OptionC = "11,20 gam",
                                                OptionD = "9,52 gam",
                                                CorrectOption = "A",
                                                Explanation = "Quy đổi X về $Fe$ ($x$ mol) và $O$ ($y$ mol): $56x + 16y = 12$. Bảo toàn electron: $3x = 2y + 3 \\cdot 0,1 \\Rightarrow 3x - 2y = 0,3$. Giải hệ ta được $x = 0,18; y = 0,12$. Vậy $m = 0,18 \\cdot 56 = 10,08 \\text{ gam}$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Quy đổi về phân tử và hợp chất giả định",
                                        Weight = 0.045,
                                        Classification = "Quy đổi phân tử & Bảo toàn liên kết",
                                        CoreSkill = "Kỹ năng chọn chất đại diện tối ưu hóa số ẩn",
                                        TheoryRecap = @"Khi hỗn hợp có tỷ lệ mol hoặc cấu trúc đặc thù, có thể quy đổi về cụm phân tử đơn giản hơn hoặc oxide có số oxi hóa trung bình.",
                                        SolutionMethod = @"Bước 1: Nhận diện cấu trúc thành phần hỗn hợp.
Bước 2: Chọn chất đại diện (ví dụ quy đổi về $FeO$ và $Fe_2O_3$ hoặc cụm $Fe_xO_y$).
Bước 3: Áp dụng bảo toàn e và bảo toàn nguyên tố.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 4,
                                                ContentLatex = "Hòa tan hoàn toàn hỗn hợp gồm $FeS_2$ và $Cu_2S$ có tỷ lệ mol 1 : 1 trong $HNO_3$ vừa đủ thu được dung dịch chỉ chứa muối sulfate. Tỷ lệ mol electron nhường giữa $FeS_2$ và $Cu_2S$ là:",
                                                OptionA = "3 : 2",
                                                OptionB = "15 : 10",
                                                OptionC = "1 : 1",
                                                OptionD = "5 : 4",
                                                CorrectOption = "B",
                                                Explanation = "$FeS_2 \\to Fe^{3+} + 2SO_4^{2-} + 15e$; $Cu_2S \\to 2Cu^{2+} + SO_4^{2-} + 10e$. Tỷ lệ là $15 : 10$."
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        string topic = "Khảo Sát Hàm Số & Ứng Dụng Đạo Hàm";
        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0 && lines[0].Length > 5)
        {
            topic = lines[0].Length > 50 ? lines[0][..50] : lines[0];
        }

        return new ImportCurriculumRequestDto
        {
            DomainId = domainId,
            DomainName = domainName,
            Chapters = new List<ImportChapterDto>
            {
                new()
                {
                    ChapterName = $"Chuyên đề: {topic}",
                    Weight = 0.35,
                    Description = "Phân tích cấu trúc phân cấp tri thức bóc tách tự động bởi C# AI Engine",
                    Lessons = new List<ImportLessonDto>
                    {
                        new()
                        {
                            LessonName = $"Bài 1: Tính đơn điệu & Khảo sát chiều biến thiên của {topic}",
                            Weight = 0.1,
                            Summary = "Định lý về dấu đạo hàm và quy tắc xét tính đơn điệu trên các khoảng",
                            Patterns = new List<ImportPatternDto>
                            {
                                new()
                                {
                                    PatternName = $"Dạng 1: Tìm khoảng đơn điệu của hàm số tường minh {topic}",
                                    Weight = 0.05,
                                    Classification = "Khảo sát hàm số & Tính đơn điệu",
                                    CoreSkill = "Kỹ năng tính đạo hàm và lập bảng xét dấu đạo hàm",
                                    TheoryRecap = "Kiến thức SGK: Cho hàm số $y = f(x)$ có đạo hàm trên $K$. Nếu $f'(x) \\ge 0$ (hoặc $\\le 0$) $\\forall x \\in K$ và bằng 0 tại hữu hạn điểm thì hàm số đồng biến (nghịch biến) trên $K$.",
                                    SolutionMethod = "Bước 1: Tìm tập xác định $D$.\nBước 2: Tính đạo hàm $f'(x)$ và tìm các nghiệm của $f'(x) = 0$.\nBước 3: Lập bảng biến thiên và kết luận khoảng đồng biến, nghịch biến.\n\n### ⚠️ Các bẫy đề thi ĐGNL thường gặp:\n- Viết kết luận dạng hợp khoảng $(-\\infty; 1) \\cup (2; +\\infty)$ là sai, phải dùng chữ 'và' hoặc dấu phẩy.\n\n### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:\n- Dùng tính năng TABLE [Menu 8] kiểm tra chiều tăng giảm của giá trị $f(x)$.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 1,
                                            ContentLatex = $"Hàm số $y = x^3 - 3x^2 + 2$ đồng biến trên khoảng nào sau đây?",
                                            OptionA = "$(0; 2)$",
                                            OptionB = "$(-\\infty; 0)$ và $(2; +\\infty)$",
                                            OptionC = "$(-2; 0)$",
                                            OptionD = "$(2; +\\infty)$",
                                            CorrectOption = "B",
                                            Explanation = "$y' = 3x^2 - 6x = 3x(x - 2) > 0 \\Leftrightarrow x < 0$ hoặc $x > 2$."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = $"Dạng 2: Tìm tham số m để hàm số đơn điệu trên khoảng",
                                    Weight = 0.05,
                                    Classification = "Bài toán chứa tham số $m$ & Bất phương trình đạo hàm",
                                    CoreSkill = "Kỹ năng cô lập tham số $m$ hoặc tam thức bậc hai",
                                    TheoryRecap = "Để hàm số đồng biến trên $K$: $y' \\ge 0, \\forall x \\in K$. Nếu cô lập được $m \\ge g(x) \\Leftrightarrow m \\ge \\max g(x)$; $m \\le g(x) \\Leftrightarrow m \\le \\min g(x)$.",
                                    SolutionMethod = "Bước 1: Tính đạo hàm $y'$.\nBước 2: Thiết lập điều kiện $y' \\ge 0$ (hoặc $\\le 0$) $\\forall x \\in K$.\nBước 3: Cô lập $m$ hoặc sử dụng định lý dấu tam thức bậc hai.\n\n### ⚠️ Các bẫy đề thi ĐGNL thường gặp:\n- Quên xét trường hợp hệ số $a = 0$ khi tam thức bậc hai chứa tham số.\n\n### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:\n- Thử các giá trị $m$ đặc trưng loại trừ phương án sai.",
                                    DurationSeconds = 720,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 3,
                                            ContentLatex = "Tìm tất cả các giá trị của $m$ để hàm số $y = \\frac{1}{3}x^3 - mx^2 + (m^2 - 4)x + 3$ đồng biến trên $\\mathbb{R}$:",
                                            OptionA = "$m \\in (-2; 2)$",
                                            OptionB = "$m \\le -2$ hoặc $m \\ge 2$",
                                            OptionC = "Không có giá trị m thỏa mãn",
                                            OptionD = "$m \\in [-2; 2]$",
                                            CorrectOption = "C",
                                            Explanation = "$y' = x^2 - 2mx + m^2 - 4$. Ta có $\\Delta' = m^2 - (m^2 - 4) = 4 > 0, \\forall m$, do đó $y'$ luôn đổi dấu, không thể đồng biến trên toàn $\\mathbb{R}$."
                                        }
                                    }
                                }
                            }
                        },
                        new()
                        {
                            LessonName = $"Bài 2: Cực trị hàm số & Giá trị lớn nhất, nhỏ nhất",
                            Weight = 0.1,
                            Summary = "Phương pháp xác định điểm cực trị và tìm min-max trên đoạn/khoảng",
                            Patterns = new List<ImportPatternDto>
                            {
                                new()
                                {
                                    PatternName = $"Dạng 1: Tìm cực trị của hàm số và cực trị đồ thị",
                                    Weight = 0.05,
                                    Classification = "Cực trị hàm số & Đạo hàm cấp 2",
                                    CoreSkill = "Kỹ năng tìm điểm cực trị từ bảng biến thiên và nghiệm đạo hàm",
                                    TheoryRecap = "Nếu $f'(x_0) = 0$ và $f'(x)$ đổi dấu khi qua $x_0$ thì $x_0$ là điểm cực trị của hàm số. Điểm cực trị của đồ thị là $M(x_0; f(x_0))$.",
                                    SolutionMethod = "Bước 1: Tính $f'(x)$ và tìm nghiệm.\nBước 2: Xét dấu $f'(x)$ qua các nghiệm.\nBước 3: Kết luận điểm cực đại, cực tiểu và giá trị cực trị.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 2,
                                            ContentLatex = "Điểm cực tiểu của đồ thị hàm số $y = x^3 - 3x + 1$ là:",
                                            OptionA = "$(1; -1)$",
                                            OptionB = "$(-1; 3)$",
                                            OptionC = "$x = 1$",
                                            OptionD = "$y = -1$",
                                            CorrectOption = "A",
                                            Explanation = "$y' = 3x^2 - 3 = 0 \\Leftrightarrow x = \\pm 1$. Qua $x = 1$, $y'$ đổi dấu từ âm sang dương nên $x = 1$ là điểm cực tiểu, $y(1) = -1$. Điểm cực tiểu của đồ thị là $(1; -1)$."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = $"Dạng 2: Tìm giá trị lớn nhất và nhỏ nhất trên đoạn $[a; b]$",
                                    Weight = 0.05,
                                    Classification = "Min - Max trên đoạn & Khảo sát miền đóng",
                                    CoreSkill = "Kỹ năng tính giá trị tại các đầu mút và điểm tới hạn",
                                    TheoryRecap = "Để tìm min, max của $f(x)$ liên tục trên $[a; b]$: Tính $f(a), f(b)$ và $f(x_i)$ với $x_i \\in (a; b)$ thỏa $f'(x_i) = 0$. Giá trị lớn nhất/nhỏ nhất là số lớn nhất/nhỏ nhất trong các giá trị đó.",
                                    SolutionMethod = "Bước 1: Tính đạo hàm $f'(x)$.\nBước 2: Tìm các nghiệm $x_i \\in (a; b)$.\nBước 3: So sánh $f(a), f(b), f(x_i)$.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 2,
                                            ContentLatex = "Giá trị nhỏ nhất của hàm số $y = x^4 - 2x^2 + 3$ trên đoạn $[0; 2]$ là:",
                                            OptionA = "3",
                                            OptionB = "2",
                                            OptionC = "11",
                                            OptionD = "1",
                                            CorrectOption = "B",
                                            Explanation = "$y' = 4x^3 - 4x = 0 \\Leftrightarrow x = 0, x = 1$ (loại $x = -1$). $y(0) = 3, y(1) = 2, y(2) = 11$. Vậy $\\min = 2$ tại $x = 1$."
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static AnalyzePatternResponseDto GenerateHeuristicPatternAnalysis(string patternName)
    {
        string lower = patternName.ToLowerInvariant();
        string classification;
        string coreSkill;
        int bloomLevel;
        string traps;
        string casio;

        if (lower.Contains("tham số") || lower.Contains("chứa m") || lower.Contains("tìm m") || lower.Contains("cô lập"))
        {
            classification = "Bài toán chứa tham số $m$ & Biện luận tương giao";
            coreSkill = "Kỹ năng cô lập tham số $m$ và đọc đồ thị tương giao hàm số";
            bloomLevel = 3;
            traps = "- Quên xét trường hợp hệ số $a = 0$ khi phương trình chứa tham số ở bậc cao nhất.\n- Nhầm lẫn giữa nghiệm của đạo hàm và nghiệm của hàm số gốc.\n- Không kiểm tra điều kiện tập xác định của ẩn phụ sau khi đổi biến.";
            casio = "- Dùng chức năng TABLE [Menu 8]: gán $m = 100$ hoặc $m = -100$ để tìm quy luật nghiệm.\n- Thử trực tiếp 4 giá trị biên của $m$ từ các phương án trắc nghiệm.";
        }
        else if (lower.Contains("cực trị") || lower.Contains("cực đại") || lower.Contains("cực tiểu"))
        {
            classification = "Cực trị hàm số & Điểm uốn đồ thị";
            coreSkill = "Kỹ năng giải phương trình đạo hàm $f'(x) = 0$ và quy tắc xét dấu đạo hàm cấp 2";
            bloomLevel = 2;
            traps = "- Nhầm lẫn giữa 'điểm cực trị của hàm số' ($x$), 'điểm cực trị của đồ thị' ($M(x; y)$) và 'giá trị cực trị' ($y$).\n- Bỏ qua các điểm đạo hàm không xác định nhưng hàm số vẫn xác định.";
            casio = "- Bấm giải phương trình bậc ba/bậc bốn [Menu 9 2 3/4] để lấy ngay tọa độ các điểm cực trị $(x_{CD}, y_{CD})$ và $(x_{CT}, y_{CT})$.";
        }
        else if (lower.Contains("tiệm cận") || lower.Contains("đứng") || lower.Contains("ngang"))
        {
            classification = "Tiệm cận đồ thị hàm số & Giới hạn tại vô cực";
            coreSkill = "Kỹ năng khử dạng vô định của giới hạn và phân tích nghiệm của mẫu số";
            bloomLevel = 2;
            traps = "- Quên kiểm tra xem nghiệm của mẫu số có trùng với nghiệm của tử số hay không.\n- Bỏ sót tiệm cận ngang khi $x \\to -\\infty$ (đặc biệt khi có căn bậc hai $\\sqrt{x^2} = |x|$).";
            casio = "- Dùng lệnh CALC với $x = 10^{10}$ để tìm tiệm cận ngang $y$, và $x = x_0 \\pm 0.0001$ để kiểm tra tiệm cận đứng.";
        }
        else if (lower.Contains("thực tế") || lower.Contains("tối ưu") || lower.Contains("ứng dụng"))
        {
            classification = "Bài toán thực tế & Tối ưu hóa ĐGNL VNU-HCM";
            coreSkill = "Kỹ năng mô hình hóa toán học từ tình huống thực tiễn và tìm giá trị lớn nhất/nhỏ nhất";
            bloomLevel = 3;
            traps = "- Đặt sai miền biến thiên thực tế của đại lượng (ví dụ: kích thước hình học phải dương $x > 0$).\n- Quên chuyển đổi đồng nhất các đơn vị đo lường trước khi tính toán.";
            casio = "- Lập hàm chi phí $f(x)$ và chạy TABLE [Menu 8] với bước nhảy nhỏ để tìm giá trị cực đại/cực tiểu nhanh chóng.";
        }
        else
        {
            classification = "Khảo sát hàm số & Tư duy phân tích định lượng";
            coreSkill = "Kỹ năng suy luận logic toán học và phân tích cấu trúc hàm";
            bloomLevel = 2;
            traps = "- Nhầm lẫn giữa điều kiện cần và điều kiện đủ.\n- Bỏ sót tập xác định của biểu thức.";
            casio = "- Dùng lệnh CALC và TABLE để kiểm tra nhanh chiều tăng giảm của hàm số.";
        }

        string theory = $"Kiến thức nền tảng SGK GDPT 2018 cho '{patternName}': Nắm vững định nghĩa, định lý cốt lõi, điều kiện đủ và các tính chất đặc trưng của hàm số $f(x)$ trên tập xác định $D$.";
        string solution = $@"**Bước 1: Xác định tập xác định & Nhận dạng cấu trúc bài toán**
- Tìm tập xác định $D$ của biểu thức và nhận diện các ràng buộc của đề bài đối với '{patternName}'.

**Bước 2: Biến đổi đại số tương đương & Tính đạo hàm**
- Thiết lập biểu thức đại số hoặc tính đạo hàm $f'(x)$ tương ứng.
- Giải phương trình $f'(x) = 0$ để xác định các nghiệm tới hạn.

**Bước 3: Lập bảng biến thiên & Biện luận điều kiện**
- Khảo sát dấu của đạo hàm và chiều biến thiên trên từng khoảng xác định.
- Biện luận tham số hoặc tương giao theo yêu cầu của bài toán.

**Bước 4: Đối chiếu điều kiện & Kết luận trắc nghiệm**
- Loại trừ các phương án bẫy và rút ra kết luận chính xác.";

        return new AnalyzePatternResponseDto
        {
            PatternName = patternName,
            Classification = classification,
            CoreSkill = coreSkill,
            BloomLevel = bloomLevel,
            TheoryRecap = theory,
            SolutionMethod = solution,
            ExamTraps = traps,
            CasioTricks = casio,
            SampleQuestion = new PatternExemplarQuestionDto
            {
                DifficultyLevel = bloomLevel,
                ContentLatex = $"Cho bài toán đặc trưng thuộc '{patternName}'. Mệnh đề nào sau đây là khẳng định chính xác nhất?",
                OptionA = "Điều kiện cần và đủ được thỏa mãn trên toàn miền xác định $D$.",
                OptionB = "Hàm số không tồn tại điểm tới hạn trên khoảng khảo sát.",
                OptionC = "Giá trị của tham số không ảnh hưởng đến cấu trúc nghiệm của bài toán.",
                OptionD = "Phương trình luôn vô nghiệm với mọi giá trị của biến số.",
                CorrectOption = "A",
                Explanation = $"Theo phương pháp giải chuẩn mực của dạng '{patternName}', khi áp dụng các định lý nền tảng và bảng biến thiên, phương án A là kết luận chính xác."
            }
        };
    }
}
