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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int totalPagesProcessed = 0;
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
                        totalPagesProcessed++;
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

        string depthDirective = @"YÊU CẦU BÓC TÁCH TOÀN DIỆN, ĐA DẠNG BÀI TOÁN & PHỦ RỘNG 100% CÁC PHẦN (COMPREHENSIVE EXTRACTION):
- TÀI LIỆU CÓ QUY MÔ LỚN GỒM NHIỀU PHẦN / CHỦ ĐỀ HỌC THUẬT.
- BẮT BUỘC QUÉT TOÀN BỘ CÁC PHẦN (Phần I Lý thuyết, Phần II Bài tập phân dạng, Phần III Nâng cao, Phần IV Ứng dụng thực tế, Phần V Trắc nghiệm):
  + BẮT BUỘC BÓC TÁCH TỪ 4 ĐẾN 5 BÀI HỌC (`lessons`) bao quát toàn diện các phần:
    * Bài 1: Bản chất số oxi hóa & Quy tắc xác định (hoặc chủ đề nền tảng 1).
    * Bài 2: Cân bằng phản ứng oxi hóa - khử nâng cao (Thăng bằng electron & Ion-electron).
    * Bài 3: Phương pháp giải toán bảo toàn electron (Kim loại tác dụng acid & Hỗn hợp khí).
    * Bài 4: Phương pháp quy đổi nâng cao trong giải toán hóa học (Quy đổi nguyên tố & Oxide giả định).
    * Bài 5: Bài toán ứng dụng thực tế, ĐGNL & Bẫy đề thi nâng cao (Thổi nồng độ cồn, pin điện hóa, ăn mòn, HSG).
  + MỖI BÀI HỌC (LESSON) BẮT BUỘC NHẬN DIỆN TỪ 2 ĐẾN 3 DẠNG BÀI TOÁN CHI TIẾT (`patterns`). TUYỆT ĐỐI KHÔNG ĐỂ 1 DẠNG BÀI ĐƠN ĐỘC!
  + TỔNG SỐ DẠNG BÀI TRÊN TOÀN CHƯƠNG PHẢI ĐẠT TỪ 8 ĐẾN 12 DẠNG BÀI CHI TIẾT.
  + MỖI DẠNG BÀI CÓ ĐỦ 4 CÂU HỎI BÀI MẪU (Câu 1: L1 Nhận biết, Câu 2: L2 Thông hiểu, Câu 3: L3 Vận dụng, Câu 4: L4 Vận dụng cao), kèm KaTeX và giải súc tích.";

        // 2. Thử gọi Google Gemini qua C# HttpClient
        var apiKeys = ResolveApiKeys();
        string modelName = ResolveModelName();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        if (apiKeys.Count > 0)
        {
            bool isStem = IsStemDomain(domainName);
            string domainScope = isStem ? "TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS)" : "TỪNG DẠNG BÀI ĐỌC HIỂU / LÀM VĂN / BÀI HỌC CỤ THỂ (PATTERNS)";
            string sampleHeading = isStem ? "### 📄 Bài toán mẫu áp dụng phương pháp:" : "### 📄 Bài mẫu áp dụng phương pháp:";
            string tipHeading = isStem ? "### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:" : "### 💡 Mẹo tư duy & Chiến thuật làm bài:";
            string tipGuideline = isStem 
                ? "Mẹo nhẩm hoặc kỹ thuật bấm máy Casio (fx-580VN X, fx-880BTG)" 
                : "Mẹo gạch chân từ khóa, phân bổ thời gian, chiến thuật hành văn ghi điểm tuyệt đối (TUYỆT ĐỐI KHÔNG sinh mẹo bấm máy Casio cho môn Ngữ văn/Khoa học xã hội)";
            string sampleKey = isStem ? "Bài toán mẫu:" : "Bài mẫu:";

            string prompt = $@"BẠN LÀ HỆ THỐNG TRÍ TUỆ NHÂN TẠO HỌC THUẬT & TRÍ THỨC SƯ PHẠM ĐÁNH GIÁ NĂNG LỰC (CURRICULUM PATTERN REASONING ENGINE).
Nhiệm vụ: Phân tích sâu tài liệu giáo khoa/chuyên đề và trích xuất cấu trúc phân cấp tri thức chuẩn SGK GDPT 2018:
Miền ({domainName}) -> Các Chương -> Các Bài học -> {domainScope}.

{depthDirective}

QUY TẮC BẮT BUỘC VỀ VÍ DỤ MINH HỌA, BÀI MẪU & NGÂN HÀNG CÂU HỎI (MANDATORY EXAMPLES & SAMPLE PROBLEMS):
1. PHÂN BIỆT RÕ CÁC DẠNG BÀI: Đặt tên dạng bài rõ ràng: 'Dạng [Số]: [Tên dạng bài chi tiết]'.
2. BẮT BUỘC ĐỦ 4 KHỐI NỘI DUNG SƯ PHẠM KÈM VÍ DỤ VÀ BÀI TOÁN MẪU:
   - `theoryRecap`: Lý thuyết nền tảng SGK, định lý/khái niệm cốt lõi, công thức KaTeX đặt trong $...$ (nếu có). Kèm theo '### 📌 Ví dụ minh họa SGK:' (Trích xuất ví dụ/ngữ liệu từ tài liệu có phân tích và lời giải ngắn gọn).
   - `solutionMethod`: Quy trình 3-4 bước giải / làm bài: 'Bước 1: ...', 'Bước 2: ...', 'Bước 3: ...'. Kèm theo:
     * '{sampleHeading}' (Bài mẫu có phân tích/giải từng bước).
     * '### ⚠️ Các bẫy đề thi ĐGNL thường gặp:' (1-2 bẫy đề thi hay gặp).
     * '{tipHeading}' ({tipGuideline}).
   - `videoUrl`: null, `durationSeconds`: 600.
   - `questions`: MỖI DẠNG BÀI CÓ ĐÚNG 4 CÂU HỎI TRẮC NGHIỆM ĐA TẦNG BLOOM (L1 -> L4):
     * Câu 1: [L1 - Nhận biết] Câu hỏi lý thuyết / khái niệm / nhận diện hiện tượng.
     * Câu 2: [L2 - Thông hiểu] Ví dụ mẫu áp dụng trực tiếp quy tắc/công thức/đọc hiểu.
     * Câu 3: [L3 - Vận dụng] Câu hỏi vận dụng ĐGNL, phân tích ngữ cảnh / số liệu.
     * Câu 4: [L4 - Vận dụng cao] Bài toán/câu hỏi tổng hợp nâng cao, phân loại học sinh.
     Mỗi câu gồm: difficultyLevel (1-4), contentLatex, optionA, optionB, optionC, optionD, correctOption ('A'|'B'|'C'|'D'), explanation (giải súc tích).
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
          ""lessonName"": ""Bài 1: [Tên bài học 1]"",
          ""weight"": 0.1,
          ""patterns"": [
            {{
              ""patternName"": ""Dạng 1: [Tên dạng bài 1]"",
              ""weight"": 0.05,
              ""classification"": ""..."",
              ""coreSkill"": ""..."",
              ""theoryRecap"": ""[Định lý & Công thức SGK]\n\n### 📌 Ví dụ minh họa SGK:\n**Ví dụ:** ...\n**Phân tích & Lời giải:** ..."",
              ""solutionMethod"": ""Bước 1: ...\nBước 2: ...\n\n{sampleHeading}\n**{sampleKey}** ...\n**Hướng dẫn giải:** ...\n\n### ⚠️ Các bẫy đề thi:\n...\n\n{tipHeading}\n..."",
              ""durationSeconds"": 600,
              ""questions"": [
                {{
                  ""difficultyLevel"": 1,
                  ""contentLatex"": ""[Câu 1 - Nhận biết]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""A"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 2,
                  ""contentLatex"": ""[Câu 2 - Thông hiểu]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""B"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 3,
                  ""contentLatex"": ""[Câu 3 - Vận dụng]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""C"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 4,
                  ""contentLatex"": ""[Câu 4 - Vận dụng cao]..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""D"",
                  ""explanation"": ""...""
                }}
              ]
            }},
            {{
              ""patternName"": ""Dạng 2: [Tên dạng bài 2 chi tiết]"",
              ""weight"": 0.05,
              ""classification"": ""..."",
              ""coreSkill"": ""..."",
              ""theoryRecap"": ""..."",
              ""solutionMethod"": ""..."",
              ""durationSeconds"": 600,
              ""questions"": [
                {{
                  ""difficultyLevel"": 1,
                  ""contentLatex"": ""..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""A"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 2,
                  ""contentLatex"": ""..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""B"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 3,
                  ""contentLatex"": ""..."",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""C"",
                  ""explanation"": ""...""
                }},
                {{
                  ""difficultyLevel"": 4,
                  ""contentLatex"": ""..."",
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
{textSnippet}

LƯU Ý CỰC KỲ QUAN TRỌNG VỀ ĐỊNH DẠNG JSON VÀ LATEX (CRITICAL JSON RULES):
1. TRÍCH XUẤT 100% DỮ LIỆU THẬT: Toàn bộ Chương, Bài học, Dạng bài, Ví dụ và Câu hỏi trắc nghiệm phải được trích xuất THỰC TẾ từ tài liệu được cung cấp ở trên. Tuyệt đối không tự bịa đặt nội dung mẫu chung chung.
2. ESCAPE LATEX TRONG JSON: Mọi ký tự gạch chéo ngược LaTeX BẮT BUỘC phải dùng hai gạch chéo ngược (double backslash): \\frac, \\sqrt, \\Delta, \\{{, \\}}, \\%, \\$, \\alpha, \\beta, \\times, \\text... Tuyệt đối không dùng 1 gạch chéo ngược đơn độc.
3. QUY TẮC DẤU NGOẶC KÉP TRÍCH DẪN: TUYỆT ĐỐI KHÔNG dùng dấu ngoặc kép thẳng "" bên trong nội dung văn bản (đề bài, lý thuyết, giải thích, phương án A-D). Nếu cần trích dẫn thuật ngữ, tên chất hay công thức, BẮT BUỘC dùng dấu nháy đơn '...' (ví dụ: 'chất khử', 'Fe').
4. ĐẦU RA HOÀN CHỈNH: Đảm bảo đóng đầy đủ các dấu ngoặc nhọn }} và ngoặc vuông ]] để JSON hợp lệ 100%. Không sinh bất kỳ đoạn chat giải thích nào ngoài khối JSON.";

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
                            ["maxOutputTokens"] = 32768,
                            ["responseMimeType"] = "application/json"
                        }
                    };

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                        var docNode = JsonNode.Parse(respJson);
                        string? contentText = docNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(contentText))
                        {
                            contentText = SanitizeAndRepairGeminiJson(contentText);
                            ImportCurriculumRequestDto? parsed = null;

                            try
                            {
                                parsed = JsonSerializer.Deserialize<ImportCurriculumRequestDto>(contentText, new JsonSerializerOptions 
                                { 
                                    PropertyNameCaseInsensitive = true,
                                    AllowTrailingCommas = true
                                });
                            }
                            catch (Exception jsonEx)
                            {
                                _logger.LogWarning("Strict JSON deserialize gặp lỗi ({Msg}), chuyển sang bộ phân tích JsonNode mềm dẻo...", jsonEx.Message);
                                parsed = TryParseCurriculumFromLooseJson(contentText, domainId, domainName);
                            }

                            if (parsed == null || parsed.Chapters.Count == 0)
                            {
                                _logger.LogWarning("JsonNode phân tích không trọn vẹn, kích hoạt bộ bóc tách Regex Real-Data Engine...");
                                parsed = ExtractCurriculumViaRegex(contentText, domainId, domainName);
                            }

                            if (parsed != null && parsed.Chapters.Count > 0)
                            {
                                stopwatch.Stop();
                                parsed.ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
                                parsed.IsRealExtraction = true;
                                parsed.SourceFileName = fileName ?? "Direct_Text_Input";
                                parsed.TotalPagesProcessed = totalPagesProcessed > 0 ? totalPagesProcessed : 1;
                                parsed.TotalCharactersExtracted = extractedText.Length;

                                _logger.LogInformation("C# AI Engine trích xuất thành công {Count} chương DỮ LIỆU THẬT trong {Elapsed:F2}s ({Ms} ms) từ tệp '{FileName}'.", 
                                    parsed.Chapters.Count, parsed.ProcessingTimeSeconds, stopwatch.ElapsedMilliseconds, parsed.SourceFileName);
                                return parsed;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Gọi Gemini API thất bại ({Message}), tiếp tục kiểm tra key/model tiếp theo.", ex.Message);
                }
            }
        }

        stopwatch.Stop();
        string errDetail = apiKeys.Count == 0 ? "Chưa cấu hình GEMINI_API_KEY trong hệ thống." : "Gemini AI không thể bóc tách cấu trúc tri thức hoặc kết nối mạng bị gián đoạn.";
        throw new InvalidOperationException($"Không thể trích xuất cấu trúc tri thức thực tế từ tài liệu: {errDetail}. Vui lòng kiểm tra lại API key hoặc nội dung tệp.");
    }

    public async Task<AnalyzePatternResponseDto> AnalyzePatternAsync(
        AnalyzePatternRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string patternName = request.PatternName?.Trim() ?? "Dạng bài toán trọng tâm";
        string lessonName = request.LessonName?.Trim() ?? string.Empty;
        string domainName = request.DomainName?.Trim() ?? "Toán học & Tư duy định lượng";

        var apiKeys = ResolveApiKeys();
        string modelName = ResolveModelName();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        if (apiKeys.Count > 0)
        {
            bool isStem = IsStemDomain(domainName);
            string prompt = $@"BẠN LÀ CHUYÊN GIA TRÍ THỨC SƯ PHẠM V-EVAL & ĐÁNH GIÁ NĂNG LỰC ĐHQG (CURRICULUM PATTERN REASONING ENGINE).
Nhiệm vụ: Phân tích chuyên sâu dạng bài {(isStem ? "toán" : "đọc hiểu / làm văn / bài học")} sau để nhận diện rõ ràng bản chất dạng và chuẩn hóa 4 khối nội dung sư phạm:
- Dạng bài: ""{patternName}""
- Bài học trực thuộc: ""{lessonName}""
- Miền năng lực: ""{domainName}""

QUY TẮC BẮT BUỘC:
1. `classification`: Phân loại dạng.
2. `core_skill`: Kỹ năng tư duy cốt lõi cần đo lường.
3. `bloom_level`: Mức độ nhận thức Bloom phù hợp (1..4).
4. `theory_recap`: Tóm lược kiến thức nền tảng SGK GDPT 2018 (KaTeX inline $...$ nếu có).
5. `solution_method`: Quy trình giải / làm bài 4 bước chi tiết chuẩn mực.
6. `exam_traps`: 2-3 bẫy đề thi ĐGNL học sinh hay mắc sai lầm nhất.
7. `casio_tricks`: {(isStem ? "Kỹ thuật bấm máy Casio (fx-580VN X, fx-880BTG) hoặc mẹo tư duy nhanh." : "Mẹo tư duy nhanh, chiến thuật đọc hiểu/hành văn, kỹ thuật phân bổ thời gian và mẹo ghi điểm tối đa ĐGNL (TUYỆT ĐỐI KHÔNG đề cập máy tính Casio vì đây là môn Ngữ văn / Khoa học xã hội).")}
8. `sample_question`: 1 câu hỏi trắc nghiệm minh họa 4 phương án kèm lời giải chi tiết.

LƯU Ý CỰC KỲ QUAN TRỌNG VỀ ĐỊNH DẠNG JSON VÀ LATEX:
- Mọi ký tự gạch chéo ngược LaTeX BẮT BUỘC dùng 2 gạch chéo ngược: \\frac, \\sqrt, \\Delta, \\{{, \\}}, \\%, \\$, \\alpha... Tuyệt đối không dùng 1 gạch chéo ngược đơn độc.
- Chỉ trả về duy nhất 1 JSON object hợp lệ.

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
                            ["maxOutputTokens"] = 8192,
                            ["responseMimeType"] = "application/json"
                        }
                    };

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                        var docNode = JsonNode.Parse(respJson);
                        string? contentText = docNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(contentText))
                        {
                            contentText = SanitizeAndRepairGeminiJson(contentText);
                            AnalyzePatternResponseDto? parsed = null;
                            try
                            {
                                parsed = JsonSerializer.Deserialize<AnalyzePatternResponseDto>(contentText, new JsonSerializerOptions 
                                { 
                                    PropertyNameCaseInsensitive = true,
                                    AllowTrailingCommas = true
                                });
                            }
                            catch (Exception jsonEx)
                            {
                                _logger.LogWarning("Strict JSON deserialize pattern gặp lỗi ({Msg}), kích hoạt bộ bóc tách Regex...", jsonEx.Message);
                                parsed = ExtractPatternAnalysisViaRegex(contentText, patternName);
                            }

                            if (parsed == null || string.IsNullOrWhiteSpace(parsed.PatternName))
                            {
                                parsed = ExtractPatternAnalysisViaRegex(contentText, patternName);
                            }

                            if (parsed != null)
                            {
                                stopwatch.Stop();
                                parsed.ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
                                _logger.LogInformation("C# AI Engine nhận diện thành công dạng bài '{Pattern}' DỮ LIỆU THẬT trong {Elapsed:F2}s.", patternName, parsed.ProcessingTimeSeconds);
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

        stopwatch.Stop();
        string errDetail = apiKeys.Count == 0 ? "Chưa cấu hình GEMINI_API_KEY trong hệ thống." : "Gemini AI không thể phân tích dạng bài hoặc kết nối mạng bị gián đoạn.";
        throw new InvalidOperationException($"Không thể phân tích dạng bài '{patternName}' từ Gemini AI: {errDetail}.");
    }

    public async Task<GeneratePatternQuestionsResponseDto> GeneratePatternQuestionsAsync(
        GeneratePatternQuestionsRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string patternName = request.PatternName?.Trim() ?? "Dạng bài trọng tâm";
        string lessonName = request.LessonName?.Trim() ?? string.Empty;
        string domainName = request.DomainName?.Trim() ?? string.Empty;
        string theoryRecap = request.TheoryRecap?.Trim() ?? string.Empty;
        string solutionMethod = request.SolutionMethod?.Trim() ?? string.Empty;
        int count = request.Count <= 0 ? 2 : request.Count;

        var apiKeys = ResolveApiKeys();
        string modelName = ResolveModelName();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        if (apiKeys.Count > 0)
        {
            bool isStem = IsStemDomain(domainName);
            string antiDuplicationNote = string.Empty;
            if (request.ExistingQuestions != null && request.ExistingQuestions.Count > 0)
            {
                var samples = string.Join("\n- ", request.ExistingQuestions.TakeLast(6).Select(q => q.Length > 150 ? q[..150] + "..." : q));
                antiDuplicationNote = $@"
QUY TẮC BẮT BUỘC CHỐNG TRÙNG LẶP:
Dạng bài này hiện ĐÃ CÓ {request.ExistingQuestions.Count} câu hỏi sau:
- {samples}
TUYỆT ĐỐI KHÔNG ĐƯỢC lặp lại, không được copy lại đề bài hoặc ngữ liệu y hệt ở trên!
BẮT BUỘC PHẢI KHAI THÁC các trích đoạn thơ/văn mới, góc nhìn cảm thụ mới, chi tiết nghệ thuật mới, hoặc các câu hỏi đọc hiểu/vận dụng hoàn toàn mới!";
            }

            string prompt = $@"BẠN LÀ CHUYÊN GIA SƯ PHẠM V-EVAL & ĐÁNH GIÁ NĂNG LỰC ĐHQG (PRACTICE QUESTION GENERATOR).
Nhiệm vụ: Tạo ra đúng {count} câu hỏi trắc nghiệm rèn luyện (gồm 1 câu L3 Vận dụng và 1 câu L4 Vận dụng cao) BÁM SÁT 100% VÀO DẠNG BÀI ĐANG ĐƯỢC PHÂN TÍCH:
- Tên Dạng bài: ""{patternName}""
- Bài học: ""{lessonName}""
- Môn học / Miền: ""{domainName}""
- Khối 1 (Lý thuyết nền tảng SGK & Ví dụ minh họa):
{theoryRecap}
- Khối 2 (Phương pháp giải chi tiết & Bài toán mẫu áp dụng):
{solutionMethod}
{antiDuplicationNote}

QUY TẮC BẮT BUỘC VỀ SƯ PHẠM:
1. ĐẶC BIỆT LƯU Ý VỚI MÔN NGỮ VĂN / KHOA HỌC XÃ HỘI:
   - Câu hỏi BẮT BUỘC PHẢI THAM KHẢO VÀ DỰA VÀO ĐÚNG TÁC PHẨM, NGỮ LIỆU, HOẶC VÍ DỤ NÊU TRONG LÝ THUYẾT VÀ PHƯƠNG PHÁP GIẢI CỦA DẠNG NÀY (ví dụ: các bài thơ, đoạn trích, tác giả, vấn đề nghị luận được đề cập ở trên).
   - TUYỆT ĐỐI KHÔNG dùng toán học, không có đại số, không có biến số X, nồng độ, thể tích hay phương trình!
   - Câu hỏi L3: Đo lường kỹ năng đọc hiểu, xác định vấn đề nghị luận, phạm vi dẫn chứng, kiểu đoạn văn (Tổng - phân - hợp, diễn dịch...), hoặc phân tích chi tiết tiêu biểu.
   - Câu hỏi L4: Đo lường tư duy đánh giá, liên hệ nâng cao bối cảnh thời đại, chiều sâu tư tưởng nhân văn hoặc phong cách tác giả.
2. ĐỐI VỚI CÁC MÔN STEM (Toán, Lý, Hóa, Sinh, Tin):
   - Câu hỏi phải bám sát đúng mô hình toán/khoa học, thuật toán và công thức của dạng này.

LƯU Ý CỰC KỲ QUAN TRỌNG VỀ ĐỊNH DẠNG JSON VÀ LATEX:
- Mọi ký tự gạch chéo ngược LaTeX BẮT BUỘC dùng 2 gạch chéo ngược: \\frac, \\sqrt, \\Delta, \\{{, \\}}, \\%, \\$, \\alpha...
- Chỉ trả về duy nhất 1 JSON object hợp lệ theo schema sau:

{{
  ""questions"": [
    {{
      ""difficultyLevel"": 3,
      ""contentLatex"": ""[Bài tập rèn luyện 1] Đề bài..."",
      ""optionA"": ""Phương án A..."",
      ""optionB"": ""Phương án B..."",
      ""optionC"": ""Phương án C..."",
      ""optionD"": ""Phương án D..."",
      ""correctOption"": ""A"",
      ""explanation"": ""Lời giải chi tiết từng bước...""
    }},
    {{
      ""difficultyLevel"": 4,
      ""contentLatex"": ""[Bài tập rèn luyện 2 - Vận dụng cao] Đề bài..."",
      ""optionA"": ""Phương án A..."",
      ""optionB"": ""Phương án B..."",
      ""optionC"": ""Phương án C..."",
      ""optionD"": ""Phương án D..."",
      ""correctOption"": ""A"",
      ""explanation"": ""Lời giải chi tiết từng bước...""
    }}
  ]
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
                            ["temperature"] = 0.7,
                            ["maxOutputTokens"] = 8192,
                            ["responseMimeType"] = "application/json"
                        }
                    };

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                        var docNode = JsonNode.Parse(respJson);
                        string? contentText = docNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(contentText))
                        {
                            contentText = SanitizeAndRepairGeminiJson(contentText);
                            JsonNode? parsedNode = null;
                            try
                            {
                                parsedNode = JsonNode.Parse(contentText);
                            }
                            catch (Exception parseEx)
                            {
                                _logger.LogWarning("Strict JsonNode.Parse failed: {Message}. Attempting loose regex extraction.", parseEx.Message);
                            }

                            var questionsArray = parsedNode?["questions"]?.AsArray();
                            var resultQuestions = new List<ImportQuestionDto>();

                            if (questionsArray != null && questionsArray.Count > 0)
                            {
                                foreach (var qNode in questionsArray)
                                {
                                    if (qNode == null) continue;
                                    var q = new ImportQuestionDto
                                    {
                                        DifficultyLevel = qNode["difficultyLevel"]?.GetValue<int>() ?? qNode["difficulty_level"]?.GetValue<int>() ?? 3,
                                        ContentLatex = qNode["contentLatex"]?.GetValue<string>() ?? qNode["content_latex"]?.GetValue<string>() ?? string.Empty,
                                        OptionA = qNode["optionA"]?.GetValue<string>() ?? qNode["option_a"]?.GetValue<string>() ?? string.Empty,
                                        OptionB = qNode["optionB"]?.GetValue<string>() ?? qNode["option_b"]?.GetValue<string>() ?? string.Empty,
                                        OptionC = qNode["optionC"]?.GetValue<string>() ?? qNode["option_c"]?.GetValue<string>() ?? string.Empty,
                                        OptionD = qNode["optionD"]?.GetValue<string>() ?? qNode["option_d"]?.GetValue<string>() ?? string.Empty,
                                        CorrectOption = qNode["correctOption"]?.GetValue<string>() ?? qNode["correct_option"]?.GetValue<string>() ?? "A",
                                        Explanation = qNode["explanation"]?.GetValue<string>() ?? string.Empty
                                    };
                                    if (!string.IsNullOrWhiteSpace(q.ContentLatex))
                                    {
                                        resultQuestions.Add(q);
                                    }
                                }
                            }
                            else
                            {
                                resultQuestions = ExtractQuestionsViaRegex(contentText);
                            }

                            if (resultQuestions.Count > 0)
                            {
                                stopwatch.Stop();
                                return new GeneratePatternQuestionsResponseDto
                                {
                                    Questions = resultQuestions,
                                    ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2)
                                };
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Gemini AI generate pattern questions error: {Message}", ex.Message);
                }
            }
        }

        stopwatch.Stop();
        throw new InvalidOperationException($"Không thể tạo thêm câu hỏi cho dạng bài '{patternName}' từ Gemini AI.");
    }


    private static string CleanJsonFences(string json)
    {
        return SanitizeAndRepairGeminiJson(json);
    }

    private static string SanitizeAndRepairGeminiJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "{}";

        string cleaned = rawJson.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(7);
        else if (cleaned.StartsWith("```"))
            cleaned = cleaned.Substring(3);

        if (cleaned.EndsWith("```"))
            cleaned = cleaned.Substring(0, cleaned.Length - 3);

        cleaned = cleaned.Trim();

        // 0. Chuẩn hóa các dấu ngoặc kép trích dẫn dạng typographic
        cleaned = cleaned.Replace('“', '\'')
                         .Replace('”', '\'')
                         .Replace('„', '\'')
                         .Replace('‟', '\'')
                         .Replace('«', '\'')
                         .Replace('»', '\'');

        // 0.1 Chuẩn hóa các dấu ngoặc kép không escape ở giữa các trường giá trị theo từng dòng
        var lines = cleaned.Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            var line = lines[l];
            var m = Regex.Match(line, @"^(\s*""[a-zA-Z0-9_]+""\s*:\s*"")(.*)(""(?:\s*,\s*|\s*)?)$");
            if (m.Success)
            {
                var prefix = m.Groups[1].Value;
                var middle = m.Groups[2].Value;
                var suffix = m.Groups[3].Value;
                middle = Regex.Replace(middle, @"(?<!\\)""", "'");
                lines[l] = prefix + middle + suffix;
            }
        }
        cleaned = string.Join("\n", lines);

        // 0.2 Sửa lỗi Gemini gõ nhầm dấu phẩy thay vì hai chấm sau tên thuộc tính (ví dụ: "propertyName", "value" -> "propertyName": "value")
        // Bắt buộc dùng lookbehind (?<=[\{\,]\s*) để chỉ sửa khi là tên trường, tuyệt đối không sửa giá trị như "correctOption": "B",
        cleaned = Regex.Replace(cleaned, @"(?<=[\{\,]\s*)(""[a-zA-Z0-9_]{2,40}"")\s*,\s*(?=""|\[|\{|-?[0-9]|true|false)", "$1: ");

        // 1. Phục hồi các ký tự điều khiển ASCII bị nuốt do escape
        cleaned = cleaned.Replace("\x0crac", "\\\\frac")
                         .Replace("\x09imes", "\\\\times")
                         .Replace("\x09ext", "\\\\text")
                         .Replace("\x08ar", "\\\\bar")
                         .Replace("\x08eta", "\\\\beta");

        // 2. Tokenize và sanitize các invalid escapes bên trong chuỗi JSON string
        var sb = new StringBuilder(cleaned.Length + 1024);
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < cleaned.Length; i++)
        {
            char c = cleaned[i];

            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                }
                sb.Append(c);
                continue;
            }

            // Đang ở bên trong chuỗi JSON string literal
            if (isEscaped)
            {
                isEscaped = false;

                // Kiểm tra xem ký tự sau backslash có phải escape JSON hợp lệ không
                if (c == '"' || c == '\\' || c == '/')
                {
                    sb.Append(c);
                }
                else if (c == 'b' || c == 'f' || c == 'n' || c == 'r' || c == 't')
                {
                    // Kiểm tra xem đây có phải là lệnh LaTeX bị nuốt không
                    bool isLatexCommand = false;
                    if (c == 'f' && i + 3 < cleaned.Length && cleaned.Substring(i, 4) == "frac") isLatexCommand = true;
                    else if (c == 't' && i + 3 < cleaned.Length && (cleaned.Substring(i, 4) == "text" || cleaned.Substring(i, 4) == "imes" || cleaned.Substring(i, 4) == "heta" || cleaned.Substring(i, 2) == "au")) isLatexCommand = true;
                    else if (c == 'b' && i + 3 < cleaned.Length && (cleaned.Substring(i, 4) == "egin" || cleaned.Substring(i, 3) == "bar" || cleaned.Substring(i, 3) == "eta")) isLatexCommand = true;
                    else if (c == 'n' && i + 2 < cleaned.Length && (cleaned.Substring(i, 2) == "eq" || cleaned.Substring(i, 3) == "abla" || cleaned.Substring(i, 4) == "otin")) isLatexCommand = true;
                    else if (c == 'r' && i + 2 < cleaned.Length && (cleaned.Substring(i, 2) == "ho" || cleaned.Substring(i, 4) == "ight")) isLatexCommand = true;

                    if (isLatexCommand)
                    {
                        // Thêm backslash để giữ nguyên lệnh LaTeX trong JSON: \\frac, \\text...
                        sb.Append('\\');
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else if (c == 'u')
                {
                    sb.Append(c);
                }
                else
                {
                    // BẮT BUỘC DOUBLE-ESCAPE CHO TẤT CẢ CÁC KÝ TỰ ESCAPE KHÔNG HỢP LỆ TRONG JSON:
                    // Ví dụ: \{, \}, \^, \_, \%, \$, \&, \#, \Delta, \alpha, \sqrt, \le, \ge, \pm...
                    sb.Append('\\');
                    sb.Append(c);
                }
                continue;
            }

            if (c == '\\')
            {
                isEscaped = true;
                sb.Append('\\');
                continue;
            }

            if (c == '"')
            {
                inString = false;
                sb.Append(c);
                continue;
            }

            if (c == '\n')
            {
                sb.Append("\\n");
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            if (c == '\t')
            {
                sb.Append("\\t");
                continue;
            }

            sb.Append(c);
        }

        string result = sb.ToString();

        // 3. Khôi phục JSON nếu bị cắt cụt do giới hạn token
        result = RepairTruncatedJson(result);

        return result;
    }

    private static string RepairTruncatedJson(string json)
    {
        string trimmed = json.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "{}";

        var stack = new Stack<char>();
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{' || c == '[')
            {
                stack.Push(c);
            }
            else if (c == '}' && stack.Count > 0 && stack.Peek() == '{')
            {
                stack.Pop();
            }
            else if (c == ']' && stack.Count > 0 && stack.Peek() == '[')
            {
                stack.Pop();
            }
        }

        if (stack.Count == 0 && !inString) return trimmed;

        var sb = new StringBuilder(trimmed);

        // Đóng ngoặc kép nếu bị cắt ngang trong chuỗi
        if (inString)
        {
            sb.Append('"');
        }

        // Loại bỏ các dấu phẩy lơ lửng hoặc thuộc tính chưa có giá trị trước khi đóng ngoặc
        string curr = sb.ToString();
        curr = Regex.Replace(curr, @",\s*$", "");
        curr = Regex.Replace(curr, @"""[^""]*""\s*:\s*$", "");
        curr = Regex.Replace(curr, @",\s*$", "");

        sb = new StringBuilder(curr);

        while (stack.Count > 0)
        {
            char open = stack.Pop();
            if (open == '{')
                sb.Append('}');
            else if (open == '[')
                sb.Append(']');
        }

        return sb.ToString();
    }

    private static bool IsStemDomain(string? domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName)) return true;
        string lower = domainName.ToLowerInvariant();
        if (lower.Contains("văn") || lower.Contains("ngữ") || lower.Contains("tiếng việt") ||
            lower.Contains("ngoại ngữ") || lower.Contains("anh") || lower.Contains("english") ||
            lower.Contains("sử") || lower.Contains("địa") || lower.Contains("xã hội") ||
            lower.Contains("định tính"))
        {
            return false;
        }
        return true;
    }

    private static ImportCurriculumRequestDto? TryParseCurriculumFromLooseJson(string rawJson, string domainId, string domainName)
    {
        try
        {
            var node = JsonNode.Parse(rawJson, new JsonNodeOptions(), new JsonDocumentOptions { AllowTrailingCommas = true });
            if (node == null) return null;

            var result = new ImportCurriculumRequestDto
            {
                DomainId = node["domainId"]?.GetValue<string>() ?? domainId,
                DomainName = node["domainName"]?.GetValue<string>() ?? domainName
            };

            var chaptersNode = node["chapters"] as JsonArray;
            if (chaptersNode == null) return null;

            foreach (var chNode in chaptersNode)
            {
                if (chNode == null) continue;
                var ch = new ImportChapterDto
                {
                    ChapterName = chNode["chapterName"]?.GetValue<string>() ?? "Chương học thuật",
                    Weight = chNode["weight"] != null ? chNode["weight"]!.GetValue<double>() : 0.25,
                    Description = chNode["description"]?.GetValue<string>()
                };

                var lessonsNode = chNode["lessons"] as JsonArray;
                if (lessonsNode != null)
                {
                    foreach (var lesNode in lessonsNode)
                    {
                        if (lesNode == null) continue;
                        var les = new ImportLessonDto
                        {
                            LessonName = lesNode["lessonName"]?.GetValue<string>() ?? "Bài học",
                            Weight = lesNode["weight"] != null ? lesNode["weight"]!.GetValue<double>() : 0.1,
                            Summary = lesNode["summary"]?.GetValue<string>()
                        };

                        var patternsNode = lesNode["patterns"] as JsonArray;
                        if (patternsNode != null)
                        {
                            foreach (var patNode in patternsNode)
                            {
                                if (patNode == null) continue;
                                var pat = new ImportPatternDto
                                {
                                    PatternName = patNode["patternName"]?.GetValue<string>() ?? "Dạng bài",
                                    Weight = patNode["weight"] != null ? patNode["weight"]!.GetValue<double>() : 0.05,
                                    Classification = patNode["classification"]?.GetValue<string>(),
                                    CoreSkill = patNode["coreSkill"]?.GetValue<string>(),
                                    TheoryRecap = patNode["theoryRecap"]?.GetValue<string>(),
                                    SolutionMethod = patNode["solutionMethod"]?.GetValue<string>(),
                                    VideoUrl = patNode["videoUrl"]?.GetValue<string>(),
                                    DurationSeconds = patNode["durationSeconds"] != null ? patNode["durationSeconds"]!.GetValue<int>() : 600
                                };

                                var questionsNode = patNode["questions"] as JsonArray;
                                if (questionsNode != null)
                                {
                                    foreach (var qNode in questionsNode)
                                    {
                                        if (qNode == null) continue;
                                        pat.Questions.Add(new ImportQuestionDto
                                        {
                                            DifficultyLevel = qNode["difficultyLevel"] != null ? qNode["difficultyLevel"]!.GetValue<int>() : 2,
                                            ContentLatex = qNode["contentLatex"]?.GetValue<string>() ?? string.Empty,
                                            OptionA = qNode["optionA"]?.GetValue<string>() ?? string.Empty,
                                            OptionB = qNode["optionB"]?.GetValue<string>() ?? string.Empty,
                                            OptionC = qNode["optionC"]?.GetValue<string>() ?? string.Empty,
                                            OptionD = qNode["optionD"]?.GetValue<string>() ?? string.Empty,
                                            CorrectOption = qNode["correctOption"]?.GetValue<string>() ?? "A",
                                            Explanation = qNode["explanation"]?.GetValue<string>()
                                        });
                                    }
                                }

                                les.Patterns.Add(pat);
                            }
                        }

                        ch.Lessons.Add(les);
                    }
                }

                result.Chapters.Add(ch);
            }

            return result.Chapters.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static ImportCurriculumRequestDto? ExtractCurriculumViaRegex(string text, string domainId, string domainName)
    {
        try
        {
            var result = new ImportCurriculumRequestDto
            {
                DomainId = domainId,
                DomainName = domainName
            };

            var domMatch = Regex.Match(text, @"""domainId""\s*:\s*""([^""]+)""");
            if (domMatch.Success) result.DomainId = domMatch.Groups[1].Value;

            var domNameMatch = Regex.Match(text, @"""domainName""\s*:\s*""([^""]+)""");
            if (domNameMatch.Success) result.DomainName = domNameMatch.Groups[1].Value;

            var chMatches = Regex.Matches(text, @"""chapterName""\s*:\s*""([^""]+)""");
            if (chMatches.Count == 0) return null;

            for (int c = 0; c < chMatches.Count; c++)
            {
                var chMatch = chMatches[c];
                int chStart = chMatch.Index;
                int chEnd = (c + 1 < chMatches.Count) ? chMatches[c + 1].Index : text.Length;
                string chBlock = text.Substring(chStart, chEnd - chStart);

                var ch = new ImportChapterDto
                {
                    ChapterName = chMatch.Groups[1].Value,
                    Weight = 0.35
                };

                var lesMatches = Regex.Matches(chBlock, @"""lessonName""\s*:\s*""([^""]+)""");
                if (lesMatches.Count == 0)
                {
                    lesMatches = Regex.Matches(chBlock, @"""chapterName""\s*:\s*""([^""]+)""");
                }

                for (int l = 0; l < lesMatches.Count; l++)
                {
                    var lesMatch = lesMatches[l];
                    int lesStart = lesMatch.Index;
                    int lesEnd = (l + 1 < lesMatches.Count) ? lesMatches[l + 1].Index : chBlock.Length;
                    string lesBlock = chBlock.Substring(lesStart, lesEnd - lesStart);

                    var les = new ImportLessonDto
                    {
                        LessonName = lesMatch.Groups[1].Value.StartsWith("Chương") ? "Bài 1: Trọng tâm chuyên đề" : lesMatch.Groups[1].Value,
                        Weight = 0.1
                    };

                    var patMatches = Regex.Matches(lesBlock, @"""patternName""\s*:\s*""([^""]+)""");
                    for (int p = 0; p < patMatches.Count; p++)
                    {
                        var patMatch = patMatches[p];
                        int patStart = patMatch.Index;
                        int patEnd = (p + 1 < patMatches.Count) ? patMatches[p + 1].Index : lesBlock.Length;
                        string patBlock = lesBlock.Substring(patStart, patEnd - patStart);

                        var pat = new ImportPatternDto
                        {
                            PatternName = patMatch.Groups[1].Value,
                            Classification = ExtractRegexField(patBlock, "classification") ?? "Dạng bài trọng tâm",
                            CoreSkill = ExtractRegexField(patBlock, "coreSkill") ?? "Tư duy giải toán",
                            TheoryRecap = ExtractRegexField(patBlock, "theoryRecap"),
                            SolutionMethod = ExtractRegexField(patBlock, "solutionMethod"),
                            DurationSeconds = 600,
                            Weight = 0.05
                        };

                        var qMatches = Regex.Matches(patBlock, @"""contentLatex""\s*:\s*""(.*?)(?<!\\)""", RegexOptions.Singleline);
                        for (int q = 0; q < qMatches.Count; q++)
                        {
                            var qMatch = qMatches[q];
                            int qStart = qMatch.Index;
                            int qEnd = (q + 1 < qMatches.Count) ? qMatches[q + 1].Index : patBlock.Length;
                            string qBlock = patBlock.Substring(qStart, qEnd - qStart);

                            int diff = 2;
                            var diffMatch = Regex.Match(qBlock, @"""difficultyLevel""\s*:\s*(\d+)");
                            if (diffMatch.Success && int.TryParse(diffMatch.Groups[1].Value, out int dVal))
                            {
                                diff = dVal;
                            }

                            string correct = ExtractRegexField(qBlock, "correctOption") ?? "A";
                            if (correct.Length > 1) correct = correct.Substring(0, 1);

                            string qContent = ExtractRegexField(qBlock, "contentLatex") ?? qMatch.Groups[1].Value;
                            pat.Questions.Add(new ImportQuestionDto
                            {
                                DifficultyLevel = diff,
                                ContentLatex = qContent,
                                OptionA = ExtractRegexField(qBlock, "optionA") ?? string.Empty,
                                OptionB = ExtractRegexField(qBlock, "optionB") ?? string.Empty,
                                OptionC = ExtractRegexField(qBlock, "optionC") ?? string.Empty,
                                OptionD = ExtractRegexField(qBlock, "optionD") ?? string.Empty,
                                CorrectOption = correct,
                                Explanation = ExtractRegexField(qBlock, "explanation")
                            });
                        }

                        les.Patterns.Add(pat);
                    }

                    if (les.Patterns.Count > 0)
                    {
                        ch.Lessons.Add(les);
                    }
                }

                if (ch.Lessons.Count > 0)
                {
                    result.Chapters.Add(ch);
                }
            }

            return result.Chapters.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static AnalyzePatternResponseDto? ExtractPatternAnalysisViaRegex(string text, string patternName)
    {
        try
        {
            string? name = ExtractRegexField(text, "pattern_name") ?? patternName;
            string? classification = ExtractRegexField(text, "classification") ?? "Khảo sát và Phân loại bài toán";
            string? coreSkill = ExtractRegexField(text, "core_skill") ?? "Tư duy suy luận";
            string? theory = ExtractRegexField(text, "theory_recap");
            string? solution = ExtractRegexField(text, "solution_method");
            string? traps = ExtractRegexField(text, "exam_traps");
            string? casio = ExtractRegexField(text, "casio_tricks");

            int bloom = 3;
            var bloomM = Regex.Match(text, @"""bloom_level""\s*:\s*(\d+)");
            if (bloomM.Success && int.TryParse(bloomM.Groups[1].Value, out int bVal)) bloom = bVal;

            var dto = new AnalyzePatternResponseDto
            {
                PatternName = name,
                Classification = classification,
                CoreSkill = coreSkill,
                BloomLevel = bloom,
                TheoryRecap = theory ?? string.Empty,
                SolutionMethod = solution ?? string.Empty,
                ExamTraps = traps ?? string.Empty,
                CasioTricks = casio ?? string.Empty
            };

            var qMatch = Regex.Match(text, @"""content_latex""\s*:\s*""(.*?)(?<!\\)""", RegexOptions.Singleline);
            if (qMatch.Success)
            {
                dto.SampleQuestion = new PatternExemplarQuestionDto
                {
                    DifficultyLevel = bloom,
                    ContentLatex = ExtractRegexField(text, "content_latex") ?? qMatch.Groups[1].Value,
                    OptionA = ExtractRegexField(text, "option_a") ?? string.Empty,
                    OptionB = ExtractRegexField(text, "option_b") ?? string.Empty,
                    OptionC = ExtractRegexField(text, "option_c") ?? string.Empty,
                    OptionD = ExtractRegexField(text, "option_d") ?? string.Empty,
                    CorrectOption = ExtractRegexField(text, "correct_option") ?? "A",
                    Explanation = ExtractRegexField(text, "explanation")
                };
            }

            return dto;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractRegexField(string block, string fieldName)
    {
        var m = Regex.Match(block, $@"""{fieldName}""\s*:\s*""(.*?)(?<!\\)""", RegexOptions.Singleline);
        if (m.Success)
        {
            string val = m.Groups[1].Value;
            // Bảo toàn các lệnh LaTeX bắt đầu bằng \t (\text, \times, \theta, \tau...) và \n (\neq, \notin, \nabla...)
            val = Regex.Replace(val, @"\\t(?![a-zA-Z])", "\t");
            val = Regex.Replace(val, @"\\n(?!(?:eq|otin|abla|u|atural|earrow|warrow|subseteq|supseteq|gtr|less|parallel)\b)", "\n");
            val = val.Replace(@"\""", "\"").Replace(@"\\", @"\");
            return val;
        }

        var numM = Regex.Match(block, $@"""{fieldName}""\s*:\s*([0-9\.]+)");
        if (numM.Success)
        {
            return numM.Groups[1].Value;
        }

        return null;
    }

    private static List<ImportQuestionDto> ExtractQuestionsViaRegex(string text)
    {
        var list = new List<ImportQuestionDto>();
        try
        {
            // Tìm tất cả các vị trí xuất hiện của trường contentLatex hoặc content_latex
            var qKeyMatches = Regex.Matches(text, @"""(?:contentLatex|content_latex)""\s*:\s*""", RegexOptions.Singleline);
            if (qKeyMatches.Count == 0) return list;

            for (int i = 0; i < qKeyMatches.Count; i++)
            {
                int keyIndex = qKeyMatches[i].Index;
                int blockStart = text.LastIndexOf('{', keyIndex);
                if (blockStart < 0) blockStart = keyIndex;

                int blockEnd;
                if (i + 1 < qKeyMatches.Count)
                {
                    int nextKeyIndex = qKeyMatches[i + 1].Index;
                    int nextOpenBrace = text.LastIndexOf('{', nextKeyIndex);
                    blockEnd = nextOpenBrace > blockStart ? nextOpenBrace : nextKeyIndex;
                }
                else
                {
                    int arrayClose = text.IndexOf(']', keyIndex);
                    blockEnd = arrayClose > keyIndex ? arrayClose : text.Length;
                }

                string block = text.Substring(blockStart, blockEnd - blockStart);
                string? content = ExtractRegexField(block, "contentLatex") ?? ExtractRegexField(block, "content_latex");
                if (string.IsNullOrWhiteSpace(content)) continue;

                int diff = 3;
                var diffM = Regex.Match(block, @"""(?:difficultyLevel|difficulty_level)""\s*:\s*(\d+)");
                if (diffM.Success && int.TryParse(diffM.Groups[1].Value, out int dVal)) diff = dVal;

                list.Add(new ImportQuestionDto
                {
                    DifficultyLevel = diff,
                    ContentLatex = content,
                    OptionA = ExtractRegexField(block, "optionA") ?? ExtractRegexField(block, "option_a") ?? string.Empty,
                    OptionB = ExtractRegexField(block, "optionB") ?? ExtractRegexField(block, "option_b") ?? string.Empty,
                    OptionC = ExtractRegexField(block, "optionC") ?? ExtractRegexField(block, "option_c") ?? string.Empty,
                    OptionD = ExtractRegexField(block, "optionD") ?? ExtractRegexField(block, "option_d") ?? string.Empty,
                    CorrectOption = ExtractRegexField(block, "correctOption") ?? ExtractRegexField(block, "correct_option") ?? "A",
                    Explanation = ExtractRegexField(block, "explanation") ?? string.Empty
                });
            }
        }
        catch
        {
            // Ignore
        }
        return list;
    }
}
