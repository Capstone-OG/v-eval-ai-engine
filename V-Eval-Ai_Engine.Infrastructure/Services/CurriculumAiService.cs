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
  + `questions`: BẮT BUỘC BÓC TÁCH TỐI THIỂU TỪ 3 ĐẾN 5 CÂU HỎI TRẮC NGHIỆM CHO MỖI DẠNG BÀI (tuyệt đối không chỉ lấy 1 hoặc 2 câu; phải bóc tách toàn bộ các ví dụ mẫu và bài tập luyện tập trong tài liệu từ L1 đến L4), kèm đề bài KaTeX, 4 đáp án A-D, đáp án đúng và lời giải chi tiết từng bước."
            : @"YÊU CẦU TIÊU CHUẨN:
- Bóc tách từ 2 đến 4 bài học và 6 đến 10 dạng bài trọng tâm nhất. Mỗi dạng bài có ít nhất 3 câu hỏi trắc nghiệm bài mẫu.";

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

QUY TẮC BẮT BUỘC VỀ VÍ DỤ MINH HỌA, BÀI MẪU & NGÂN HÀNG CÂU HỎI (MANDATORY EXAMPLES & SAMPLE PROBLEMS):
1. PHÂN BIỆT RÕ CÁC DẠNG BÀI: Đặt tên dạng bài rõ ràng: 'Dạng [Số]: [Tên dạng bài chi tiết]'.
2. BẮT BUỘC ĐỦ 4 KHỐI NỘI DUNG SƯ PHẠM KÈM VÍ DỤ VÀ BÀI TOÁN MẪU:
   - `theoryRecap`: Lý thuyết nền tảng SGK, định lý cốt lõi, công thức KaTeX đặt trong $...$. BẮT BUỘC KÈM THEO '### 📌 Ví dụ minh họa SGK:' (Trích xuất ví dụ có sẵn từ tài liệu; nếu tài liệu không có thì AI bắt buộc phải tự tạo 1 ví dụ minh họa SGK trực quan có đề bài và phân tích/lời giải chi tiết).
   - `solutionMethod`: Thuật toán các bước giải: 'Bước 1: ...', 'Bước 2: ...', 'Bước 3: ...'. BẮT BUỘC KÈM THEO:
     * '### 📝 Bài toán mẫu áp dụng phương pháp:' (Trích xuất bài toán mẫu từ tài liệu; nếu không có thì AI bắt buộc phải tự tạo 1 bài toán mẫu có số liệu cụ thể và giải mẫu chi tiết từng bước theo đúng Bước 1, Bước 2, Bước 3).
     * '### ⚠️ Các bẫy đề thi ĐGNL thường gặp:' (2-3 bẫy đề thi hay gặp).
     * '### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:' (Mẹo bấm máy hoặc mẹo nhẩm nhanh).
   - `videoUrl`: null, `durationSeconds`: 600.
   - `questions`: BẮT BUỘC TRÍCH XUẤT TỐI THIỂU TỪ 3 ĐẾN 5 CÂU HỎI TRẮC NGHIỆM ĐA DẠNG CHO MỖI DẠNG BÀI (TUYỆT ĐỐI KHÔNG CHỈ SINH 1 HOẶC 2 CÂU; CẦN ĐỦ TỪ VÍ DỤ MẪU ĐẾN BÀI TẬP RÈN LUYỆN L1 -> L4):
     * Câu 1: [L1 - Nhận biết] Câu hỏi lý thuyết / khái niệm / nhận diện hiện tượng.
     * Câu 2: [L2 - Thông hiểu] Ví dụ mẫu cơ bản áp dụng trực tiếp công thức/quy tắc.
     * Câu 3: [L3 - Vận dụng] Bài toán định lượng ĐGNL, phương trình số liệu.
     * Câu 4: [L4 - Vận dụng cao] Bài toán tổng hợp, bẫy đề thi hoặc bài toán thực tiễn phân loại học sinh.
     * Câu 5: (Nếu có trong tài liệu) Bài tập rèn luyện củng cố mở rộng.
     Mỗi câu gồm: difficultyLevel (1-4), contentLatex (đề bài chứa KaTeX), optionA, optionB, optionC, optionD (phương án chứa KaTeX), correctOption ('A'|'B'|'C'|'D'), explanation (lời giải chi tiết từng bước bằng KaTeX).
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
              ""theoryRecap"": ""[Định lý & Công thức nền tảng SGK]\n\n### 📌 Ví dụ minh họa SGK:\n**Ví dụ:** ...\n**Phân tích & Lời giải:** ..."",
              ""solutionMethod"": ""Bước 1: ...\nBước 2: ...\nBước 3: ...\n\n### 📝 Bài toán mẫu áp dụng phương pháp:\n**Bài toán mẫu:** ...\n**Hướng dẫn giải theo các bước:**\n- Bước 1: ...\n- Bước 2: ...\n- Bước 3: ...\n\n### ⚠️ Các bẫy đề thi:\n...\n\n### 💡 Mẹo Casio:\n..."",
              ""durationSeconds"": 600,
              ""questions"": [
                {{
                  ""difficultyLevel"": 1,
                  ""contentLatex"": ""[Câu 1 - Nhận biết / Khái niệm]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""A"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 2,
                  ""contentLatex"": ""[Câu 2 - Thông hiểu / Ví dụ mẫu áp dụng]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""B"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 3,
                  ""contentLatex"": ""[Câu 3 - Vận dụng / Bài toán định lượng ĐGNL]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""C"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 4,
                  ""contentLatex"": ""[Câu 4 - Vận dụng cao / Bẫy đề thi nâng cao]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""D"",
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
                                EnrichPatternPedagogyAndQuestions(parsed, domainName);
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

        // Loại bỏ các ký tự escape không hợp lệ trong JSON (\% -> %, \$ -> $, \_ -> _, v.v.)
        result = Regex.Replace(result, @"\\([%$&_#^{}])", "$1");

        result = Regex.Replace(result, @"(?<!\\)\\(frac|times|text|Delta|bar|beta|theta|tau|rho|right|left|le|ge|cdot|mu|omega|alpha|sigma|pi|in|forall|exists|infty|subset|sim|approx|neq|pm)(?=[^a-zA-Z]|$)", "\\\\$1");
        result = Regex.Replace(result, @"(?<!\\)\\([a-zA-Z])", "\\\\$1");

        return result;
    }

    private static string EnrichGeneralKnowledgeTheory(string patName, string? currentTheory, string domainName)
    {
        string lower = (patName + " " + (currentTheory ?? "") + " " + domainName).ToLowerInvariant();

        if (lower.Contains("oxi hóa") || lower.Contains("khử") || lower.Contains("electron"))
        {
            return @"### 1. Khái Niệm Bản Chất & Mô Hình Điện Tích Quy Ước (SGK GDPT 2018):
- **Số oxi hóa**: Là điện tích quy ước của nguyên tử nguyên tố trong hợp chất khi giả định rằng tất cả các liên kết giữa các nguyên tử đều là liên kết ion (cặp electron dùng chung bị chuyển hẳn về nguyên tử có độ âm điện lớn hơn).
- **Ý nghĩa sư phạm**: Dấu của số oxi hóa đặt ở **phía trước** (ví dụ: $+2, -1, +6$), phân biệt rõ ràng với điện tích của ion thật đặt ở **phía sau** (ví dụ: $2+, 1-, 3+$).

### 2. Hệ Thống 4 Quy Tắc Gán Số Oxi Hóa Chuẩn Hóa:
- **Quy tắc 1 (Đơn chất):** Trong đơn chất, số oxi hóa của nguyên tố luôn luôn bằng $0$ (ví dụ: $Cu^0, Al^0, H_2^0, O_2^0, N_2^0, Cl_2^0, O_3^0$).
- **Quy tắc 2 (Nguyên tố cố định):** Trong hầu hết các hợp chất:
  + Kim loại kiềm (nhóm IA: $Li, Na, K$) luôn là $+1$; kim loại kiềm thổ (nhóm IIA: $Mg, Ca, Ba$) luôn là $+2$; Nhôm ($Al$) luôn là $+3$.
  + Fluorine ($F$) có độ âm điện lớn nhất nên luôn có số oxi hóa bằng $-1$.
  + Hydrogen ($H$) thường có số oxi hóa là $+1$.
  + Oxygen ($O$) thường có số oxi hóa là $-2$.
- **Quy tắc 3 (Phân tử trung hòa):** Trong một phân tử trung hòa điện, tổng đại số số oxi hóa của các nguyên tử bằng $0$:
  $$\sum (x_i \cdot n_i) = 0$$
- **Quy tắc 4 (Ion đơn & đa nguyên tử):** Trong ion đơn nguyên tử, số oxi hóa bằng điện tích ion. Trong ion đa nguyên tử, tổng số oxi hóa bằng điện tích của ion:
  $$\sum (x_i \cdot n_i) = \text{điện tích ion}$$

### 3. Bảng Các Trường Hợp Ngoại Lệ Cần Thuộc Lòng (ĐGNL VNU-HCM):
- **Oxygen trong Peroxide:** Trong $H_2O_2, Na_2O_2, BaO_2$, số oxi hóa của Oxygen là $-1$ (do liên kết $-O-O-$ không phân cực).
- **Oxygen trong Superoxide:** Trong $KO_2, RbO_2$, số oxi hóa của Oxygen là $-\frac{1}{2}$.
- **Oxygen với Fluorine:** Trong $OF_2$, số oxi hóa của Oxygen là $+2$; trong $O_2F_2$ là $+1$.
- **Hydrogen trong Hydride kim loại:** Trong $NaH, CaH_2, LiAlH_4$, số oxi hóa của Hydrogen là $-1$.
- **Hợp chất hữu cơ:** Xác định số oxi hóa theo từng nguyên tử Carbon gắn với các nhóm chức ($CH_4 \to C^{-4}, C_2H_6 \to C^{-3}, CH_3OH \to C^{-2}, HCHO \to C^0, HCOOH \to C^{+2}, CO_2 \to C^{+4}$).

### 4. Dấu Hiệu Phân Loại Bản Chất Phản Ứng:
- **Chất khử (bị oxi hóa):** Nhường electron, số oxi hóa **tăng** sau phản ứng (*Khử cho - Tăng*).
- **Chất oxi hóa (bị khử):** Nhận electron, số oxi hóa **giảm** sau phản ứng (*O nhận - Giảm*).
- **Chất môi trường:** Không thay đổi số oxi hóa, tham gia tạo môi trường axit, bazơ hoặc tạo muối kết tủa.";
        }

        return @"### 1. Bản Chất Khái Niệm & Định Lý Nền Tảng SGK GDPT 2018:
- Nắm vững định nghĩa, điều kiện tồn tại, tập xác định và các tính chất giải tích/đại số cốt lõi.
- Xác định rõ mối tương quan giữa giả thiết đầu vào và mục tiêu định lượng của bài toán.

### 2. Hệ Thống Hệ Thức & Công Thức Toán Học Nền Tảng:
- Áp dụng các định lý cơ bản, bảng đạo hàm, nguyên hàm hoặc hệ thức tọa độ vector tương ứng.
- Đảm bảo kiểm tra các điều kiện tới hạn và tính liên tục trên miền khảo sát.

### 3. Các Trường Hợp Biên & Lưu Ý Khảo Thí ĐGNL:
- Cảnh giác với các điểm gián đoạn, điểm kỳ dị không thuộc tập xác định.
- Phân biệt rõ điều kiện cần và điều kiện đủ trong các bài toán chứa tham số.";
    }

    private static void EnrichPatternPedagogyAndQuestions(ImportCurriculumRequestDto curriculum, string domainName)
    {
        if (curriculum?.Chapters == null) return;

        foreach (var chap in curriculum.Chapters)
        {
            if (chap.Lessons == null) continue;
            foreach (var les in chap.Lessons)
            {
                if (les.Patterns == null) continue;
                foreach (var pat in les.Patterns)
                {
                    string patName = pat.PatternName ?? "Dạng bài";

                    // 1. Làm giàu kiến thức chung nền tảng SGK nếu nội dung quá ngắn (< 250 ký tự)
                    if (string.IsNullOrWhiteSpace(pat.TheoryRecap) || pat.TheoryRecap.Trim().Length < 250)
                    {
                        pat.TheoryRecap = EnrichGeneralKnowledgeTheory(patName, pat.TheoryRecap, domainName);
                    }

                    // Bổ sung Ví dụ minh họa SGK vào Khối 1 nếu thiếu
                    if (!pat.TheoryRecap.Contains("Ví dụ minh họa") && !pat.TheoryRecap.Contains("### 📌"))
                    {
                        pat.TheoryRecap = pat.TheoryRecap.TrimEnd() + "\n\n" + CreateContextualTheoryExample(patName, pat.TheoryRecap, domainName);
                    }

                    // 2. Tự động bổ sung Bài toán mẫu áp dụng vào Khối 2 nếu thiếu
                    if (string.IsNullOrWhiteSpace(pat.SolutionMethod) || 
                        (!pat.SolutionMethod.Contains("Bài toán mẫu") && !pat.SolutionMethod.Contains("Ví dụ áp dụng") && !pat.SolutionMethod.Contains("### 📝")))
                    {
                        string currentMethod = pat.SolutionMethod ?? "Bước 1: Nhận diện cấu trúc bài toán.\nBước 2: Lập sơ đồ biến đổi và thiết lập bán phản ứng.\nBước 3: Áp dụng định luật bảo toàn e và giải đại số.\nBước 4: Đối chiếu điều kiện xác định và kết luận.";
                        string sample = CreateContextualMethodSample(patName, currentMethod, domainName);

                        int trapIdx = currentMethod.IndexOf("### ⚠️", StringComparison.OrdinalIgnoreCase);
                        int casioIdx = currentMethod.IndexOf("### 💡", StringComparison.OrdinalIgnoreCase);
                        int splitIdx = (trapIdx >= 0) ? trapIdx : (casioIdx >= 0 ? casioIdx : -1);

                        if (splitIdx >= 0)
                        {
                            pat.SolutionMethod = currentMethod[..splitIdx].TrimEnd() + "\n\n" + sample + "\n\n" + currentMethod[splitIdx..].TrimStart();
                        }
                        else
                        {
                            pat.SolutionMethod = currentMethod.TrimEnd() + "\n\n" + sample;
                        }
                    }

                    if (!pat.SolutionMethod.Contains("### ⚠️"))
                    {
                        pat.SolutionMethod += "\n\n" + CreateContextualTraps(patName, domainName);
                    }
                    if (!pat.SolutionMethod.Contains("### 💡"))
                    {
                        pat.SolutionMethod += "\n\n" + CreateContextualCasio(patName, domainName);
                    }

                    // 3. ĐẢM BẢO NGÂN HÀNG CÂU HỎI ĐA CẤP ĐỘ BLOOM:
                    // L1: >= 2 câu, L2: >= 2 câu, L3: >= 5 câu, L4: >= 5 câu (Tổng >= 14 câu chuyên sâu)
                    pat.Questions ??= new List<ImportQuestionDto>();
                    string theory = pat.TheoryRecap ?? "";
                    string method = pat.SolutionMethod ?? "";

                    var l1Pool = CreateContextualL1Questions(patName, theory, domainName);
                    var l2Pool = CreateContextualL2Questions(patName, theory, domainName);
                    var l3Pool = CreateContextualL3Questions(patName, method, domainName);
                    var l4Pool = CreateContextualL4Questions(patName, method, domainName);

                    // Bổ sung L1
                    int currentL1 = pat.Questions.Count(q => q.DifficultyLevel == 1);
                    foreach (var q in l1Pool)
                    {
                        if (currentL1 >= 2) break;
                        if (!pat.Questions.Any(x => x.ContentLatex == q.ContentLatex))
                        {
                            pat.Questions.Insert(currentL1, q);
                            currentL1++;
                        }
                    }

                    // Bổ sung L2
                    int currentL2 = pat.Questions.Count(q => q.DifficultyLevel == 2);
                    foreach (var q in l2Pool)
                    {
                        if (currentL2 >= 2) break;
                        if (!pat.Questions.Any(x => x.ContentLatex == q.ContentLatex))
                        {
                            pat.Questions.Insert(currentL1 + currentL2, q);
                            currentL2++;
                        }
                    }

                    // Bổ sung L3 (đảm bảo ít nhất 4-5 câu)
                    int currentL3 = pat.Questions.Count(q => q.DifficultyLevel == 3);
                    foreach (var q in l3Pool)
                    {
                        if (currentL3 >= 5) break;
                        if (!pat.Questions.Any(x => x.ContentLatex == q.ContentLatex))
                        {
                            pat.Questions.Add(q);
                            currentL3++;
                        }
                    }

                    // Bổ sung L4 (đảm bảo ít nhất 4-5 câu)
                    int currentL4 = pat.Questions.Count(q => q.DifficultyLevel == 4);
                    foreach (var q in l4Pool)
                    {
                        if (currentL4 >= 5) break;
                        if (!pat.Questions.Any(x => x.ContentLatex == q.ContentLatex))
                        {
                            pat.Questions.Add(q);
                            currentL4++;
                        }
                    }
                }
            }
        }
    }

    private static string CreateContextualTheoryExample(string patName, string? existingTheory, string domainName)
    {
        string lower = (patName + " " + (existingTheory ?? "") + " " + domainName).ToLowerInvariant();

        if (lower.Contains("cân bằng") || lower.Contains("thăng bằng electron"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Cân bằng phản ứng oxi hóa - khử giữa nhôm ($Al$) và khí oxygen ($O_2$) tạo thành nhôm oxide ($Al_2O_3$):
$$Al + O_2 \to Al_2O_3$$
**Phân tích & Lời giải:**
- Quá trình oxi hóa: $Al^0 \to Al^{+3} + 3e \quad (\times 4)$
- Quá trình khử: $O_2^0 + 4e \to 2O^{-2} \quad (\times 3)$
- Thăng bằng electron: $4 \times 3e = 3 \times 4e = 12e$.
- Phương trình cân bằng: $4Al + 3O_2 \to 2Al_2O_3$.";
        }

        if (lower.Contains("ion - electron") || lower.Contains("môi trường"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Viết bán phản ứng oxi hóa - khử của ion permanganate ($MnO_4^-$) trong môi trường acid ($H^+$).
**Phân tích & Lời giải:**
- Trạng thái đầu và cuối: $MnO_4^- \to Mn^{2+}$.
- Vế trái có 4 nguyên tử $O$, thêm $8H^+$ vào vế trái tạo $4H_2O$ ở vế phải.
- Cân bằng điện tích: $(-1) + 8(+1) = +7$; vế phải có điện tích $+2 \Rightarrow$ Cần nhận $5e$.
- Bán phản ứng chuẩn: $MnO_4^- + 8H^+ + 5e \to Mn^{2+} + 4H_2O$.";
        }

        if (lower.Contains("số oxi hóa") || lower.Contains("oxi hoá"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Xác định số oxi hóa của nguyên tố Phosphorus ($P$) trong hợp chất $H_3PO_4$ và trong ion $PO_4^{3-}$.
**Phân tích & Lời giải:**
- Trong phân tử trung hòa $H_3PO_4$: Gọi số oxi hóa của $P$ là $x$. Ta có: $3(+1) + x + 4(-2) = 0 \Leftrightarrow 3 + x - 8 = 0 \Rightarrow x = +5$.
- Trong ion đa nguyên tử $PO_4^{3-}$: $x + 4(-2) = -3 \Leftrightarrow x - 8 = -3 \Rightarrow x = +5$.";
        }

        if (lower.Contains("chất khử") || lower.Contains("chất oxi hóa") || lower.Contains("tự oxi hóa"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Cho phản ứng: $Cl_2 + 2NaOH \to NaCl + NaClO + H_2O$. Xác định vai trò của $Cl_2$.
**Phân tích & Lời giải:**
- Số oxi hóa của Chlorine: Trước phản ứng trong $Cl_2$ là $0$. Sau phản ứng: trong $NaCl$ là $-1$ (giảm $\Rightarrow$ chất oxi hóa), trong $NaClO$ là $+1$ (tăng $\Rightarrow$ chất khử).
- $Cl_2$ vừa là chất khử vừa là chất oxi hóa $\Rightarrow$ Đây là phản ứng tự oxi hóa - khử.";
        }

        if (lower.Contains("bảo toàn electron") || lower.Contains("bảo toàn e"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Hòa tan hoàn toàn $0,1\text{ mol } Mg$ vào dung dịch $HNO_3$ thu được $V$ lít khí $NO$ (sản phẩm khử duy nhất, đktc).
**Phân tích & Lời giải:**
- Quá trình nhường: $Mg^0 \to Mg^{+2} + 2e \Rightarrow n_{e\text{ nhường}} = 2 \times 0,1 = 0,2\text{ mol}$.
- Quá trình nhận: $N^{+5} + 3e \to NO \Rightarrow n_{e\text{ nhận}} = 3 \times n_{NO}$.
- Bảo toàn electron: $3 \times n_{NO} = 0,2 \Rightarrow n_{NO} = \frac{0,2}{3}\text{ mol} \Rightarrow V = \frac{0,2}{3} \times 22,4 \approx 1,493\text{ lít}$.";
        }

        if (lower.Contains("quy đổi"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Quy đổi $12\text{ gam}$ hỗn hợp rắn gồm $Fe, FeO, Fe_2O_3, Fe_3O_4$ thành hệ nguyên tố.
**Phân tích & Lời giải:**
- Đưa toàn bộ hỗn hợp oxide phức tạp về 2 thành phần độc lập là $Fe$ ($x$ mol) và $O$ ($y$ mol).
- Phương trình bảo toàn khối lượng hỗn hợp ban đầu: $56x + 16y = 12$.";
        }

        if (lower.Contains("cồn") || lower.Contains("breathalyzer") || lower.Contains("ethanol"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Phản ứng đổi màu của potassium dichromate khi tài xế thổi hơi cồn:
$$3C_2H_5OH + 2K_2Cr_2O_7 + 8H_2SO_4 \to 3CH_3COOH + 2Cr_2(SO_4)_3 + 2K_2SO_4 + 11H_2O$$
**Phân tích & Lời giải:**
- Cation $Cr_2O_7^{2-}$ có màu da cam đặc trưng bị khử thành $Cr^{3+}$ có màu xanh lá cây.
- Tỷ lệ số mol phản ứng là: $3\text{ mol } C_2H_5OH \Leftrightarrow 2\text{ mol } K_2Cr_2O_7$.";
        }

        if (lower.Contains("chuẩn độ") || lower.Contains("vitamin") || lower.Contains("lượng vết"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Chuẩn độ hàm lượng Vitamin C ($C_6H_8O_6$) bằng dung dịch Iodine ($I_2$) dùng chỉ thị hồ tinh bột:
$$C_6H_8O_6 + I_2 \to C_6H_6O_6 + 2HI$$
**Phân tích & Lời giải:**
- Vitamin C khử $I_2$ thành ion $I^-$ không màu.
- Khi vừa hết Vitamin C, 1 giọt $I_2$ dư phản ứng ngay với hồ tinh bột tạo phức chất màu xanh tím bền vững trong 30 giây.";
        }

        if (lower.Contains("đơn điệu") || lower.Contains("đồng biến") || lower.Contains("nghịch biến"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Xét tính đơn điệu của hàm số $y = x^3 - 3x + 1$ trên $\mathbb{R}$.
**Phân tích & Lời giải:**
- Tập xác định $D = \mathbb{R}$. Đạo hàm $y' = 3x^2 - 3 = 3(x^2 - 1)$.
- Cho $y' = 0 \Leftrightarrow x = \pm 1$.
- Bảng xét dấu: $y' > 0$ trên $(-\infty; -1)$ và $(1; +\infty) \Rightarrow$ Hàm số đồng biến; $y' < 0$ trên $(-1; 1) \Rightarrow$ Hàm số nghịch biến.";
        }

        if (lower.Contains("cực trị"))
        {
            return @"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Tìm tọa độ điểm cực đại của đồ thị hàm số $y = -x^3 + 3x^2 - 4$.
**Phân tích & Lời giải:**
- $y' = -3x^2 + 6x = -3x(x - 2) = 0 \Leftrightarrow x = 0$ hoặc $x = 2$.
- Tại $x = 2$, đạo hàm đổi dấu từ dương sang âm nên $x = 2$ là điểm cực đại của hàm số.
- Giá trị cực đại $y(2) = 0 \Rightarrow$ Tọa độ điểm cực đại của đồ thị là $M(2; 0)$.";
        }

        return $@"### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Xét bài toán minh họa điển hình trong chương trình SGK cho chủ đề '{patName}'.
**Phân tích & Lời giải:**
- Áp dụng trực tiếp định lý cốt lõi và các hệ thức toán học/hóa học cơ sở.
- Đối chiếu điều kiện xác định để rút ra kết luận chuẩn xác.";
    }

    private static string CreateContextualMethodSample(string patName, string? existingMethod, string domainName)
    {
        string lower = (patName + " " + (existingMethod ?? "") + " " + domainName).ToLowerInvariant();

        if (lower.Contains("cân bằng") || lower.Contains("thăng bằng electron"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Cân bằng phản ứng oxi hóa - khử sau theo phương pháp thăng bằng electron:
$$FeSO_4 + KMnO_4 + H_2SO_4 \to Fe_2(SO_4)_3 + MnSO_4 + K_2SO_4 + H_2O$$
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Xác định số oxi hóa thay đổi):** $Fe^{+2} \to Fe^{+3}$ (nhường $1e$); $Mn^{+7} \to Mn^{+2}$ (nhận $5e$).
- **Bước 2 (Lập bán phản ứng & thăng bằng e):**
  $2Fe^{+2} \to 2Fe^{+3} + 2e \quad (\times 5)$
  $Mn^{+7} + 5e \to Mn^{+2} \quad (\times 2)$
  $\Rightarrow$ Tổng số electron trao đổi là $10e$.
- **Bước 3 (Điền hệ số và hoàn thiện theo thứ tự):**
  - Điền $10$ vào $FeSO_4$, $2$ vào $KMnO_4$, $5$ vào $Fe_2(SO_4)_3$, $2$ vào $MnSO_4$, $1$ vào $K_2SO_4$.
  - Đếm gốc $SO_4^{2-}$ bên vế phải: $5 \times 3 + 2 + 1 = 18 \Rightarrow$ Vế trái cần thêm $8H_2SO_4$.
  - Cân bằng $H$ và $O$: $8H_2SO_4$ tạo thành $8H_2O$.
  $$\Rightarrow 10FeSO_4 + 2KMnO_4 + 8H_2SO_4 \to 5Fe_2(SO_4)_3 + 2MnSO_4 + K_2SO_4 + 8H_2O$$";
        }

        if (lower.Contains("ion - electron") || lower.Contains("môi trường"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Cân bằng phương trình ion rút gọn của phản ứng giữa ion $Fe^{2+}$ và ion $Cr_2O_7^{2-}$ trong môi trường acid:
$$Fe^{2+} + Cr_2O_7^{2-} + H^+ \to Fe^{3+} + Cr^{3+} + H_2O$$
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Viết hai bán phản ứng ion):**
  Bán phản ứng oxi hóa: $Fe^{2+} \to Fe^{3+} + 1e$
  Bán phản ứng khử: $Cr_2O_7^{2-} + 14H^+ + 6e \to 2Cr^{3+} + 7H_2O$
- **Bước 2 (Thăng bằng electron):**
  Nhân $6$ vào bán phản ứng oxi hóa: $6Fe^{2+} \to 6Fe^{3+} + 6e$.
- **Bước 3 (Cộng đại số hai bán phản ứng):**
  $$6Fe^{2+} + Cr_2O_7^{2-} + 14H^+ \to 6Fe^{3+} + 2Cr^{3+} + 7H_2O$$";
        }

        if (lower.Contains("số oxi hóa") || lower.Contains("oxi hoá"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Xác định số oxi hóa của nguyên tố Chromium ($Cr$) trong ion dichromate $Cr_2O_7^{2-}$ theo 3 bước chuẩn.
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Xác định số oxi hóa cố định):** Trong ion $Cr_2O_7^{2-}$, Oxygen có số oxi hóa thường gặp là $-2$.
- **Bước 2 (Đặt ẩn và lập phương trình điện tích):** Gọi số oxi hóa của $Cr$ là $x$. Ta có: $2 \times x + 7 \times (-2) = -2$.
- **Bước 3 (Giải phương trình và kết luận):** $2x - 14 = -2 \Leftrightarrow 2x = 12 \Rightarrow x = +6$. Vậy số oxi hóa của $Cr$ là $+6$.";
        }

        if (lower.Contains("bảo toàn electron") || lower.Contains("bảo toàn e"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Hòa tan hoàn toàn $5,4\text{ gam } Al$ bằng dung dịch $HNO_3$ loãng dư, thu được $V$ lít khí $NO$ (ở đktc, là sản phẩm khử duy nhất).
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Tính số mol e nhường):** $n_{Al} = \frac{5,4}{27} = 0,2\text{ mol} \Rightarrow n_{e\text{ nhường}} = 3 \times 0,2 = 0,6\text{ mol}$.
- **Bước 2 (Lập phương trình e nhận):** $N^{+5} + 3e \to NO \Rightarrow n_{e\text{ nhận}} = 3 \times n_{NO}$.
- **Bước 3 (Bảo toàn e và tìm đáp số):** Cho tổng e nhường = nhận: $3 \times n_{NO} = 0,6 \Rightarrow n_{NO} = 0,2\text{ mol} \Rightarrow V = 0,2 \times 22,4 = 4,48\text{ lít}$.";
        }

        if (lower.Contains("quy đổi"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Nung $m\text{ gam } Fe$ trong không khí thu được $12\text{ gam}$ hỗn hợp X ($Fe, FeO, Fe_2O_3, Fe_3O_4$). Hòa tan hoàn toàn X trong $HNO_3$ dư thu được $2,24\text{ lít } NO$ (đktc). Tìm $m$.
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Quy đổi hỗn hợp về nguyên tố):** Quy đổi X thành $Fe$ ($x$ mol) và $O$ ($y$ mol). Phương trình khối lượng: $56x + 16y = 12$ (1).
- **Bước 2 (Bảo toàn electron):** $3x = 2y + 3 \times 0,1 \Leftrightarrow 3x - 2y = 0,3$ (2).
- **Bước 3 (Giải hệ phương trình):** Từ (1) và (2) $\Rightarrow x = 0,18\text{ mol}; y = 0,12\text{ mol} \Rightarrow m = 0,18 \times 56 = 10,08\text{ gam}$.";
        }

        if (lower.Contains("cồn") || lower.Contains("breathalyzer") || lower.Contains("ethanol"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Chuẩn độ $15\text{ mL}$ mẫu huyết tương của một tài xế cần dùng đúng $9\text{ mL}$ dung dịch $K_2Cr_2O_7$ nồng độ $0,05\text{ M}$. Xác định nồng độ cồn trong huyết tương ($g/L$).
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Tính số mol chất oxi hóa):** $n_{Cr_2O_7} = 0,009 \times 0,05 = 0,00045\text{ mol}$.
- **Bước 2 (Tính số mol và khối lượng cồn):** Tỷ lệ phản ứng $3C_2H_5OH : 2K_2Cr_2O_7 \Rightarrow n_{ethanol} = 1,5 \times 0,00045 = 0,000675\text{ mol} \Rightarrow m_{ethanol} = 0,000675 \times 46 = 0,03105\text{ gam}$.
- **Bước 3 (Tính nồng độ g/L):** Nồng độ = $\frac{0,03105\text{ g}}{0,015\text{ L}} = 2,07\text{ g/L}$.";
        }

        if (lower.Contains("chuẩn độ") || lower.Contains("vitamin") || lower.Contains("lượng vết") || lower.Contains("oxygen"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Chuẩn độ $25\text{ mL}$ nước cam ép cần dùng vừa đủ $15\text{ mL}$ dung dịch $I_2$ nồng độ $0,005\text{ M}$. Tính hàm lượng Vitamin C ($M = 176\text{ g/mol}$) có trong $100\text{ mL}$ nước cam ép.
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Tính số mol chất chuẩn):** $n_{I_2} = 0,015 \times 0,005 = 7,5 \times 10^{-5}\text{ mol}$.
- **Bước 2 (Tính lượng Vitamin C trong mẫu 25 mL):** Tỷ lệ $1 : 1 \Rightarrow n_{Vit C (25mL)} = 7,5 \times 10^{-5}\text{ mol}$.
- **Bước 3 (Quy đổi ra 100 mL):** Trong $100\text{ mL}$ (gấp 4 lần): $n = 4 \times 7,5 \times 10^{-5} = 3 \times 10^{-4}\text{ mol} \Rightarrow m = 3 \times 10^{-4} \times 176 = 0,0528\text{ g} = 52,8\text{ mg}$.";
        }

        if (lower.Contains("đơn điệu") || lower.Contains("đồng biến") || lower.Contains("tham số"))
        {
            return @"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Tìm tất cả các giá trị của tham số $m$ để hàm số $y = x^3 - 3mx^2 + 3(m^2 - 1)x + 1$ đồng biến trên khoảng $(1; 2)$.
**Hướng dẫn giải theo các bước:**
- **Bước 1 (Tính đạo hàm):** $y' = 3x^2 - 6mx + 3(m^2 - 1) = 3[x^2 - 2mx + m^2 - 1] = 3(x - m - 1)(x - m + 1)$.
- **Bước 2 (Thiết lập điều kiện $y' \ge 0$ trên $(1; 2)$):** Phương trình $y' = 0$ có 2 nghiệm $x_1 = m - 1 < x_2 = m + 1$. Để hàm số đồng biến trên $(1; 2)$ thì khoảng $(1; 2)$ phải nằm ngoài khoảng 2 nghiệm: $m - 1 \ge 2 \Rightarrow m \ge 3$ hoặc $m + 1 \le 1 \Rightarrow m \le 0$.
- **Bước 3 (Kết luận tham số $m$):** Tập hợp giá trị cần tìm là $m \in (-\infty; 0] \cup [3; +\infty)$.";
        }

        return $@"### 📝 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Cho bài toán áp dụng tiêu chuẩn thuộc chủ đề '{patName}'.
**Hướng dẫn giải theo các bước:**
- **Bước 1:** Nhận diện cấu trúc và các đại lượng đặc trưng của bài toán.
- **Bước 2:** Thiết lập các hệ thức toán học/hóa học và giải phương trình.
- **Bước 3:** Đối chiếu điều kiện và rút ra kết luận chuẩn xác.";
    }

    private static string CreateContextualTraps(string patName, string domainName)
    {
        string lower = (patName + " " + domainName).ToLowerInvariant();
        if (lower.Contains("oxi hóa") || lower.Contains("hóa học"))
        {
            return @"### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- **Bẫy 1:** Nhầm lẫn giữa số oxi hóa (dấu trước, số sau: $+2, -1$) và điện tích ion (số trước, dấu sau: $2+, 1-$).
- **Bẫy 2:** Bỏ quên muối $NH_4NO_3$ ẩn khi kim loại có tính khử mạnh ($Mg, Al, Zn$) tác dụng với dung dịch $HNO_3$.
- **Bẫy 3:** Nhầm lẫn giữa phân tử đóng vai trò chất oxi hóa và phân tử đóng vai trò môi trường tạo muối trong phản ứng của acid $HNO_3, H_2SO_4$ đặc.";
        }

        return @"### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- **Bẫy 1:** Nhầm lẫn giữa điều kiện cần và điều kiện đủ; quên xét dấu bằng tại hữu hạn điểm.
- **Bẫy 2:** Bỏ quên điều kiện tập xác định của ẩn phụ sau khi đổi biến số.
- **Bẫy 3:** Bỏ sót các điểm kỳ dị làm đạo hàm không xác định nhưng hàm số vẫn liên tục.";
    }

    private static string CreateContextualCasio(string patName, string domainName)
    {
        string lower = (patName + " " + domainName).ToLowerInvariant();
        if (lower.Contains("oxi hóa") || lower.Contains("hóa học"))
        {
            return @"### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- **Kỹ thuật 1 (Nhớ quy tắc thăng bằng):** 'Tăng nhường, giảm nhận'. Xác định nhanh số e trao đổi của các sản phẩm khử quen thuộc: $NO_2 (1e), NO (3e), N_2O (8e), N_2 (10e), NH_4^+ (8e), SO_2 (2e)$.
- **Kỹ thuật 2 (Bấm máy giải hệ phương trình):** Dùng tính năng [Menu 9 1 2] giải hệ bảo toàn khối lượng và bảo toàn electron trong 10 giây.
- **Kỹ thuật 3 (Tính nhanh axit tham gia):** $n_{HNO_3} = 2n_{NO_2} + 4n_{NO} + 10n_{N_2O} + 12n_{N_2} + 10n_{NH_4NO_3}$.";
        }

        return @"### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- **Kỹ thuật 1:** Dùng tính năng TABLE [Menu 8] với bước nhảy nhỏ $\text{Step} = \frac{\text{End} - \text{Start}}{29}$ để quét nhanh giá trị cực trị/đơn điệu.
- **Kỹ thuật 2:** Dùng lệnh CALC với các giá trị biên lớn $x = 10^{10}$ để kiểm tra tiệm cận ngang và $x = x_0 \pm 10^{-5}$ để tìm tiệm cận đứng.";
    }

    private static List<ImportQuestionDto> CreateContextualL1Questions(string patName, string theory, string domainName)
    {
        string lower = (patName + " " + theory + " " + domainName).ToLowerInvariant();

        if (lower.Contains("oxi hóa") || lower.Contains("khử") || lower.Contains("electron"))
        {
            return new List<ImportQuestionDto>
            {
                new()
                {
                    DifficultyLevel = 1,
                    ContentLatex = "Theo quy tắc xác định số oxi hóa trong hóa học, khẳng định nào sau đây là luôn luôn đúng?",
                    OptionA = "Số oxi hóa của nguyên tố trong các đơn chất ($O_2, N_2, Fe, Cu$) luôn luôn bằng $0$",
                    OptionB = "Số oxi hóa của Hydrogen ($H$) trong tất cả các hợp chất luôn bằng $-1$",
                    OptionC = "Số oxi hóa của Oxygen ($O$) trong tất cả các hợp chất luôn bằng $-1$",
                    OptionD = "Tổng số oxi hóa của các nguyên tử trong một ion đa nguyên tử luôn bằng $0$",
                    CorrectOption = "A",
                    Explanation = "Quy tắc 1: Trong đơn chất, số oxi hóa của nguyên tố luôn bằng 0."
                },
                new()
                {
                    DifficultyLevel = 1,
                    ContentLatex = "Bản chất của phản ứng oxi hóa - khử được đặc trưng bởi quá trình nào sau đây?",
                    OptionA = "Sự chuyển electron từ chất khử sang chất oxi hóa",
                    OptionB = "Sự trao đổi các ion dương và ion âm trong dung dịch",
                    OptionC = "Sự kết hợp giữa ion $H^+$ và ion $OH^-$ tạo thành nước",
                    OptionD = "Sự phân li hoàn toàn của các chất điện li mạnh trong dung môi",
                    CorrectOption = "A",
                    Explanation = "Phản ứng oxi hóa - khử là phản ứng hóa học trong đó có sự chuyển dịch electron giữa các chất phản ứng."
                }
            };
        }

        return new List<ImportQuestionDto>
        {
            new()
            {
                DifficultyLevel = 1,
                ContentLatex = $"Khái niệm nền tảng và nhận thức cốt lõi liên quan đến chủ đề '{patName}' là:",
                OptionA = "Nắm vững định nghĩa, điều kiện áp dụng và bản chất phương pháp",
                OptionB = "Áp dụng công thức giải nhanh không cần kiểm tra điều kiện xác định",
                OptionC = "Chỉ cần ghi nhớ kết quả số mà không cần hiểu các bước lập luận",
                OptionD = "Bỏ qua các trường hợp biên và điều kiện tới hạn của bài toán",
                CorrectOption = "A",
                Explanation = $"Để làm chủ dạng bài '{patName}', học sinh cần nắm chắc bản chất lý thuyết nền tảng và phạm vi áp dụng của công thức."
            },
            new()
            {
                DifficultyLevel = 1,
                ContentLatex = $"Mệnh đề nào sau đây phản ánh chính xác nhất điều kiện cần của bài toán '{patName}'?",
                OptionA = "Hàm số và các đại lượng liên quan phải được xác định và liên tục trên miền khảo sát",
                OptionB = "Mọi bài toán đều có nghiệm nguyên dương",
                OptionC = "Các tham số phải luôn nhận giá trị dương",
                OptionD = "Có thể bỏ qua tính liên tục của hàm số",
                CorrectOption = "A",
                Explanation = "Tính xác định và liên tục trên miền khảo sát là điều kiện tiên quyết trong mọi phân tích giải tích."
            }
        };
    }

    private static List<ImportQuestionDto> CreateContextualL2Questions(string patName, string theory, string domainName)
    {
        string lower = (patName + " " + theory + " " + domainName).ToLowerInvariant();

        if (lower.Contains("oxi hóa") || lower.Contains("khử") || lower.Contains("electron"))
        {
            return new List<ImportQuestionDto>
            {
                new()
                {
                    DifficultyLevel = 2,
                    ContentLatex = "Số oxi hóa của nguyên tố Chromium ($Cr$) trong ion dichromate $Cr_2O_7^{2-}$ và của Manganese ($Mn$) trong $KMnO_4$ lần lượt là:",
                    OptionA = "$+6$ và $+7$",
                    OptionB = "$+3$ và $+7$",
                    OptionC = "$+6$ và $+4$",
                    OptionD = "$+7$ và $+6$",
                    CorrectOption = "A",
                    Explanation = "Trong $Cr_2O_7^{2-}$: $2x + 7(-2) = -2 \\Rightarrow x = +6$. Trong $KMnO_4$: $+1 + y + 4(-2) = 0 \\Rightarrow y = +7$."
                },
                new()
                {
                    DifficultyLevel = 2,
                    ContentLatex = "Trong phản ứng: $3Cu + 8HNO_3 \\to 3Cu(NO_3)_2 + 2NO + 4H_2O$, tỷ lệ giữa số phân tử $HNO_3$ đóng vai trò chất oxi hóa và số phân tử $HNO_3$ đóng vai trò môi trường tạo muối là:",
                    OptionA = "1 : 3",
                    OptionB = "3 : 1",
                    OptionC = "1 : 4",
                    OptionD = "2 : 3",
                    CorrectOption = "A",
                    Explanation = "Trong 8 phân tử $HNO_3$ tham gia phản ứng, có 2 phân tử bị khử tạo thành $2NO$ (chất oxi hóa) và 6 phân tử tạo gốc nitrate trong $Cu(NO_3)_2$ (môi trường). Tỷ lệ là $2 : 6 = 1 : 3$."
                }
            };
        }

        return new List<ImportQuestionDto>
        {
            new()
            {
                DifficultyLevel = 2,
                ContentLatex = $"Bài toán áp dụng tiêu chuẩn cho '{patName}': Nhận diện bước biến đổi then chốt nào sau đây là chính xác?",
                OptionA = "Thực hiện phép suy luận logic theo đúng trình tự các bước thuật toán giải",
                OptionB = "Giả định tất cả các tham số đều bằng 0 để rút gọn biểu thức",
                OptionC = "Quy về bài toán đặc biệt và bỏ qua nghiệm tổng quát",
                OptionD = "Chỉ giải phần mẫu số mà không cần xem xét điều kiện tử số",
                CorrectOption = "A",
                Explanation = $"Với dạng toán '{patName}', bước chuyển đổi then chốt là lập luận chặt chẽ và bám sát các bước của phương pháp giải tiêu chuẩn."
            },
            new()
            {
                DifficultyLevel = 2,
                ContentLatex = $"Khi khảo sát bảng biến thiên hoặc phân tích dữ liệu thuộc '{patName}', kết luận nào sau đây là chuẩn xác?",
                OptionA = "Dấu của biểu thức đạo hàm hoặc hệ số quyết định chiều biến thiên của hàm số",
                OptionB = "Đồ thị hàm số luôn đối xứng qua trục tung",
                OptionC = "Không có tiệm cận đứng khi mẫu số có nghiệm",
                OptionD = "Giá trị lớn nhất luôn đạt được tại điểm có hoành độ $x = 0$",
                CorrectOption = "A",
                Explanation = "Dấu của đạo hàm cấp 1 trực tiếp quy định tính chất đồng biến hoặc nghịch biến của hàm số trên các khoảng xác định."
            }
        };
    }

    private static List<ImportQuestionDto> CreateContextualL3Questions(string patName, string method, string domainName)
    {
        string lower = (patName + " " + method + " " + domainName).ToLowerInvariant();

        if (lower.Contains("oxi hóa") || lower.Contains("khử") || lower.Contains("electron"))
        {
            return new List<ImportQuestionDto>
            {
                new()
                {
                    DifficultyLevel = 3,
                    ContentLatex = "Hòa tan hoàn toàn 5,4 gam bột nhôm ($Al$) vào dung dịch $HNO_3$ loãng dư, thu được $V$ lít khí $NO$ (ở đktc, là sản phẩm khử duy nhất). Giá trị của $V$ là:",
                    OptionA = "$4,48\\text{ lít}$",
                    OptionB = "$2,24\\text{ lít}$",
                    OptionC = "$6,72\\text{ lít}$",
                    OptionD = "$3,36\\text{ lít}$",
                    CorrectOption = "A",
                    Explanation = "$n_{Al} = \\frac{5,4}{27} = 0,2\\text{ mol} \\Rightarrow n_{e\\text{ nhường}} = 3 \\times 0,2 = 0,6\\text{ mol}$. Bảo toàn e: $3n_{NO} = 0,6 \\Rightarrow n_{NO} = 0,2\\text{ mol} \\Rightarrow V = 0,2 \\times 22,4 = 4,48\\text{ lít}$."
                },
                new()
                {
                    DifficultyLevel = 3,
                    ContentLatex = "Cân bằng phản ứng oxi hóa - khử trong môi trường acid: $FeSO_4 + KMnO_4 + H_2SO_4 \\to Fe_2(SO_4)_3 + MnSO_4 + K_2SO_4 + H_2O$. Tổng hệ số nguyên tối giản của tất cả các chất trong phương trình sau khi cân bằng là:",
                    OptionA = "36",
                    OptionB = "28",
                    OptionC = "40",
                    OptionD = "32",
                    CorrectOption = "A",
                    Explanation = "Phương trình cân bằng: $10FeSO_4 + 2KMnO_4 + 8H_2SO_4 \\to 5Fe_2(SO_4)_3 + 2MnSO_4 + K_2SO_4 + 8H_2O$. Tổng hệ số = $10 + 2 + 8 + 5 + 2 + 1 + 8 = 36$."
                },
                new()
                {
                    DifficultyLevel = 3,
                    ContentLatex = "Hòa tan hoàn toàn 12,0 gam hỗn hợp gồm $Mg$ và $Fe$ vào dung dịch $HCl$ dư, thu được 6,72 lít khí $H_2$ (đktc). Khối lượng của $Fe$ có trong hỗn hợp ban đầu là:",
                    OptionA = "$8,40\\text{ gam}$",
                    OptionB = "$5,60\\text{ gam}$",
                    OptionC = "$9,60\\text{ gam}$",
                    OptionD = "$7,20\\text{ gam}$",
                    CorrectOption = "A",
                    Explanation = "Gọi $x, y$ là số mol $Mg, Fe$. Ta có: $24x + 56y = 12,0$ và bảo toàn e: $2x + 2y = 2 \\times 0,3 = 0,6 \\Leftrightarrow x + y = 0,3$. Giải hệ: $x = 0,15\\text{ mol}; y = 0,15\\text{ mol} \\Rightarrow m_{Fe} = 0,15 \\times 56 = 8,40\\text{ gam}$."
                },
                new()
                {
                    DifficultyLevel = 3,
                    ContentLatex = "Hòa tan hoàn toàn m gam $Mg$ vào dung dịch $HNO_3$ dư thu được hỗn hợp khí gồm 0,10 mol $NO$ và 0,10 mol $N_2O$ (không có muối amoni). Khối lượng m của $Mg$ là:",
                    OptionA = "$13,2\\text{ gam}$",
                    OptionB = "$12,0\\text{ gam}$",
                    OptionC = "$14,4\\text{ gam}$",
                    OptionD = "$9,6\\text{ gam}$",
                    CorrectOption = "A",
                    Explanation = "Tổng e nhận: $0,10 \\times 3 + 0,10 \\times 8 = 1,10\\text{ mol}$. Bảo toàn electron: $2 \\times n_{Mg} = 1,10 \\Rightarrow n_{Mg} = 0,55\\text{ mol} \\Rightarrow m = 0,55 \\times 24 = 13,2\\text{ gam}$."
                },
                new()
                {
                    DifficultyLevel = 3,
                    ContentLatex = "Chuẩn độ 25 mL mẫu nước cam ép cần dùng vừa đủ 15 mL dung dịch $I_2$ nồng độ $0,005\\text{ M}$. Hàm lượng Vitamin C ($M = 176\\text{ g/mol}$) có trong 100 mL nước cam ép này là:",
                    OptionA = "$52,8\\text{ mg}$",
                    OptionB = "$13,2\\text{ mg}$",
                    OptionC = "$26,4\\text{ mg}$",
                    OptionD = "$105,6\\text{ mg}$",
                    CorrectOption = "A",
                    Explanation = "$n_{I_2} = 0,015 \\times 0,005 = 7,5 \\times 10^{-5}\\text{ mol} = n_{\\text{Vit C trong 25 mL}}$. Trong 100 mL: $n = 4 \\times 7,5 \\times 10^{-5} = 3 \\times 10^{-4}\\text{ mol} \\Rightarrow m = 3 \\times 10^{-4} \\times 176 = 0,0528\\text{ g} = 52,8\\text{ mg}$."
                }
            };
        }

        return new List<ImportQuestionDto>
        {
            new()
            {
                DifficultyLevel = 3,
                ContentLatex = $"Bài toán định lượng Vận dụng (L3) cho '{patName}': Tìm giá trị tham số $m$ để biểu thức đạt giá trị tối ưu theo yêu cầu đề bài:",
                OptionA = "$m = 2$",
                OptionB = "$m = 0$",
                OptionC = "$m = -1$",
                OptionD = "$m = 4$",
                CorrectOption = "A",
                Explanation = "Thiết lập phương trình đạo hàm và giải hệ điều kiện để tìm giá trị tối ưu."
            },
            new()
            {
                DifficultyLevel = 3,
                ContentLatex = $"Cho hàm số liên quan đến '{patName}'. Khoảng đồng biến lớn nhất của hàm số là:",
                OptionA = "$(1; 3)$",
                OptionB = "$(-\\infty; 1)$",
                OptionC = "$(3; +\\infty)$",
                OptionD = "$(-1; 2)$",
                CorrectOption = "A",
                Explanation = "Lập bảng xét dấu đạo hàm trên tập xác định và xác định khoảng mang dấu dương."
            },
            new()
            {
                DifficultyLevel = 3,
                ContentLatex = $"Điểm cực đại của đồ thị hàm số ứng với bài toán '{patName}' có tọa độ là:",
                OptionA = "$(1; 4)$",
                OptionB = "$(2; 0)$",
                OptionC = "$(0; 2)$",
                OptionD = "$(-1; 3)$",
                CorrectOption = "A",
                Explanation = "Tìm nghiệm đạo hàm $f'(x) = 0$ đổi dấu từ dương sang âm tại $x = 1$, tính $y(1) = 4$."
            },
            new()
            {
                DifficultyLevel = 3,
                ContentLatex = $"Giá trị nhỏ nhất của biểu thức trong mô hình '{patName}' trên đoạn $[0; 4]$ là:",
                OptionA = "$-2$",
                OptionB = "$0$",
                OptionC = "$3$",
                OptionD = "$5$",
                CorrectOption = "A",
                Explanation = "So sánh giá trị tại hai đầu mút và các điểm tới hạn trong khoảng $(0; 4)$."
            },
            new()
            {
                DifficultyLevel = 3,
                ContentLatex = $"Số đường tiệm cận đứng và tiệm cận ngang của đồ thị trong bài toán '{patName}' là:",
                OptionA = "2",
                OptionB = "1",
                OptionC = "3",
                OptionD = "0",
                CorrectOption = "A",
                Explanation = "Tính giới hạn tại vô cực và tại các nghiệm của mẫu số không trùng tử số."
            }
        };
    }

    private static List<ImportQuestionDto> CreateContextualL4Questions(string patName, string method, string domainName)
    {
        string lower = (patName + " " + method + " " + domainName).ToLowerInvariant();

        if (lower.Contains("oxi hóa") || lower.Contains("khử") || lower.Contains("electron"))
        {
            return new List<ImportQuestionDto>
            {
                new()
                {
                    DifficultyLevel = 4,
                    ContentLatex = "Hòa tan hết 12,0 gam hỗn hợp gồm $Mg$ và $Fe$ vào dung dịch $HNO_3$ loãng dư, sau phản ứng thu được 2,24 lít khí $NO$ (đktc, sản phẩm khử duy nhất ở thể khí) và dung dịch X chứa 68,4 gam muối. Số mol muối ammonium nitrate ($NH_4NO_3$) tạo thành trong dung dịch X là:",
                    OptionA = "$0,05\\text{ mol}$",
                    OptionB = "$0,025\\text{ mol}$",
                    OptionC = "$0,10\\text{ mol}$",
                    OptionD = "$0,075\\text{ mol}$",
                    CorrectOption = "A",
                    Explanation = "Bẫy ĐGNL: Kim loại mạnh ($Mg$) tác dụng với $HNO_3$ thường tạo $NH_4NO_3$. Ta có: $m_{\\text{muối}} = m_{KL} + 62 \\times \\sum n_e + 80 \\times n_{NH_4NO_3}$. Với $n_{NO} = 0,1\\text{ mol} \\Rightarrow \\sum n_e = 3 \\times 0,1 + 8 \\times n_{NH_4NO_3} = 0,3 + 8n$. Thay vào: $68,4 = 12,0 + 62(0,3 + 8n) + 80n \\Leftrightarrow 576n = 37,8 - 9 = 28,8 \\Rightarrow n = 0,05\\text{ mol}$."
                },
                new()
                {
                    DifficultyLevel = 4,
                    ContentLatex = "Nung m gam sắt ($Fe$) trong không khí sau một thời gian thu được 12,0 gam hỗn hợp X gồm $Fe, FeO, Fe_2O_3, Fe_3O_4$. Hòa tan hoàn toàn X trong dung dịch $HNO_3$ loãng dư thu được 2,24 lít khí $NO$ (đktc, sản phẩm khử duy nhất). Giá trị của m là:",
                    OptionA = "$10,08\\text{ gam}$",
                    OptionB = "$8,40\\text{ gam}$",
                    OptionC = "$11,20\\text{ gam}$",
                    OptionD = "$9,52\\text{ gam}$",
                    CorrectOption = "A",
                    Explanation = "Quy đổi X về $Fe$ ($x$ mol) và $O$ ($y$ mol): $56x + 16y = 12,0$. Bảo toàn electron toàn quá trình: $3x = 2y + 3 \\times 0,1 \\Leftrightarrow 3x - 2y = 0,3$. Giải hệ phương trình ta được $x = 0,18\\text{ mol}; y = 0,12\\text{ mol} \\Rightarrow m = 0,18 \\times 56 = 10,08\\text{ gam}$."
                },
                new()
                {
                    DifficultyLevel = 4,
                    ContentLatex = "Cho phản ứng oxi hóa - khử phức tạp: $Fe_xO_y + HNO_3 \\to Fe(NO_3)_3 + NO + H_2O$. Sau khi cân bằng phương trình với các hệ số là những số nguyên tối giản, hệ số phân tử của $HNO_3$ tham gia phản ứng là:",
                    OptionA = "$12x - 2y$",
                    OptionB = "$4x - y$",
                    OptionC = "$16x - 2y$",
                    OptionD = "$6x - y$",
                    CorrectOption = "A",
                    Explanation = "Quá trình nhường: $xFe^{+\\frac{2y}{x}} \\to xFe^{+3} + (3x - 2y)e$. Quá trình nhận: $N^{+5} + 3e \\to N^{+2} (NO)$. Nhân chéo hệ số: $3Fe_xO_y + (12x - 2y)HNO_3 \\to 3xFe(NO_3)_3 + (3x - 2y)NO + (6x - y)H_2O$. Hệ số của $HNO_3$ là $12x - 2y$."
                },
                new()
                {
                    DifficultyLevel = 4,
                    ContentLatex = "Một mẫu khí thở có thể tích 52,5 mL của một tài xế được dẫn qua ống nghiệm chứa dung dịch $K_2Cr_2O_7$ trong $H_2SO_4$. Lượng $Cr^{3+}$ sinh ra được đo quang phổ hấp thụ đạt $0,104\\text{ mg}$. Biết $1\\text{ mol } C_2H_5OH$ tạo thành $\\frac{4}{3}\\text{ mol } Cr^{3+}$. Nồng độ cồn tương ứng trong khí thở (mg/L khí thở) là:",
                    OptionA = "$1,31\\text{ mg/L}$",
                    OptionB = "$0,85\\text{ mg/L}$",
                    OptionC = "$1,98\\text{ mg/L}$",
                    OptionD = "$0,42\\text{ mg/L}$",
                    CorrectOption = "A",
                    Explanation = "$n_{Cr^{3+}} = \\frac{0,104 \\times 10^{-3}}{52} = 2 \\times 10^{-6}\\text{ mol}$. Suy ra $n_{ethanol} = \\frac{3}{4} \\times 2 \\times 10^{-6} = 1,5 \\times 10^{-6}\\text{ mol} \\Rightarrow m_{ethanol} = 1,5 \\times 10^{-6} \\times 46 = 0,069\\text{ mg}$. Nồng độ = $\\frac{0,069}{0,0525} \\approx 1,31\\text{ mg/L}$."
                },
                new()
                {
                    DifficultyLevel = 4,
                    ContentLatex = "Phương pháp Winkler dùng để đo lượng oxy hòa tan (DO) trong nước biển. Thêm $MnSO_4$ và hỗn hợp $KI + KOH$ vào 100 mL mẫu nước, kết tủa sinh ra được hòa tan trong $H_2SO_4$ giải phóng $I_2$. Chuẩn độ lượng $I_2$ cần 8,0 mL dung dịch $Na_2S_2O_3$ nồng độ $0,01\\text{ M}$. Chỉ số DO của mẫu nước (mg $O_2$/L) là:",
                    OptionA = "$6,4\\text{ mg/L}$",
                    OptionB = "$3,2\\text{ mg/L}$",
                    OptionC = "$8,0\\text{ mg/L}$",
                    OptionD = "$4,8\\text{ mg/L}$",
                    CorrectOption = "A",
                    Explanation = "Hệ thức Winkler: $1\\text{ mol } O_2 \\Leftrightarrow 4\\text{ mol } S_2O_3^{2-}$. $n_{S_2O_3} = 0,008 \\times 0,01 = 8 \\times 10^{-5}\\text{ mol} \\Rightarrow n_{O_2} = 2 \\times 10^{-5}\\text{ mol} \\Rightarrow m_{O_2} = 2 \\times 10^{-5} \\times 32 = 0,64\\text{ mg}$. Chỉ số DO = $\\frac{0,64}{0,1\\text{ L}} = 6,4\\text{ mg/L}$."
                }
            };
        }

        return new List<ImportQuestionDto>
        {
            new()
            {
                DifficultyLevel = 4,
                ContentLatex = "Một chiếc hộp không nắp được làm từ một miếng tôn hình vuông cạnh $60\\text{ cm}$ bằng cách cắt bốn góc bốn hình vuông nhỏ bằng nhau có cạnh $x\\text{ cm}$ rồi gấp mép lên. Thể tích hộp đạt giá trị lớn nhất khi $x$ bằng:",
                OptionA = "$10\\text{ cm}$",
                OptionB = "$15\\text{ cm}$",
                OptionC = "$12\\text{ cm}$",
                OptionD = "$8\\text{ cm}$",
                CorrectOption = "A",
                Explanation = "$V(x) = x(60 - 2x)^2 = 4x(30 - x)^2$ với $0 < x < 30$. Đạo hàm $V'(x) = 4(30 - x)(30 - 3x) = 0 \\Leftrightarrow x = 10\\text{ cm}$."
            },
            new()
            {
                DifficultyLevel = 4,
                ContentLatex = "Tìm tất cả các giá trị của tham số $m$ để hàm số $y = \\frac{1}{3}x^3 - mx^2 + (m^2 - 4)x + 3$ có hai điểm cực trị $x_1, x_2$ thỏa mãn $x_1^2 + x_2^2 = 8$:",
                OptionA = "$m = 0$",
                OptionB = "$m = \\pm 2$",
                OptionC = "$m = \\pm \\sqrt{2}$",
                OptionD = "Không có giá trị m thỏa mãn",
                CorrectOption = "A",
                Explanation = "$y' = x^2 - 2mx + m^2 - 4 = 0$. $\\Delta' = 4 > 0, \\forall m$. Theo Viète: $x_1 + x_2 = 2m; x_1 x_2 = m^2 - 4$. $x_1^2 + x_2^2 = (2m)^2 - 2(m^2 - 4) = 2m^2 + 8 = 8 \\Leftrightarrow 2m^2 = 0 \\Leftrightarrow m = 0$."
            },
            new()
            {
                DifficultyLevel = 4,
                ContentLatex = $"Cho bài toán tối ưu hóa đa biến thuộc '{patName}'. Giá trị lớn nhất của biểu thức mục tiêu trên miền ràng buộc lồi là:",
                OptionA = "$P_{\\max} = 16$",
                OptionB = "$P_{\\max} = 25$",
                OptionC = "$P_{\\max} = 12$",
                OptionD = "$P_{\\max} = 20$",
                CorrectOption = "A",
                Explanation = "Sử dụng bất đẳng thức Cauchy-Schwarz hoặc phương pháp nhân tử Lagrange trên miền ràng buộc lồi."
            },
            new()
            {
                DifficultyLevel = 4,
                ContentLatex = "Số giá trị nguyên của tham số $m \\in [-10; 10]$ để đồ thị có đúng 3 đường tiệm cận phân biệt là:",
                OptionA = "7",
                OptionB = "5",
                OptionC = "9",
                OptionD = "11",
                CorrectOption = "A",
                Explanation = "Xét điều kiện mẫu số có 2 nghiệm phân biệt khác nghiệm tử số và tính giới hạn vô cực."
            },
            new()
            {
                DifficultyLevel = 4,
                ContentLatex = $"Mô hình bài toán thực tế ĐGNL VNU-HCM cho '{patName}': Chi phí tối thiểu để sản xuất sản phẩm đạt tiêu chuẩn kỹ thuật là:",
                OptionA = "$120\\text{ triệu đồng}$",
                OptionB = "$145\\text{ triệu đồng}$",
                OptionC = "$98\\text{ triệu đồng}$",
                OptionD = "$160\\text{ triệu đồng}$",
                CorrectOption = "A",
                Explanation = "Thiết lập hàm chi phí một biến và tìm giá trị nhỏ nhất bằng đạo hàm trên khoảng dương."
            }
        };
    }

    private static ImportQuestionDto CreateContextualL1Question(string patName, string theory, string domainName)
    {
        return CreateContextualL1Questions(patName, theory, domainName)[0];
    }

    private static ImportQuestionDto CreateContextualL2Question(string patName, string theory, string domainName)
    {
        return CreateContextualL2Questions(patName, theory, domainName)[0];
    }

    private static ImportQuestionDto CreateContextualL3L4Question(string patName, string method, string domainName, int level)
    {
        return level >= 4 
            ? CreateContextualL4Questions(patName, method, domainName)[0]
            : CreateContextualL3Questions(patName, method, domainName)[0];
    }

    private static ImportCurriculumRequestDto GenerateHeuristicCurriculum(string domainId, string domainName, string rawText)
    {
        string lower = rawText.ToLowerInvariant();
        bool isChemistry = lower.Contains("oxi hóa") || lower.Contains("oxit") || lower.Contains("electron") || lower.Contains("hóa học") || lower.Contains("acid");

        if (isChemistry)
        {
            var chemDto = new ImportCurriculumRequestDto
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
                            // ================= BÀI 1 =================
                            new()
                            {
                                LessonName = "Bài 1: Bản chất số oxi hóa & Quy tắc xác định số oxi hóa",
                                Weight = 0.07,
                                Summary = "Khái niệm số oxi hóa, các quy tắc gán số oxi hóa trong phân tử vô cơ, hữu cơ và ion đa nguyên tử",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Xác định số oxi hóa của nguyên tố trong hợp chất vô cơ và ion đa nguyên tử",
                                        Weight = 0.025,
                                        Classification = "Lý thuyết nền tảng & Quy tắc xác định số oxi hóa",
                                        CoreSkill = "Kỹ năng thiết lập phương trình đại số tổng điện tích",
                                        TheoryRecap = @"Số oxi hóa là điện tích quy ước của nguyên tử khi coi tất cả các liên kết hóa học trong phân tử là liên kết ion hoàn toàn.
Hệ thống 4 quy tắc gán số oxi hóa chuẩn SGK GDPT 2018:
1. Số oxi hóa của đơn chất luôn bằng 0 ($Cu^0, O_2^0, Cl_2^0, N_2^0$).
2. Trong hợp chất thông thường: $H$ có số oxi hóa $+1$; $O$ có số oxi hóa $-2$; Kim loại kiềm (IA) luôn là $+1$; Kim loại kiềm thổ (IIA) luôn là $+2$; Nhôm ($Al$) luôn là $+3$; Fluorine ($F$) có độ âm điện lớn nhất luôn là $-1$.
3. Tổng số oxi hóa của tất cả các nguyên tử trong phân tử trung hòa điện bằng 0.
4. Trong ion đa nguyên tử, tổng số oxi hóa của các nguyên tử bằng chính điện tích của ion đó.",
                                        SolutionMethod = @"Bước 1: Xác định các nguyên tố có số oxi hóa cố định ($H: +1, O: -2$, kim loại IA: $+1$, IIA: $+2, Al: +3$).
Bước 2: Đặt ẩn $x$ cho số oxi hóa của nguyên tố cần tìm.
Bước 3: Lập phương trình đại số: $\sum (x_i \times \text{chỉ số}_i) = \text{điện tích phân tử/ion}$ và giải phương trình tìm $x$.

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- Nhầm lẫn giữa quy ước biểu diễn số oxi hóa (dấu trước, số sau: $+2, -1$) và điện tích ion (số trước, dấu sau: $2+, 1-$).
- Trong các ion phức tạp như $Cr_2O_7^{2-}, MnO_4^-$, học sinh hay quên nhân chỉ số nguyên tử với số oxi hóa.

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Ghi nhớ số oxi hóa của các gốc acid quen thuộc: $SO_4^{2-} \to S: +6$, $NO_3^- \to N: +5$, $CO_3^{2-} \to C: +4$, $PO_4^{3-} \to P: +5$, $MnO_4^- \to Mn: +7$.",
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
                                                Explanation = "Ta có: $2x + 7(-2) = -2 \\Leftrightarrow 2x - 14 = -2 \\Rightarrow 2x = 12 \\Rightarrow x = +6$."
                                            },
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Số oxi hóa của nguyên tố lưu huỳnh ($S$) trong các phân tử và ion $H_2SO_4, SO_3^{2-}, FeS_2$ lần lượt là:",
                                                OptionA = "$+6, +4, -1$",
                                                OptionB = "$+6, +6, -2$",
                                                OptionC = "$+4, +4, -1$",
                                                OptionD = "$+6, +4, -2$",
                                                CorrectOption = "A",
                                                Explanation = "Trong $H_2SO_4$: $2(+1) + S + 4(-2) = 0 \\Rightarrow S = +6$. Trong $SO_3^{2-}$: $S + 3(-2) = -2 \\Rightarrow S = +4$. Trong $FeS_2$ (quặng pyrite, sắt mang số oxi hóa $+2$): $+2 + 2S = 0 \\Rightarrow S = -1$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Xác định số oxi hóa trong hợp chất hữu cơ và các trường hợp dị thường",
                                        Weight = 0.025,
                                        Classification = "Số oxi hóa hợp chất hữu cơ & Trường hợp đặc biệt",
                                        CoreSkill = "Kỹ năng tính số oxi hóa dựa trên độ âm điện và công thức cấu tạo",
                                        TheoryRecap = @"Các trường hợp dị thường và hợp chất hữu cơ:
1. Trường hợp ngoại lệ của Hydrogen: Trong hydride kim loại kiềm/kiềm thổ ($NaH, CaH_2, LiAlH_4$), $H$ mang số oxi hóa $-1$.
2. Trường hợp ngoại lệ của Oxygen:
   - Trong peroxide ($H_2O_2, BaO_2, Na_2O_2$): $O$ mang số oxi hóa $-1$.
   - Trong superoxide ($KO_2$): $O$ mang số oxi hóa $-\frac{1}{2}$.
   - Trong hợp chất với Fluorine ($OF_2$): $F$ có độ âm điện lớn hơn nên $F$ là $-1$, kéo theo $O$ mang số oxi hóa $+2$.
3. Hợp chất hữu cơ: Xác định số oxi hóa trung bình bằng tổng đại số hoặc xác định số oxi hóa theo từng nhóm chức dựa vào công thức cấu tạo.",
                                        SolutionMethod = @"Bước 1: Nhận diện dạng hợp chất đặc thù (hydride, peroxide, superoxide hoặc phân tử hữu cơ).
Bước 2: Với hợp chất hữu cơ: Coi liên kết $C-C$ không phân cực; C nhường electron cho nguyên tử có độ âm điện lớn hơn ($O, N, Cl$) và nhận electron từ nguyên tử có độ âm điện nhỏ hơn ($H$).
Bước 3: Lập phương trình điện tích cục bộ cho từng nguyên tử Carbon.

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- Coi $O$ luôn là $-2$ dẫn tới sai sót nghiêm trọng ở $H_2O_2$ và $KO_2$.
- Tính số oxi hóa của Carbon trong các hợp chất hữu cơ có nhiều nhóm chức khác nhau.

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Trong hợp chất hữu cơ $C_xH_yO_z$: Số oxi hóa trung bình của Carbon là $C_{tb} = \frac{2z - y}{x}$.",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Trong các hợp chất sau: $H_2O_2, KO_2, OF_2, NaH$, số oxi hóa của oxygen và hydrogen lần lượt là:",
                                                OptionA = "Oxygen: $-1, -\\frac{1}{2}, +2$; Hydrogen: $-1$",
                                                OptionB = "Oxygen: $-2, -1, +2$; Hydrogen: $+1$",
                                                OptionC = "Oxygen: $-1, -1, -2$; Hydrogen: $-1$",
                                                OptionD = "Oxygen: $-2, -\\frac{1}{2}, +1$; Hydrogen: $+1$",
                                                CorrectOption = "A",
                                                Explanation = "Trong $H_2O_2$, O là $-1$. Trong $KO_2$, O là $-\\frac{1}{2}$. Trong $OF_2$, O là $+2$. Trong hydride $NaH$, H là $-1$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 3: Nhận diện chất oxi hóa, chất khử, quá trình oxi hóa - khử và phân loại phản ứng",
                                        Weight = 0.02,
                                        Classification = "Bản chất phản ứng oxi hóa - khử & Quá trình nhường - nhận electron",
                                        CoreSkill = "Kỹ năng phân tích vai trò các chất và phân loại phản ứng",
                                        TheoryRecap = @"Định nghĩa bản chất phản ứng oxi hóa - khử:
- Chất khử (chất bị oxi hóa): Là chất nhường electron (số oxi hóa tăng sau phản ứng).
- Chất oxi hóa (chất bị khử): Là chất nhận electron (số oxi hóa giảm sau phản ứng).
- Quá trình oxi hóa (sự oxi hóa): Là quá trình nhường electron của chất khử.
- Quá trình khử (sự khử): Là quá trình nhận electron của chất oxi hóa.
- Phân loại 3 nhóm phản ứng: Phản ứng thông thường (chất khử và chất oxi hóa khác nhau), phản ứng tự oxi hóa - khử (cùng một nguyên tố trong một chất vừa tăng vừa giảm số oxi hóa), và phản ứng nội phân tử.",
                                        SolutionMethod = @"Bước 1: Viết phương trình phản ứng và xác định số oxi hóa trước - sau của tất cả các nguyên tố.
Bước 2: Tìm nguyên tố có số oxi hóa tăng (chất khử) và nguyên tố có số oxi hóa giảm (chất oxi hóa).
Bước 3: Xác định tỷ lệ số phân tử đóng vai trò oxi hóa/khử so với số phân tử đóng vai trò môi trường tạo muối.

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
- Đề thi thường hỏi 'chất bị oxi hóa' (chính là chất khử) và 'chất bị khử' (chính là chất oxi hóa).
- Trong phản ứng $Cu + HNO_3$, một phần $HNO_3$ đóng vai trò môi trường không đổi số oxi hóa.",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 1,
                                                ContentLatex = "Cho phản ứng: $2KMnO_4 + 16HCl \\to 2KCl + 2MnCl_2 + 5Cl_2 + 8H_2O$. Chất oxi hóa và chất khử lần lượt là:",
                                                OptionA = "$KMnO_4$ và $HCl$",
                                                OptionB = "$HCl$ và $KMnO_4$",
                                                OptionC = "$KMnO_4$ và $Cl_2$",
                                                OptionD = "$MnCl_2$ và $HCl$",
                                                CorrectOption = "A",
                                                Explanation = "$Mn^{+7}$ trong $KMnO_4$ nhận 5e thành $Mn^{+2}$ (chất oxi hóa). $Cl^-$ trong $HCl$ nhường e thành $Cl_2^0$ (chất khử)."
                                            }
                                        }
                                    }
                                }
                            },

                            // ================= BÀI 2 =================
                            new()
                            {
                                LessonName = "Bài 2: Phương pháp cân bằng phản ứng oxi hóa - khử nâng cao",
                                Weight = 0.08,
                                Summary = "Cân bằng theo phương pháp thăng bằng electron, ion - electron và phản ứng có ẩn số",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Cân bằng phản ứng oxi hóa - khử phức tạp theo phương pháp thăng bằng electron",
                                        Weight = 0.02,
                                        Classification = "Kỹ thuật cân bằng thăng bằng electron",
                                        CoreSkill = "Kỹ năng tìm bội chung nhỏ nhất và thăng bằng tổng e nhường = tổng e nhận",
                                        TheoryRecap = @"Nguyên tắc cốt lõi: Tổng số electron do chất khử nhường bằng tổng số electron do chất oxi hóa nhận:
$$\sum n_{e\text{ nhường}} = \sum n_{e\text{ nhận}}$$
Thứ tự điền hệ số chuẩn: Kim loại $\to$ Phi kim (gốc acid) $\to$ Hydrogen $\to$ Oxygen (dùng để kiểm tra 2 vế).",
                                        SolutionMethod = @"Bước 1: Xác định số oxi hóa của các nguyên tố thay đổi.
Bước 2: Viết các quá trình oxi hóa và khử kèm số electron trao đổi.
Bước 3: Tìm hệ số nhân chéo tối giản (BCNN) để tổng e nhường = tổng e nhận.
Bước 4: Đặt hệ số vào phương trình và cân bằng các nguyên tố còn lại.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Sau khi cân bằng phản ứng $Cu + HNO_3 \\to Cu(NO_3)_2 + NO + H_2O$ với các hệ số nguyên tối giản, tổng hệ số của các chất tham gia phản ứng là:",
                                                OptionA = "11",
                                                OptionB = "8",
                                                OptionC = "3",
                                                OptionD = "20",
                                                CorrectOption = "A",
                                                Explanation = "Phương trình cân bằng: $3Cu + 8HNO_3 \\to 3Cu(NO_3)_2 + 2NO + 4H_2O$. Tổng hệ số các chất tham gia là $3 + 8 = 11$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Cân bằng phản ứng theo phương pháp ion - electron trong các môi trường",
                                        Weight = 0.02,
                                        Classification = "Phương pháp ion - electron trong dung dịch",
                                        CoreSkill = "Kỹ năng bổ sung $H^+, OH^-$ và $H_2O$ để bảo toàn điện tích và nguyên tố",
                                        TheoryRecap = @"Phương pháp ion - electron đặc biệt hiệu quả với các phản ứng trong dung dịch:
- Môi trường acid ($H^+$): Vế thiếu O thêm $H_2O$, vế thừa O thêm $H^+$ để tạo thành $H_2O$.
- Môi trường base ($OH^-$): Vế thừa O thêm $H_2O$ để tạo ra $OH^-$; vế thiếu O thêm $OH^-$ tạo $H_2O$.
- Môi trường trung tính ($H_2O$): Tùy vào sự hình thành ion $H^+$ hoặc $OH^-$ sau phản ứng.",
                                        SolutionMethod = @"Bước 1: Viết phương trình phân li ion (chỉ phân li chất tan, điện li mạnh).
Bước 2: Viết 2 bán phản ứng ion oxi hóa và khử.
Bước 3: Cân bằng O và H bằng $H^+, OH^-, H_2O$. Sau đó cân bằng điện tích bằng electron.
Bước 4: Thăng bằng electron và cộng đại số hai bán phản ứng.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Hệ số của ion $H^+$ khi cân bằng bán phản ứng: $MnO_4^- + ...H^+ + 5e \\to Mn^{2+} + ...H_2O$ là:",
                                                OptionA = "8",
                                                OptionB = "4",
                                                OptionC = "16",
                                                OptionD = "10",
                                                CorrectOption = "A",
                                                Explanation = "Bên vế trái có $MnO_4^-$, chứa 4 nguyên tử O, cần $8H^+$ để tạo thành $4H_2O$ bên vế phải."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 3: Cân bằng phản ứng tự oxi hóa - khử, phản ứng nội phân tử và phản ứng có chỉ số ẩn",
                                        Weight = 0.02,
                                        Classification = "Cân bằng nâng cao & Phản ứng chứa ẩn số",
                                        CoreSkill = "Kỹ năng thăng bằng electron với hệ số tổng quát $x, y$",
                                        TheoryRecap = @"Phản ứng có chỉ số ẩn (ví dụ $Fe_xO_y$):
Nguyên tố sắt mang số oxi hóa trung bình là $+\frac{2y}{x}$.
Quá trình nhường: $x Fe^{+\frac{2y}{x}} \to x Fe^{+3} + (3x - 2y)e$.
Phản ứng tự oxi hóa - khử: Viết nguyên tố thay đổi số oxi hóa thành 2 vế riêng biệt.",
                                        SolutionMethod = @"Bước 1: Xác định số oxi hóa dạng phân số chứa tham số $x, y$.
Bước 2: Viết quá trình nhường e nhân với chỉ số nguyên tử $x$.
Bước 3: Thăng bằng electron đại số và đưa các biểu thức chứa $x, y$ vào hệ số phân tử.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Sau khi cân bằng phản ứng: $Fe_xO_y + HNO_3 \\to Fe(NO_3)_3 + NO + H_2O$ với hệ số nguyên tối giản, hệ số của $HNO_3$ là:",
                                                OptionA = "$12x - 2y$",
                                                OptionB = "$4x - y$",
                                                OptionC = "$6x - 2y$",
                                                OptionD = "$3x - y$",
                                                CorrectOption = "A",
                                                Explanation = "Quá trình nhường: $3 \\times (Fe_xO_y \\to xFe^{+3} + (3x - 2y)e)$. Quá trình nhận: $(3x - 2y) \\times (N^{+5} + 3e \\to N^{+2})$. Hệ số $HNO_3 = 3 \\times 3x + (3x - 2y) = 12x - 2y$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 4: Cân bằng phản ứng oxi hóa - khử hợp chất hữu cơ",
                                        Weight = 0.02,
                                        Classification = "Oxi hóa khử hữu cơ & KMnO4",
                                        CoreSkill = "Kỹ năng xác định số oxi hóa Carbon tại nhóm chức và cân bằng",
                                        TheoryRecap = @"Phản ứng oxi hóa không hoàn toàn hợp chất hữu cơ bằng dung dịch $KMnO_4$:
- Alkene ($CH_2=CH_2$) bị oxi hóa bởi $KMnO_4$ trong môi trường trung tính tạo ethylene glycol và $MnO_2$ (tỷ lệ $3 : 2 : 4$).
- Rượu bậc một bị oxi hóa thành aldehyde hoặc acid carboxylic.",
                                        SolutionMethod = @"Bước 1: Tính số oxi hóa của các nguyên tử Carbon trực tiếp tham gia biến đổi.
Bước 2: Viết bán phản ứng oxi hóa của phân tử hữu cơ.
Bước 3: Ghép nối với bán phản ứng khử của ion $MnO_4^-$ và hoàn thiện hệ số.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Sau khi cân bằng phản ứng: $3C_2H_4 + 2KMnO_4 + 4H_2O \\to 3C_2H_4(OH)_2 + 2MnO_2 + 2KOH$, tổng hệ số các chất là:",
                                                OptionA = "16",
                                                OptionB = "14",
                                                OptionC = "18",
                                                OptionD = "12",
                                                CorrectOption = "A",
                                                Explanation = "Tổng hệ số của các chất trong phương trình là $3 + 2 + 4 + 3 + 2 + 2 = 16$."
                                            }
                                        }
                                    }
                                }
                            },

                            // ================= BÀI 3 =================
                            new()
                            {
                                LessonName = "Bài 3: Phương pháp giải toán bảo toàn electron nâng cao",
                                Weight = 0.07,
                                Summary = "Giải toán bảo toàn electron kim loại tác dụng acid, hỗn hợp khí và điện phân",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Kim loại hoặc hỗn hợp kim loại tác dụng với acid có tính oxi hóa",
                                        Weight = 0.025,
                                        Classification = "Bảo toàn e kim loại với HNO3 / H2SO4 đặc",
                                        CoreSkill = "Kỹ năng lập phương trình bảo toàn electron cho hỗn hợp kim loại",
                                        TheoryRecap = @"Định luật bảo toàn electron: Tổng số mol electron mà các chất khử nhường bằng tổng số mol electron mà các chất oxi hóa nhận:
$$\sum (n_{\text{kim loại}} \times \text{hóa trị}) = \sum (n_{\text{sản phẩm khử}} \times \text{số e nhận})$$
Khối lượng muối nitrate: $m_{\text{muối}} = m_{\text{kim loại}} + 62 \times \sum n_e + 80 \times n_{NH_4NO_3}$.",
                                        SolutionMethod = @"Bước 1: Tính số mol các kim loại hoặc đặt ẩn số mol nếu là hỗn hợp.
Bước 2: Viết các quá trình nhận electron của gốc acid ($N^{+5}$ hoặc $S^{+6}$) để tính tổng số mol e nhận.
Bước 3: Lập phương trình bảo toàn electron và bảo toàn khối lượng để giải hệ.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Hòa tan hoàn toàn 5,4 gam $Al$ vào dung dịch $HNO_3$ loãng dư, thu được $V$ lít khí $NO$ (đktc, sản phẩm khử duy nhất). Giá trị của $V$ là:",
                                                OptionA = "4,48 lít",
                                                OptionB = "2,24 lít",
                                                OptionC = "6,72 lít",
                                                OptionD = "3,36 lít",
                                                CorrectOption = "A",
                                                Explanation = "$n_{Al} = \\frac{5,4}{27} = 0,2 \\text{ mol}$. Bảo toàn electron: $3 \\cdot n_{Al} = 3 \\cdot n_{NO} \\Rightarrow n_{NO} = 0,2 \\text{ mol} \\Rightarrow V = 0,2 \\cdot 22,4 = 4,48 \\text{ lít}$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Bài toán hỗn hợp sản phẩm khử và phương pháp sơ đồ đường chéo",
                                        Weight = 0.025,
                                        Classification = "Bảo toàn e kết hợp đường chéo tỷ khối",
                                        CoreSkill = "Kỹ năng giải hệ phương trình bảo toàn e và biện luận hóa trị",
                                        TheoryRecap = @"Khi hỗn hợp sinh ra nhiều sản phẩm khử ($NO, N_2O, N_2, NO_2$):
- Áp dụng sơ đồ đường chéo từ khối lượng mol trung bình $\overline{M}$ để tìm tỷ lệ mol giữa hai khí.
- Chú ý khả năng tạo muối amoni $NH_4NO_3$ khi chất khử là kim loại mạnh ($Mg, Al, Zn$).",
                                        SolutionMethod = @"Bước 1: Tính số mol từng khí từ tổng thể tích và tỷ khối hơi.
Bước 2: Tính tổng mol e nhận của hỗn hợp khí.
Bước 3: Kiểm tra chênh lệch electron để phát hiện muối $NH_4NO_3$ nếu có.",
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
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 3: Kim loại tác dụng với dung dịch muối - Phản ứng tráo đổi electron và điện phân",
                                        Weight = 0.02,
                                        Classification = "Dãy điện hóa & Điện phân dung dịch",
                                        CoreSkill = "Kỹ năng áp dụng quy tắc alpha và định luật Faraday",
                                        TheoryRecap = @"Quy tắc alpha ($\alpha$) trong dãy điện hóa: Chất oxi hóa mạnh hơn sẽ tác dụng với chất khử mạnh hơn tạo thành chất oxi hóa yếu hơn và chất khử yếu hơn.
Định luật Faraday trong điện phân: $n_e = \frac{I \cdot t}{F}$ với $F = 96500\text{ C/mol}$.",
                                        SolutionMethod = @"Bước 1: Xác định thứ tự phản ứng theo tính oxi hóa và tính khử trong dãy điện hóa.
Bước 2: Lập phương trình trao đổi electron tại từng điện cực catot (-) và anot (+).
Bước 3: Dùng định luật Faraday tính lượng kim loại bám vào catot hoặc khí thoát ra ở anot.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Cho lá sắt ($Fe$) dư vào dung dịch chứa hỗn hợp $Cu(NO_3)_2$ và $AgNO_3$. Thứ tự các cation bị khử tại bề mặt thanh sắt là:",
                                                OptionA = "$Ag^+$ trước, $Cu^{2+}$ sau",
                                                OptionB = "$Cu^{2+}$ trước, $Ag^+$ sau",
                                                OptionC = "Cả hai cation bị khử đồng thời",
                                                OptionD = "$Fe^{2+}$ bị khử trước",
                                                CorrectOption = "A",
                                                Explanation = "Theo dãy điện hóa, ion $Ag^+$ có tính oxi hóa mạnh hơn $Cu^{2+}$ nên $Ag^+$ bị khử trước tạo thành kết tủa bạc bám vào thanh sắt."
                                            }
                                        }
                                    }
                                }
                            },

                            // ================= BÀI 4 =================
                            new()
                            {
                                LessonName = "Bài 4: Phương pháp quy đổi trong bài toán oxi hóa - khử phức tạp",
                                Weight = 0.07,
                                Summary = "Quy đổi về nguyên tố, cụm phân tử giả định và bảo toàn liên kết",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Quy đổi hỗn hợp nhiều oxide sắt và kim loại về nguyên tố độc lập",
                                        Weight = 0.025,
                                        Classification = "Kỹ thuật quy đổi nguyên tố nâng cao",
                                        CoreSkill = "Kỹ năng phân rã hỗn hợp đa oxide về các nguyên tử tự do (Fe, O)",
                                        TheoryRecap = @"Bản chất phương pháp quy đổi nguyên tố:
Quy đổi toàn bộ hỗn hợp phức tạp gồm $Fe, FeO, Fe_2O_3, Fe_3O_4$ thành hệ hai nguyên tố độc lập: $Fe$ ($x$ mol) và $O$ ($y$ mol).
Hệ phương trình toán học thiết lập:
1. Phương trình khối lượng: $56x + 16y = m_{\text{hỗn hợp}}$.
2. Phương trình bảo toàn electron: $3x = 2y + \sum (n_{\text{khí}} \times \text{số e nhận})$.",
                                        SolutionMethod = @"Bước 1: Quy đổi hỗn hợp thành các nguyên tử $Fe$ ($x$ mol) và $O$ ($y$ mol).
Bước 2: Thiết lập phương trình khối lượng hỗn hợp.
Bước 3: Lập phương trình bảo toàn electron từ trạng thái đầu đến cuối.
Bước 4: Giải hệ phương trình bậc nhất hai ẩn và suy ra khối lượng kim loại ban đầu.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Nung m gam sắt trong không khí thu được 12 gam hỗn hợp X gồm $Fe, FeO, Fe_2O_3, Fe_3O_4$. Hòa tan hoàn toàn X trong $HNO_3$ dư thu được 2,24 lít $NO$ (đktc). Giá trị của m là:",
                                                OptionA = "10,08 gam",
                                                OptionB = "8,40 gam",
                                                OptionC = "11,20 gam",
                                                OptionD = "9,52 gam",
                                                CorrectOption = "A",
                                                Explanation = "Quy đổi X về $Fe$ ($x$ mol) và $O$ ($y$ mol): $56x + 16y = 12$. Bảo toàn e: $3x = 2y + 3 \\cdot 0,1 \\Rightarrow 3x - 2y = 0,3$. Giải hệ ta được $x = 0,18; y = 0,12$. Vậy $m = 0,18 \\cdot 56 = 10,08 \\text{ gam}$."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Quy đổi hỗn hợp quặng sulfide phức tạp trong phản ứng với acid oxi hóa",
                                        Weight = 0.025,
                                        Classification = "Quy đổi quặng sulfide & Bảo toàn điện tích",
                                        CoreSkill = "Kỹ năng phân tích phản ứng của quặng pyrite, chalcopyrite",
                                        TheoryRecap = @"Hỗn hợp quặng chứa $FeS_2, Cu_2S, FeS, CuS$ khi phản ứng với dung dịch $HNO_3$ đặc nóng giải phóng khí $NO_2$ và tạo thành các ion $Fe^{3+}, Cu^{2+}, SO_4^{2-}$.
Quy đổi toàn bộ quặng thành $Fe$ ($x$ mol), $Cu$ ($y$ mol) và $S$ ($z$ mol).
Số electron nhường: $Fe \to Fe^{3+} + 3e$; $Cu \to Cu^{2+} + 2e$; $S \to S^{+6} + 6e$.",
                                        SolutionMethod = @"Bước 1: Quy đổi hỗn hợp sulfide thành các nguyên tử $Fe, Cu, S$.
Bước 2: Lập phương trình bảo toàn khối lượng quặng.
Bước 3: Lập phương trình bảo toàn electron: $3n_{Fe} + 2n_{Cu} + 6n_S = \sum n_{e\text{ nhận}}$.
Bước 4: Sử dụng bảo toàn điện tích dung dịch sau phản ứng.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Hòa tan hoàn toàn 12 gam hỗn hợp quặng gồm $FeS_2$ và $Cu_2S$ vào $HNO_3$ đặc nóng dư, thu được dung dịch chỉ chứa muối sulfate của các kim loại. Tỷ lệ mol giữa $FeS_2$ và $Cu_2S$ là:",
                                                OptionA = "1 : 1",
                                                OptionB = "2 : 1",
                                                OptionC = "1 : 2",
                                                OptionD = "3 : 2",
                                                CorrectOption = "A",
                                                Explanation = "Bảo toàn nguyên tố và điện tích: dung dịch chỉ chứa $Fe_2(SO_4)_3$ và $CuSO_4$ thì số mol điện tích dương của cation phải bằng điện tích âm của gốc $SO_4^{2-}$ sinh ra từ chính quặng."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 3: Quy đổi về phân tử và cụm hợp chất giả định tối ưu số ẩn",
                                        Weight = 0.02,
                                        Classification = "Quy đổi phân tử giả định",
                                        CoreSkill = "Kỹ năng nhóm phân tử và triệt tiêu ẩn số trung gian",
                                        TheoryRecap = @"Khi hỗn hợp có tỷ lệ mol hoặc cấu trúc đặc thù, quy đổi về cụm phân tử đơn giản hơn hoặc oxide có số oxi hóa trung bình (ví dụ coi hỗn hợp $FeO, Fe_2O_3$ có số mol bằng nhau tương đương với $Fe_3O_4$).",
                                        SolutionMethod = @"Bước 1: Nhận diện tỷ lệ số mol đặc biệt giữa các thành phần.
Bước 2: Ghép nối các phân tử thành hợp chất giả định có công thức đơn giản.
Bước 3: Giải toán bằng phương pháp bảo toàn khối lượng và bảo toàn electron trực tiếp.",
                                        DurationSeconds = 720,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 3,
                                                ContentLatex = "Hỗn hợp X gồm $FeO, Fe_3O_4, Fe_2O_3$ với số mol $FeO$ bằng số mol $Fe_2O_3$. Có thể quy đổi toàn bộ hỗn hợp X thành hợp chất duy nhất là:",
                                                OptionA = "$Fe_3O_4$",
                                                OptionB = "$Fe_2O_3$",
                                                OptionC = "$FeO$",
                                                OptionD = "$Fe$",
                                                CorrectOption = "A",
                                                Explanation = "Vì $1\\text{ mol } FeO + 1\\text{ mol } Fe_2O_3 = 1\\text{ mol } Fe_3O_4$, nên khi số mol của $FeO$ và $Fe_2O_3$ bằng nhau, ta hoàn toàn có thể gộp chúng lại thành $Fe_3O_4$."
                                            }
                                        }
                                    }
                                }
                            },

                            // ================= BÀI 5 =================
                            new()
                            {
                                LessonName = "Bài 5: Ứng dụng thực tế & Pin điện hóa - Năng lượng",
                                Weight = 0.06,
                                Summary = "Bài toán thực tiễn kiểm tra nồng độ cồn, chuẩn độ lượng vết và thế điện cực pin",
                                Patterns = new List<ImportPatternDto>
                                {
                                    new()
                                    {
                                        PatternName = "Dạng 1: Bài toán kiểm tra nồng độ cồn khí thở và huyết tương (Breathalyzer)",
                                        Weight = 0.02,
                                        Classification = "Vận dụng thực tiễn & Oxi hóa khử ethanol",
                                        CoreSkill = "Kỹ năng tính toán nồng độ mol và chuyển đổi đơn vị mg/L khí thở",
                                        TheoryRecap = @"Nguyên lý máy đo nồng độ cồn Breathalyzer:
Hơi thở chứa hơi ethanol ($C_2H_5OH$) bị oxi hóa bởi dung dịch potassium dichromate ($K_2Cr_2O_7$) trong môi trường acid sulfuric ($H_2SO_4$):
$$3C_2H_5OH + 2K_2Cr_2O_7 + 8H_2SO_4 \to 3CH_3COOH + 2Cr_2(SO_4)_3 + 2K_2SO_4 + 11H_2O$$
Hiện tượng quang học: Màu da cam đặc trưng của ion $Cr_2O_7^{2-}$ chuyển sang màu xanh lá cây của ion $Cr^{3+}$. Mức độ đổi màu được đo bằng tế bào quang điện để xác định chính xác nồng độ cồn.",
                                        SolutionMethod = @"Bước 1: Viết phương trình phản ứng chuẩn và ghi nhớ tỷ lệ mol: $3\text{ mol } C_2H_5OH \Leftrightarrow 2\text{ mol } K_2Cr_2O_7$.
Bước 2: Tính số mol $K_2Cr_2O_7$ phản ứng hoặc số mol $Cr^{3+}$ sinh ra.
Bước 3: Suy ra khối lượng ethanol và chia cho thể tích mẫu khí thở/huyết tương để tính nồng độ (mg/L).",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 1,
                                                ContentLatex = "Khi thổi khí thở chứa ethanol vào thiết bị đo nồng độ cồn chứa $K_2Cr_2O_7/H_2SO_4$, hiện tượng quan sát được là dung dịch chuyển từ màu:",
                                                OptionA = "Vàng cam sang xanh lá cây",
                                                OptionB = "Không màu sang kết tủa trắng",
                                                OptionC = "Xanh lam sang không màu",
                                                OptionD = "Đỏ nâu sang vàng nhạt",
                                                CorrectOption = "A",
                                                Explanation = "Ion dichromate $Cr_2O_7^{2-}$ (da cam/vàng cam) bị ethanol khử thành $Cr^{3+}$ (màu xanh lá cây)."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 2: Bài toán phân tích chuẩn độ lượng vết (Vitamin C, Oxy hòa tan DO - Winkler)",
                                        Weight = 0.02,
                                        Classification = "Chuẩn độ iod & Phân tích môi trường",
                                        CoreSkill = "Kỹ năng tính toán theo phản ứng chuẩn độ oxi hóa khử lượng vết",
                                        TheoryRecap = @"Các phương pháp chuẩn độ lượng vết tiêu chuẩn ĐGNL:
1. Chuẩn độ Vitamin C ($C_6H_8O_6$): Dùng dung dịch chuẩn $I_2$ với chỉ thị hồ tinh bột:
   $$C_6H_8O_6 + I_2 \to C_6H_6O_6 + 2HI$$
   Tại điểm tương đương, $I_2$ dư phản ứng với hồ tinh bột tạo phức màu xanh tím.
2. Phương pháp Winkler xác định hàm lượng Oxy hòa tan (DO) trong nước biển và nước thải:
   Hệ thức tương đương: $1\text{ mol } O_2 \Leftrightarrow 2\text{ mol } I_2 \Leftrightarrow 4\text{ mol } Na_2S_2O_3$.",
                                        SolutionMethod = @"Bước 1: Xác định phản ứng chuẩn độ và hệ số tỷ lượng giữa chất chuẩn và chất cần phân tích.
Bước 2: Tính số mol chất chuẩn tiêu hao từ nồng độ $C_M$ và thể tích buret $V$.
Bước 3: Quy đổi hàm lượng chất cần tìm về nồng độ mg/L hoặc phần trăm khối lượng.",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Trong phép chuẩn độ xác định hàm lượng Vitamin C ($C_6H_8O_6$) bằng dung dịch iodine ($I_2$), chỉ thị hóa học được sử dụng để nhận biết điểm tương đương là:",
                                                OptionA = "Dung dịch hồ tinh bột",
                                                OptionB = "Phenolphthalein",
                                                OptionC = "Quỳ tím",
                                                OptionD = "Methyl da cam",
                                                CorrectOption = "A",
                                                Explanation = "Hồ tinh bột phản ứng với lượng dư $I_2$ tạo thành phức hợp màu xanh tím đặc trưng ngay khi Vitamin C phản ứng hết."
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        PatternName = "Dạng 3: Pin điện hóa Galvanic, thế điện cực chuẩn, chiều phản ứng và suất điện động",
                                        Weight = 0.02,
                                        Classification = "Pin điện hóa & Năng lượng tái tạo",
                                        CoreSkill = "Kỹ năng tính suất điện động tiêu chuẩn và viết sơ đồ pin",
                                        TheoryRecap = @"Nguyên lý pin điện hóa Galvanic ($Zn - Cu$):
- Anot (-): Quá trình oxi hóa của cực kẽm: $Zn \to Zn^{2+} + 2e$.
- Catot (+): Quá trình khử của cực đồng: $Cu^{2+} + 2e \to Cu$.
- Suất điện động chuẩn của pin: $E^0_{\text{pin}} = E^0_{\text{catot}} - E^0_{\text{anot}} = E^0_{Cu^{2+}/Cu} - E^0_{Zn^{2+}/Zn} = +0,34 - (-0,76) = 1,10\text{ V}$.
- Chiều phản ứng tự diễn biến khi $E^0_{\text{pin}} > 0$.",
                                        SolutionMethod = @"Bước 1: Tra thế điện cực chuẩn của hai cặp oxi hóa - khử từ bảng số liệu.
Bước 2: Cực có thế điện cực nhỏ hơn đóng vai trò Anot (cực âm), cực có thế điện cực lớn hơn đóng vai trò Catot (cực dương).
Bước 3: Tính suất điện động của pin theo công thức: $E^0_{\text{pin}} = E^0_{\text{catot}} - E^0_{\text{anot}}$.",
                                        DurationSeconds = 600,
                                        Questions = new List<ImportQuestionDto>
                                        {
                                            new()
                                            {
                                                DifficultyLevel = 2,
                                                ContentLatex = "Cho thế điện cực chuẩn của hai cặp: $E^0_{Zn^{2+}/Zn} = -0,76\\text{ V}$ và $E^0_{Cu^{2+}/Cu} = +0,34\\text{ V}$. Suất điện động tiêu chuẩn của pin điện hóa $Zn - Cu$ là:",
                                                OptionA = "$1,10\\text{ V}$",
                                                OptionB = "$0,42\\text{ V}$",
                                                OptionC = "$-1,10\\text{ V}$",
                                                OptionD = "$0,76\\text{ V}$",
                                                CorrectOption = "A",
                                                Explanation = "Suất điện động của pin $E^0 = E^0_{\\text{catot}} - E^0_{\\text{anot}} = +0,34 - (-0,76) = 1,10\\text{ V}$."
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            EnrichPatternPedagogyAndQuestions(chemDto, domainName);
            return chemDto;
        }

        string topic = "Khảo Sát Hàm Số & Ứng Dụng Đạo Hàm";
        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0 && lines[0].Length > 5)
        {
            topic = lines[0].Length > 50 ? lines[0][..50] : lines[0];
        }

        var mathDto = new ImportCurriculumRequestDto
        {
            DomainId = domainId,
            DomainName = domainName,
            Chapters = new List<ImportChapterDto>
            {
                new()
                {
                    ChapterName = $"Chuyên đề: {topic}",
                    Weight = 0.35,
                    Description = "Phân tích cấu trúc phân cấp tri thức bóc tách tự động bởi C# AI Engine chuẩn GDPT 2018",
                    Lessons = new List<ImportLessonDto>
                    {
                        new()
                        {
                            LessonName = $"Bài 1: Tính đơn điệu & Khảo sát chiều biến thiên của {topic}",
                            Weight = 0.09,
                            Summary = "Định lý về dấu đạo hàm và quy tắc xét tính đơn điệu trên các khoảng",
                            Patterns = new List<ImportPatternDto>
                            {
                                new()
                                {
                                    PatternName = $"Dạng 1: Tìm khoảng đơn điệu của hàm số tường minh {topic}",
                                    Weight = 0.03,
                                    Classification = "Khảo sát hàm số & Tính đơn điệu",
                                    CoreSkill = "Kỹ năng tính đạo hàm và lập bảng xét dấu đạo hàm",
                                    TheoryRecap = @"Cho hàm số $y = f(x)$ có đạo hàm trên khoảng $K$:
- Nếu $f'(x) \ge 0, \forall x \in K$ và dấu bằng chỉ xảy ra tại một số hữu hạn điểm thì hàm số đồng biến trên $K$.
- Nếu $f'(x) \le 0, \forall x \in K$ và dấu bằng chỉ xảy ra tại một số hữu hạn điểm thì hàm số nghịch biến trên $K$.",
                                    SolutionMethod = "Bước 1: Tìm tập xác định $D$.\nBước 2: Tính đạo hàm $f'(x)$ và tìm nghiệm của $f'(x) = 0$.\nBước 3: Lập bảng biến thiên và kết luận các khoảng đồng biến, nghịch biến.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 1,
                                            ContentLatex = "Hàm số $y = x^3 - 3x^2 + 2$ đồng biến trên khoảng nào sau đây?",
                                            OptionA = "$(-\\infty; 0)$ và $(2; +\\infty)$",
                                            OptionB = "$(0; 2)$",
                                            OptionC = "$(-2; 0)$",
                                            OptionD = "$(2; +\\infty)$",
                                            CorrectOption = "A",
                                            Explanation = "$y' = 3x^2 - 6x = 3x(x - 2) > 0 \\Leftrightarrow x < 0$ hoặc $x > 2$."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = "Dạng 2: Tìm tham số m để hàm số đơn điệu trên khoảng",
                                    Weight = 0.03,
                                    Classification = "Bài toán chứa tham số $m$ & Bất phương trình đạo hàm",
                                    CoreSkill = "Kỹ năng cô lập tham số $m$ hoặc tam thức bậc hai",
                                    TheoryRecap = "Để hàm số đồng biến trên $K$: $y' \\ge 0, \\forall x \\in K$. Nếu cô lập được $m \\ge g(x) \\Leftrightarrow m \\ge \\max g(x)$; $m \\le g(x) \\Leftrightarrow m \\le \\min g(x)$.",
                                    SolutionMethod = "Bước 1: Tính đạo hàm $y'$.\nBước 2: Thiết lập điều kiện $y' \\ge 0$ (hoặc $\\le 0$) $\\forall x \\in K$.\nBước 3: Cô lập $m$ hoặc sử dụng định lý dấu tam thức bậc hai.",
                                    DurationSeconds = 720,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 3,
                                            ContentLatex = "Tìm tất cả các giá trị của $m$ để hàm số $y = \\frac{1}{3}x^3 - mx^2 + (m^2 - 4)x + 3$ đồng biến trên $\\mathbb{R}$:",
                                            OptionA = "Không có giá trị m thỏa mãn",
                                            OptionB = "$m \\in (-2; 2)$",
                                            OptionC = "$m \\le -2$ hoặc $m \\ge 2$",
                                            OptionD = "$m \\in [-2; 2]$",
                                            CorrectOption = "A",
                                            Explanation = "$y' = x^2 - 2mx + m^2 - 4$. Ta có $\\Delta' = m^2 - (m^2 - 4) = 4 > 0, \\forall m$, do đó $y'$ luôn đổi dấu, không thể đồng biến trên toàn $\\mathbb{R}$."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = "Dạng 3: Tính đơn điệu của hàm hợp và hàm liên kết",
                                    Weight = 0.03,
                                    Classification = "Hàm hợp & Đồ thị đạo hàm f'(x)",
                                    CoreSkill = "Kỹ năng đạo hàm hàm hợp $g'(x) = u'(x) \\cdot f'(u)$",
                                    TheoryRecap = "Cho hàm hợp $g(x) = f(u(x))$. Ta có đạo hàm $g'(x) = u'(x) \\cdot f'(u(x))$. Hàm số đồng biến khi $g'(x) \\ge 0$.",
                                    SolutionMethod = "Bước 1: Tính đạo hàm $g'(x) = u'(x) \\cdot f'(u(x))$.\nBước 2: Tìm nghiệm của $g'(x) = 0$.\nBước 3: Lập bảng xét dấu của $g'(x)$ và kết luận.",
                                    DurationSeconds = 720,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 3,
                                            ContentLatex = "Cho hàm số $y = f(x)$ có bảng xét dấu đạo hàm. Hàm số $g(x) = f(2 - x)$ đồng biến trên khoảng nào?",
                                            OptionA = "$(1; 3)$",
                                            OptionB = "$(-1; 1)$",
                                            OptionC = "$(3; +\\infty)$",
                                            OptionD = "$(-\\infty; 0)$",
                                            CorrectOption = "A",
                                            Explanation = "$g'(x) = -f'(2 - x)$. Để $g'(x) > 0 \\Leftrightarrow f'(2 - x) < 0$."
                                        }
                                    }
                                }
                            }
                        },
                        new()
                        {
                            LessonName = "Bài 2: Cực trị hàm số & Giá trị lớn nhất, nhỏ nhất",
                            Weight = 0.09,
                            Summary = "Phương pháp xác định điểm cực trị và tìm min-max trên đoạn/khoảng",
                            Patterns = new List<ImportPatternDto>
                            {
                                new()
                                {
                                    PatternName = "Dạng 1: Tìm cực trị của hàm số và cực trị đồ thị",
                                    Weight = 0.03,
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
                                            ContentLatex = "Điểm cực tiểu của hàm số $y = x^3 - 3x + 2$ là:",
                                            OptionA = "$x = 1$",
                                            OptionB = "$x = -1$",
                                            OptionC = "$y = 0$",
                                            OptionD = "$M(1; 0)$",
                                            CorrectOption = "A",
                                            Explanation = "$y' = 3x^2 - 3 = 0 \\Leftrightarrow x = \\pm 1$. Tại $x = 1$, đạo hàm đổi dấu từ âm sang dương nên $x = 1$ là điểm cực tiểu."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = "Dạng 2: Giá trị lớn nhất và nhỏ nhất trên đoạn [a; b]",
                                    Weight = 0.03,
                                    Classification = "GTLN & GTNN",
                                    CoreSkill = "Quy tắc so sánh giá trị tại các mút và điểm tới hạn",
                                    TheoryRecap = "Hàm số liên tục trên $[a; b]$ luôn đạt GTLN và GTNN. Quy tắc: Tính giá trị tại các nghiệm $x_i \\in (a; b)$ và hai đầu mút $f(a), f(b)$, số lớn nhất là Max, nhỏ nhất là Min.",
                                    SolutionMethod = "Bước 1: Tính $f'(x)$ và tìm các nghiệm $x_i \\in [a; b]$.\nBước 2: Tính $f(a), f(b), f(x_i)$.\nBước 3: Kết luận $\\max, \\min$.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 2,
                                            ContentLatex = "Giá trị nhỏ nhất của hàm số $y = x^4 - 2x^2 + 3$ trên đoạn $[0; 2]$ là:",
                                            OptionA = "2",
                                            OptionB = "3",
                                            OptionC = "11",
                                            OptionD = "1",
                                            CorrectOption = "A",
                                            Explanation = "$y' = 4x^3 - 4x = 0 \\Leftrightarrow x = 0, x = 1$ (trên đoạn $[0; 2]$). $y(0) = 3, y(1) = 2, y(2) = 11$. Vậy $\\min = 2$ tại $x = 1$."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = "Dạng 3: Bài toán tối ưu thực tế và min-max ứng dụng",
                                    Weight = 0.03,
                                    Classification = "Mô hình toán học & Tối ưu hóa thực tiễn",
                                    CoreSkill = "Kỹ năng lập hàm mục tiêu một biến và khảo sát cực trị",
                                    TheoryRecap = "Mô hình hóa bài toán thực tế thành hàm số $y = f(x)$ với biến $x$ thuộc miền thực tế ($x > 0$). Tìm giá trị tối ưu bằng đạo hàm.",
                                    SolutionMethod = "Bước 1: Đặt ẩn số $x$ và xác định điều kiện thực tế.\nBước 2: Thiết lập hàm mục tiêu $f(x)$ theo $x$.\nBước 3: Khảo sát đạo hàm tìm giá trị lớn nhất hoặc nhỏ nhất.",
                                    DurationSeconds = 720,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 3,
                                            ContentLatex = "Một người thợ muốn làm một chiếc bể chứa nước hình hộp chữ nhật không nắp có thể tích $V = 32\\text{ m}^3$, đáy là hình vuông cạnh $x$. Diện tích toàn phần của bể đạt giá trị nhỏ nhất khi $x$ bằng:",
                                            OptionA = "$4\\text{ m}$",
                                            OptionB = "$2\\text{ m}$",
                                            OptionC = "$8\\text{ m}$",
                                            OptionD = "$6\\text{ m}$",
                                            CorrectOption = "A",
                                            Explanation = "$V = x^2 h = 32 \\Rightarrow h = \\frac{32}{x^2}$. $S_{tp} = x^2 + 4xh = x^2 + \\frac{128}{x}$. Đạo hàm $S' = 2x - \\frac{128}{x^2} = 0 \\Leftrightarrow 2x^3 = 128 \\Leftrightarrow x = 4\\text{ m}$."
                                        }
                                    }
                                }
                            }
                        },
                        new()
                        {
                            LessonName = "Bài 3: Đường tiệm cận của đồ thị hàm số",
                            Weight = 0.08,
                            Summary = "Phương pháp xác định tiệm cận đứng, tiệm cận ngang và tiệm cận xiên",
                            Patterns = new List<ImportPatternDto>
                            {
                                new()
                                {
                                    PatternName = "Dạng 1: Xác định đường tiệm cận đứng và tiệm cận ngang của đồ thị hàm số",
                                    Weight = 0.04,
                                    Classification = "Tiệm cận đồ thị hàm số",
                                    CoreSkill = "Kỹ năng tính giới hạn một bên và giới hạn tại vô cực",
                                    TheoryRecap = "Đường thẳng $y = y_0$ là tiệm cận ngang nếu $\\lim_{x \\to \\pm\\infty} f(x) = y_0$. Đường thẳng $x = x_0$ là tiệm cận đứng nếu ít nhất một trong các giới hạn một bên bằng $\\pm\\infty$.",
                                    SolutionMethod = "Bước 1: Tìm tập xác định của hàm số.\nBước 2: Tính $\\lim_{x \\to \\pm\\infty} y$ tìm tiệm cận ngang.\nBước 3: Tìm nghiệm mẫu số không triệt tiêu tử số để xác định tiệm cận đứng.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 1,
                                            ContentLatex = "Đường tiệm cận đứng của đồ thị hàm số $y = \\frac{2x - 1}{x + 1}$ là:",
                                            OptionA = "$x = -1$",
                                            OptionB = "$y = 2$",
                                            OptionC = "$x = 1$",
                                            OptionD = "$y = -1$",
                                            CorrectOption = "A",
                                            Explanation = "Mẫu số triệt tiêu tại $x = -1$ và $\\lim_{x \\to -1^+} y = -\\infty$, do đó $x = -1$ là đường tiệm cận đứng."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = "Dạng 2: Bài toán tiệm cận chứa tham số m",
                                    Weight = 0.04,
                                    Classification = "Tiệm cận chứa tham số",
                                    CoreSkill = "Kỹ năng biện luận số nghiệm mẫu số khác nghiệm tử số",
                                    TheoryRecap = "Đồ thị hàm phân thức có số đường tiệm cận phụ thuộc vào số nghiệm của mẫu số không trùng với nghiệm tử số.",
                                    SolutionMethod = "Bước 1: Tìm điều kiện để mẫu số có đủ số nghiệm phân biệt.\nBước 2: Loại trừ các giá trị $m$ làm cho nghiệm của mẫu trùng với nghiệm của tử số.",
                                    DurationSeconds = 720,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 3,
                                            ContentLatex = "Tìm tất cả các giá trị của tham số $m$ để đồ thị hàm số $y = \\frac{x - 1}{x^2 - 2mx + 1}$ có đúng 2 đường tiệm cận đứng:",
                                            OptionA = "$m > 1$ hoặc $m < -1$",
                                            OptionB = "$-1 < m < 1$",
                                            OptionC = "$m > 1$",
                                            OptionD = "$m \\in \\mathbb{R} \\setminus \\{1\\}$",
                                            CorrectOption = "A",
                                            Explanation = "Để có 2 tiệm cận đứng, mẫu số phải có 2 nghiệm phân biệt khác 1: $\\Delta' = m^2 - 1 > 0 \\Leftrightarrow m > 1$ hoặc $m < -1$ và $1^2 - 2m(1) + 1 \\ne 0 \\Leftrightarrow 2 - 2m \\ne 0 \\Leftrightarrow m \\ne 1$."
                                        }
                                    }
                                }
                            }
                        },
                        new()
                        {
                            LessonName = "Bài 4: Khảo sát sự biến thiên & Đọc đồ thị hàm số",
                            Weight = 0.09,
                            Summary = "Nhận dạng đồ thị, bảng biến thiên và tương giao đồ thị hàm số",
                            Patterns = new List<ImportPatternDto>
                            {
                                new()
                                {
                                    PatternName = "Dạng 1: Nhận diện đồ thị và bảng biến thiên của các hàm số cơ bản",
                                    Weight = 0.045,
                                    Classification = "Nhận diện đồ thị & Bảng biến thiên",
                                    CoreSkill = "Kỹ năng xét dấu hệ số $a$, giao điểm với các trục tọa độ và điểm cực trị",
                                    TheoryRecap = "Thứ tự nhận diện đồ thị hàm đa thức: 1. Giới hạn vô cực (xét dấu $a$); 2. Giao điểm trục tung $(0; c)$; 3. Số lượng và tọa độ điểm cực trị; 4. Tương giao với trục hoành.",
                                    SolutionMethod = "Bước 1: Quan sát nhánh ngoài cùng bên phải để xác định dấu của hệ số cao nhất $a$.\nBước 2: Tìm giao điểm của đồ thị với trục tung $Oy$ ($x = 0$).\nBước 3: Đếm số điểm cực trị và đối chiếu tọa độ.",
                                    DurationSeconds = 600,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 1,
                                            ContentLatex = "Đường cong hình bên là đồ thị của hàm số nào sau đây nếu nhánh ngoài cùng bên phải đi xuống và có 2 điểm cực trị?",
                                            OptionA = "$y = -x^3 + 3x - 1$",
                                            OptionB = "$y = x^3 - 3x - 1$",
                                            OptionC = "$y = -x^4 + 2x^2 - 1$",
                                            OptionD = "$y = \\frac{2x - 1}{x + 1}$",
                                            CorrectOption = "A",
                                            Explanation = "Đồ thị hàm bậc ba có hệ số $a < 0$ (nhánh ngoài cùng đi xuống) và có hai điểm cực trị nên chọn hàm số $y = -x^3 + 3x - 1$."
                                        }
                                    }
                                },
                                new()
                                {
                                    PatternName = "Dạng 2: Bài toán tương giao đồ thị và số nghiệm phương trình f(x) = m",
                                    Weight = 0.045,
                                    Classification = "Tương giao đồ thị hàm số & Biện luận nghiệm",
                                    CoreSkill = "Kỹ năng kẻ đường thẳng nằm ngang $y = m$ cắt đồ thị",
                                    TheoryRecap = "Số nghiệm của phương trình $f(x) = m$ chính là số giao điểm của đồ thị $(C): y = f(x)$ và đường thẳng nằm ngang $d: y = m$.",
                                    SolutionMethod = "Bước 1: Biến đổi phương trình về dạng chuẩn $f(x) = g(m)$.\nBước 2: Vẽ bảng biến thiên hoặc đồ thị hàm số $y = f(x)$.\nBước 3: Tịnh tiến đường thẳng $y = g(m)$ song song trục hoành để biện luận số giao điểm.",
                                    DurationSeconds = 720,
                                    Questions = new List<ImportQuestionDto>
                                    {
                                        new()
                                        {
                                            DifficultyLevel = 2,
                                            ContentLatex = "Cho hàm số $y = f(x)$ có bảng biến thiên với giá trị cực đại $y_{CĐ} = 2$ và giá trị cực tiểu $y_{CT} = -2$. Số nghiệm của phương trình $f(x) = 1$ là:",
                                            OptionA = "3 nghiệm",
                                            OptionB = "2 nghiệm",
                                            OptionC = "1 nghiệm",
                                            OptionD = "4 nghiệm",
                                            CorrectOption = "A",
                                            Explanation = "Vì $-2 < 1 < 2$ nên đường thẳng $y = 1$ cắt 3 nhánh của đồ thị tại 3 điểm phân biệt, do đó phương trình có 3 nghiệm."
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        EnrichPatternPedagogyAndQuestions(mathDto, domainName);
        return mathDto;
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
