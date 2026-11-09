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
    private readonly IPromptTemplateService _promptTemplateService;

    public CurriculumAiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CurriculumAiService> logger,
        IPromptTemplateService promptTemplateService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _promptTemplateService = promptTemplateService;
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

    private List<string> ResolveGeminiModels()
    {
        var models = _configuration.GetSection("AiSettings:GeminiModels").Get<string[]>();
        if (models != null && models.Length > 0)
        {
            return models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct()
                .ToList();
        }

        var single = _configuration["AiSettings:GeminiModel"];
        if (!string.IsNullOrWhiteSpace(single))
        {
            return new List<string> { single.Trim() };
        }

        return new List<string> { 
            "gemini-3.6-flash",
            "gemini-3.5-flash-lite",
            "gemini-3.1-flash-lite",
            "gemini-flash-lite-latest",
            "gemini-3.8-flash"
        };
    }

    private string? ResolveOpenAiKey()
    {
        var key = _configuration["AiSettings:ApiKey"] 
                  ?? _configuration["AiSettings:OpenAiApiKey"]
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(key)) return null;

        key = key.Trim();
        if (key.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)) return null;

        return key;
    }

    private List<string> ResolveOpenAiModels()
    {
        var models = _configuration.GetSection("AiSettings:OpenAiModels").Get<string[]>();
        if (models != null && models.Length > 0)
        {
            return models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct()
                .ToList();
        }

        return new List<string> { "gpt-4o-mini", "gpt-4o", "o3-mini", "chatgpt-4o-latest" };
    }

    private async Task<string?> TryCallOpenAiCurriculumAsync(
        string modelName,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        var requestBody = new JsonObject
        {
            ["model"] = modelName,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "BẠN LÀ HỆ THỐNG TRÍ TUỆ NHÂN TẠO HỌC THUẬT & TRÍ THỨC SƯ PHẠM V-EVAL. Luôn phản hồi duy nhất 1 chuỗi JSON hợp lệ 100% theo đúng cấu trúc yêu cầu, không sinh text giải thích ngoài JSON."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            },
            ["response_format"] = new JsonObject { ["type"] = "json_object" }
        };

        if (modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase))
        {
            requestBody["max_completion_tokens"] = 32768;
        }
        else
        {
            requestBody["temperature"] = 0.1;
            requestBody["max_tokens"] = 16384;
        }

        string openAiUrl = _configuration["AiSettings:OpenAiBaseUrl"] ?? "https://api.openai.com/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, openAiUrl)
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        var response = await _httpClient.SendAsync(request, linkedCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("OpenAI Model '{Model}' thất bại với HTTP {Status}: {Body}", 
                modelName, (int)response.StatusCode, errBody.Length > 250 ? errBody[..250] : errBody);
            return null;
        }

        string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(respJson);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
        }

        return null;
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

        string depthDirective = $@"YÊU CẦU BÓC TÁCH CẤU TRÚC TRI THỨC VÀ CÁC DẠNG BÀI TRỌNG TÂM (CURRICULUM INGESTION DIRECTIVE):
- Phân tích bám sát 100% nội dung thực tế từ tài liệu thuộc lĩnh vực: {domainName}.
- Cấu trúc bóc tách gồm:
  + Từ 2 đến 4 Bài học (`lessons`) bao quát trọn vẹn chủ đề/chuyên đề trong tài liệu.
  + Mỗi Bài học nhận diện từ 2 đến 3 Dạng bài (`patterns`) cốt lõi (tổng cộng 4 đến 8 dạng bài cho toàn chuyên đề).
  + Mỗi Dạng bài có từ 2 đến 3 Câu hỏi bài mẫu (`questions`) đại diện các cấp độ Bloom (Nhận biết/Thông hiểu và Vận dụng/Vận dụng cao), có KaTeX chuẩn xác, đáp án và lời giải chi tiết.";

        // 2. Thử gọi Google Gemini qua C# HttpClient
        var apiKeys = ResolveApiKeys();
        var geminiModels = ResolveGeminiModels();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        bool isStem = IsStemDomain(domainName);
        string domainScope = isStem ? "TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS)" : "TỪNG DẠNG BÀI ĐỌC HIỂU / LÀM VĂN / BÀI HỌC CỤ THỂ (PATTERNS)";
        string sampleHeading = isStem ? "### 📄 Bài toán mẫu áp dụng phương pháp:" : "### 📄 Bài mẫu áp dụng phương pháp:";
        string tipHeading = isStem ? "### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:" : "### 💡 Mẹo tư duy & Chiến thuật làm bài:";
        string tipGuideline = isStem 
            ? "Mẹo nhẩm hoặc kỹ thuật bấm máy Casio (fx-580VN X, fx-880BTG)" 
            : "Mẹo gạch chân từ khóa, phân bổ thời gian, chiến thuật hành văn ghi điểm tuyệt đối (TUYỆT ĐỐI KHÔNG sinh mẹo bấm máy Casio cho môn Ngữ văn/Khoa học xã hội)";
        string sampleKey = isStem ? "Bài toán mẫu:" : "Bài mẫu:";

        var promptVariables = new Dictionary<string, string>
        {
            ["domainName"] = domainName,
            ["domainScope"] = domainScope,
            ["depthDirective"] = depthDirective,
            ["sampleHeading"] = sampleHeading,
            ["tipHeading"] = tipHeading,
            ["tipGuideline"] = tipGuideline,
            ["sampleKey"] = sampleKey,
            ["domainId"] = domainId,
            ["textSnippet"] = textSnippet
        };

        string prompt = await _promptTemplateService.RenderPromptAsync("CurriculumIngestion.prompt.txt", promptVariables, cancellationToken);

        int minChaptersThreshold = _configuration.GetValue<int>("AiSettings:MinChaptersThreshold", 1);
        if (minChaptersThreshold <= 0) minChaptersThreshold = 1;

        if (apiKeys.Count > 0)
        {
            foreach (var modelName in geminiModels)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Tiến trình bóc tách đã bị hủy từ phía Client/Gateway.");
                    break;
                }

                foreach (var key in apiKeys)
                {
                    if (cancellationToken.IsCancellationRequested) break;

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

                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
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

                                    _logger.LogInformation("C# AI Engine trích xuất thành công {Count} chương DỮ LIỆU THẬT trong {Elapsed:F2}s ({Ms} ms) từ tệp '{FileName}' bằng mô hình '{Model}'.", 
                                        parsed.Chapters.Count, parsed.ProcessingTimeSeconds, stopwatch.ElapsedMilliseconds, parsed.SourceFileName, modelName);
                                    return parsed;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Gemini API model '{Model}' trả về HTTP {StatusCode}, tiếp tục chuyển đổi model/key tiếp theo.", 
                                modelName, (int)response.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogWarning("Tiến trình bóc tách đã bị hủy từ phía Client/Gateway ({Message}). Dừng toàn bộ các model dự phòng.", ex.Message);
                            break;
                        }
                        _logger.LogWarning("Gọi Gemini API model '{Model}' thất bại ({Message}), tiếp tục kiểm tra key/model tiếp theo.", modelName, ex.Message);
                    }
                }
            }
        }

        // 3. TẦNG 2: FAILOVER SANG MÔ HÌNH OPENAI (GPT-4o-mini / GPT-4o / o3-mini) CHO BÓC TÁCH KHUNG CHƯƠNG TRÌNH
        var openAiKey = ResolveOpenAiKey();
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            var openAiModels = ResolveOpenAiModels();
            int maxAttempts = _configuration.GetValue<int>("AiSettings:MaxAttempts", 5);
            if (maxAttempts <= 0) maxAttempts = 5;

            _logger.LogInformation("Kích hoạt cơ chế Failover sang OpenAI cho bóc tách khung chương trình (Phát hiện {Count} mô hình GPT dự phòng)...", openAiModels.Count);

            int attempt = 0;
            foreach (var gptModel in openAiModels)
            {
                if (cancellationToken.IsCancellationRequested || attempt >= maxAttempts) break;
                attempt++;

                try
                {
                    _logger.LogInformation("[Fallback Curriculum lần {Attempt}/{MaxAttempts}] Đang gửi yêu cầu bóc tách tới OpenAI '{ModelName}'...", attempt, maxAttempts, gptModel);
                    string? gptResult = await TryCallOpenAiCurriculumAsync(gptModel, openAiKey, prompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        string contentText = SanitizeAndRepairGeminiJson(gptResult);
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
                            _logger.LogWarning("OpenAI '{Model}' Strict JSON deserialize gặp lỗi ({Msg}), chuyển sang bộ phân tích JsonNode mềm dẻo...", gptModel, jsonEx.Message);
                            parsed = TryParseCurriculumFromLooseJson(contentText, domainId, domainName);
                        }

                        if (parsed == null || parsed.Chapters.Count < minChaptersThreshold)
                        {
                            _logger.LogWarning("OpenAI '{Model}' JsonNode phân tích dưới ngưỡng an toàn ({Count} < {Threshold}), kích hoạt bộ bóc tách Regex Real-Data Engine...", 
                                gptModel, parsed?.Chapters.Count ?? 0, minChaptersThreshold);
                            parsed = ExtractCurriculumViaRegex(contentText, domainId, domainName);
                        }

                        if (parsed != null && parsed.Chapters.Count >= minChaptersThreshold)
                        {
                            stopwatch.Stop();
                            parsed.ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
                            parsed.IsRealExtraction = true;
                            parsed.SourceFileName = fileName ?? "Direct_Text_Input";
                            parsed.TotalPagesProcessed = totalPagesProcessed > 0 ? totalPagesProcessed : 1;
                            parsed.TotalCharactersExtracted = extractedText.Length;

                            _logger.LogInformation("OpenAI Failover '{Model}' trích xuất thành công {Count} chương DỮ LIỆU THẬT trong {Elapsed:F2}s từ tệp '{FileName}'.", 
                                gptModel, parsed.Chapters.Count, parsed.ProcessingTimeSeconds, parsed.SourceFileName);
                            return parsed;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi gọi OpenAI '{Model}' trong luồng bóc tách khung chương trình. Thử model tiếp theo...", gptModel);
                }
            }
        }

        stopwatch.Stop();
        string errDetail = apiKeys.Count == 0 && string.IsNullOrWhiteSpace(openAiKey)
            ? "Chưa cấu hình GEMINI_API_KEY hoặc OPENAI_API_KEY trong hệ thống."
            : "Các mô hình AI (Gemini / OpenAI) không thể bóc tách cấu trúc tri thức hoặc kết nối mạng bị gián đoạn.";
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
        var geminiModels = ResolveGeminiModels();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        bool isStem = IsStemDomain(domainName);
        string subjectType = isStem ? "toán" : "đọc hiểu / làm văn / bài học";
        string casioGuideline = isStem 
            ? "Kỹ thuật bấm máy Casio (fx-580VN X, fx-880BTG) hoặc mẹo tư duy nhanh." 
            : "Mẹo tư duy nhanh, chiến thuật đọc hiểu/hành văn, kỹ thuật phân bổ thời gian và mẹo ghi điểm tối đa ĐGNL (TUYỆT ĐỐI KHÔNG đề cập máy tính Casio vì đây là môn Ngữ văn / Khoa học xã hội).";

        var promptVariables = new Dictionary<string, string>
        {
            ["subjectType"] = subjectType,
            ["patternName"] = patternName,
            ["lessonName"] = lessonName,
            ["domainName"] = domainName,
            ["casioGuideline"] = casioGuideline
        };

        string prompt = await _promptTemplateService.RenderPromptAsync("PatternAnalysis.prompt.txt", promptVariables, cancellationToken);

        if (apiKeys.Count > 0)
        {
            foreach (var modelName in geminiModels)
            {
                if (cancellationToken.IsCancellationRequested) break;

                foreach (var key in apiKeys)
                {
                    if (cancellationToken.IsCancellationRequested) break;

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
                                    _logger.LogInformation("C# AI Engine nhận diện thành công dạng bài '{Pattern}' DỮ LIỆU THẬT trong {Elapsed:F2}s bằng mô hình '{Model}'.", patternName, parsed.ProcessingTimeSeconds, modelName);
                                    return parsed;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Gemini AI pattern analysis model '{Model}' trả về HTTP {StatusCode}, tiếp tục chuyển đổi model/key tiếp theo.", 
                                modelName, (int)response.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        _logger.LogWarning("Gemini AI pattern analysis model '{Model}' error: {Message}", modelName, ex.Message);
                    }
                }
            }
        }

        // 3. TẦNG 2: FAILOVER SANG MÔ HÌNH OPENAI (GPT-4o-mini / GPT-4o) CHO PHÂN TÍCH DẠNG BÀI
        var openAiKey = ResolveOpenAiKey();
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            var openAiModels = ResolveOpenAiModels();
            int maxAttempts = _configuration.GetValue<int>("AiSettings:MaxAttempts", 5);
            if (maxAttempts <= 0) maxAttempts = 5;

            _logger.LogInformation("Kích hoạt cơ chế Failover sang OpenAI cho phân tích dạng bài (Phát hiện {Count} mô hình GPT dự phòng)...", openAiModels.Count);

            int attempt = 0;
            foreach (var gptModel in openAiModels)
            {
                if (cancellationToken.IsCancellationRequested || attempt >= maxAttempts) break;
                attempt++;

                try
                {
                    _logger.LogInformation("[Fallback Pattern lần {Attempt}/{MaxAttempts}] Đang gửi yêu cầu phân tích tới OpenAI '{ModelName}'...", attempt, maxAttempts, gptModel);
                    string? gptResult = await TryCallOpenAiCurriculumAsync(gptModel, openAiKey, prompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        string contentText = SanitizeAndRepairGeminiJson(gptResult);
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
                            _logger.LogWarning("OpenAI '{Model}' JSON deserialize pattern gặp lỗi ({Msg}), thử Regex...", gptModel, jsonEx.Message);
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
                            _logger.LogInformation("OpenAI Failover '{Model}' nhận diện thành công dạng bài '{Pattern}' trong {Elapsed:F2}s!", gptModel, patternName, parsed.ProcessingTimeSeconds);
                            return parsed;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi gọi OpenAI '{Model}' trong luồng phân tích dạng bài. Thử model tiếp theo...", gptModel);
                }
            }
        }

        stopwatch.Stop();
        string errDetail = apiKeys.Count == 0 && string.IsNullOrWhiteSpace(openAiKey)
            ? "Chưa cấu hình GEMINI_API_KEY hoặc OPENAI_API_KEY trong hệ thống."
            : "Các mô hình AI (Gemini / OpenAI) không thể phân tích dạng bài hoặc kết nối mạng bị gián đoạn.";
        throw new InvalidOperationException($"Không thể phân tích dạng bài '{patternName}' từ AI: {errDetail}.");
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
        var geminiModels = ResolveGeminiModels();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

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

        var promptVariables = new Dictionary<string, string>
        {
            ["count"] = count.ToString(),
            ["patternName"] = patternName,
            ["lessonName"] = lessonName,
            ["domainName"] = domainName,
            ["theoryRecap"] = theoryRecap,
            ["solutionMethod"] = solutionMethod,
            ["antiDuplicationNote"] = antiDuplicationNote
        };

        string prompt = await _promptTemplateService.RenderPromptAsync("PracticeQuestionGenerator.prompt.txt", promptVariables, cancellationToken);

        if (apiKeys.Count > 0)
        {
            foreach (var modelName in geminiModels)
            {
                if (cancellationToken.IsCancellationRequested) break;

                foreach (var key in apiKeys)
                {
                    if (cancellationToken.IsCancellationRequested) break;

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
                                            CorrectOption = NormalizeCorrectOption(ExtractJsonNodeAsString(qNode["correctOption"] ?? qNode["correct_option"])),
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
                        else
                        {
                            _logger.LogWarning("Gemini AI generate pattern questions model '{Model}' trả về HTTP {StatusCode}, tiếp tục chuyển đổi model/key tiếp theo.", 
                                modelName, (int)response.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        _logger.LogWarning("Gemini AI generate pattern questions model '{Model}' error: {Message}", modelName, ex.Message);
                    }
                }
            }
        }

        // 3. TẦNG 2: FAILOVER SANG MÔ HÌNH OPENAI (GPT-4o-mini / GPT-4o) CHO SINH CÂU HỎI
        var openAiKey = ResolveOpenAiKey();
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            var openAiModels = ResolveOpenAiModels();
            int maxAttempts = _configuration.GetValue<int>("AiSettings:MaxAttempts", 5);
            if (maxAttempts <= 0) maxAttempts = 5;

            _logger.LogInformation("Kích hoạt cơ chế Failover sang OpenAI cho sinh câu hỏi dạng bài...");

            int attempt = 0;
            foreach (var gptModel in openAiModels)
            {
                if (cancellationToken.IsCancellationRequested || attempt >= maxAttempts) break;
                attempt++;

                try
                {
                    _logger.LogInformation("[Fallback Question lần {Attempt}/{MaxAttempts}] Đang gửi yêu cầu tới OpenAI '{ModelName}'...", attempt, maxAttempts, gptModel);
                    string? gptResult = await TryCallOpenAiCurriculumAsync(gptModel, openAiKey, prompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        string contentText = SanitizeAndRepairGeminiJson(gptResult);
                        var resultQuestions = new List<ImportQuestionDto>();

                        try
                        {
                            var docNode = JsonNode.Parse(contentText);
                            var questionsArray = docNode?["questions"]?.AsArray();
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
                                        CorrectOption = NormalizeCorrectOption(ExtractJsonNodeAsString(qNode["correctOption"] ?? qNode["correct_option"])),
                                        Explanation = qNode["explanation"]?.GetValue<string>() ?? string.Empty
                                    };
                                    if (!string.IsNullOrWhiteSpace(q.ContentLatex))
                                    {
                                        resultQuestions.Add(q);
                                    }
                                }
                            }
                        }
                        catch (Exception jsonEx)
                        {
                            _logger.LogWarning("OpenAI '{Model}' JSON deserialize câu hỏi gặp lỗi ({Msg}), thử Regex...", gptModel, jsonEx.Message);
                            resultQuestions = ExtractQuestionsViaRegex(contentText);
                        }

                        if (resultQuestions.Count == 0)
                        {
                            resultQuestions = ExtractQuestionsViaRegex(contentText);
                        }

                        if (resultQuestions.Count > 0)
                        {
                            stopwatch.Stop();
                            _logger.LogInformation("OpenAI Failover '{Model}' sinh thành công {Count} câu hỏi cho dạng bài '{Pattern}' trong {Elapsed:F2}s!", gptModel, resultQuestions.Count, patternName, stopwatch.Elapsed.TotalSeconds);
                            return new GeneratePatternQuestionsResponseDto
                            {
                                Questions = resultQuestions,
                                ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2)
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi gọi OpenAI '{Model}' trong luồng sinh câu hỏi. Thử model tiếp theo...", gptModel);
                }
            }
        }

        stopwatch.Stop();
        string errDetail = apiKeys.Count == 0 && string.IsNullOrWhiteSpace(openAiKey)
            ? "Chưa cấu hình GEMINI_API_KEY hoặc OPENAI_API_KEY trong hệ thống."
            : "Các mô hình AI (Gemini / OpenAI) không thể sinh câu hỏi bài tập hoặc kết nối mạng bị gián đoạn.";
        throw new InvalidOperationException($"Không thể tạo thêm câu hỏi cho dạng bài '{patternName}' từ AI: {errDetail}.");
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

        // 0.3 Sửa lỗi Gemini quên bọc ngoặc kép ở giá trị chuỗi (ví dụ: "optionA": m in (SAC), -> "optionA": "m in (SAC)",)
        cleaned = Regex.Replace(cleaned, @"(?<=""(?:option[A-D]|correctOption|classification|coreSkill)""\s*:\s*)(?![""{\[\d]|true|false|null)([^,\n\r]+)(?=[,\n\r])", "\"$1\"");

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
                // Kiểm tra xem đây là dấu ngoặc kép đóng chuỗi hợp lệ hay dấu ngoặc kép lơ lửng bên trong nội dung
                int nextIdx = i + 1;
                while (nextIdx < cleaned.Length && (cleaned[nextIdx] == ' ' || cleaned[nextIdx] == '\t' || cleaned[nextIdx] == '\r' || cleaned[nextIdx] == '\n'))
                {
                    nextIdx++;
                }

                char nextChar = nextIdx < cleaned.Length ? cleaned[nextIdx] : '\0';

                // Trong JSON chuẩn, dấu ngoặc kép đóng chuỗi BẮT BUỘC phải đi liền trước một trong các ký tự:
                // ',' (phần tử hoặc thuộc tính tiếp theo)
                // '}' (kết thúc object)
                // ']' (kết thúc array)
                // ':' (kết thúc key/tên thuộc tính)
                // '\0' (cuối văn bản)
                bool isClosingQuote = (nextChar == ',' || nextChar == '}' || nextChar == ']' || nextChar == ':' || nextChar == '\0');

                if (!isClosingQuote && nextChar == '"')
                {
                    // Trường hợp AI quên dấu phẩy giữa 2 thuộc tính: "prop1": "val1" \n "prop2": "val2"
                    string sub = cleaned.Substring(nextIdx);
                    if (Regex.IsMatch(sub, @"^""[a-zA-Z0-9_]{2,40}""\s*:"))
                    {
                        inString = false;
                        sb.Append("\",");
                        i = nextIdx - 1;
                        continue;
                    }
                }

                if (isClosingQuote)
                {
                    inString = false;
                    sb.Append(c);
                }
                else
                {
                    // Dấu ngoặc kép không escape ở giữa nội dung tiếng Việt (ví dụ: "Đoạn thẳng", "M", "N"...)
                    // Thay thế bằng nháy đơn để bảo vệ cấu trúc cú pháp JSON 100%
                    sb.Append('\'');
                }
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
                                    PatternName = patNode["patternName"]?.GetValue<string>() ?? patNode["pattern_name"]?.GetValue<string>() ?? "Dạng bài",
                                    Weight = patNode["weight"] != null ? patNode["weight"]!.GetValue<double>() : 0.05,
                                    Classification = patNode["classification"]?.GetValue<string>(),
                                    CoreSkill = patNode["coreSkill"]?.GetValue<string>() ?? patNode["core_skill"]?.GetValue<string>(),
                                    TheoryRecap = patNode["theoryRecap"]?.GetValue<string>() ?? patNode["theory_recap"]?.GetValue<string>() ?? string.Empty,
                                    SolutionMethod = patNode["solutionMethod"]?.GetValue<string>() ?? patNode["solution_method"]?.GetValue<string>() ?? string.Empty,
                                    VideoUrl = patNode["videoUrl"]?.GetValue<string>() ?? patNode["video_url"]?.GetValue<string>(),
                                    DurationSeconds = patNode["durationSeconds"] != null ? patNode["durationSeconds"]!.GetValue<int>() : (patNode["duration_seconds"] != null ? patNode["duration_seconds"]!.GetValue<int>() : 600)
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
                                            CorrectOption = NormalizeCorrectOption(ExtractJsonNodeAsString(qNode["correctOption"] ?? qNode["correct_option"])),
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
                            CoreSkill = ExtractRegexField(patBlock, "coreSkill") ?? ExtractRegexField(patBlock, "core_skill") ?? "Tư duy giải toán",
                            TheoryRecap = ExtractRegexField(patBlock, "theoryRecap") ?? ExtractRegexField(patBlock, "theory_recap") ?? string.Empty,
                            SolutionMethod = ExtractRegexField(patBlock, "solutionMethod") ?? ExtractRegexField(patBlock, "solution_method") ?? string.Empty,
                            VideoUrl = ExtractRegexField(patBlock, "videoUrl") ?? ExtractRegexField(patBlock, "video_url"),
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

                            string correct = NormalizeCorrectOption(ExtractRegexField(qBlock, "correctOption") ?? ExtractRegexField(qBlock, "correct_option"));

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
                    CorrectOption = NormalizeCorrectOption(ExtractRegexField(text, "correct_option")),
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

    private static string NormalizeCorrectOption(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "A";
        raw = raw.Trim();

        // 1. Nếu chỉ là 1 ký tự hoặc có dấu bao quanh như "A.", "(B)", "[C]", "D:"
        string stripped = raw.Trim(" \t\r\n.:();,[]'\"".ToCharArray());
        if (stripped.Length == 1)
        {
            char c = char.ToUpperInvariant(stripped[0]);
            if (c >= 'A' && c <= 'D') return c.ToString();
            if (c >= '0' && c <= '3') return ((char)('A' + (c - '0'))).ToString();
            if (c >= '1' && c <= '4') return ((char)('A' + (c - '1'))).ToString();
        }

        // 2. Tìm khớp từ khóa (Đáp án A, Phương án B, Option C, Answer D, Chọn A...)
        var match = Regex.Match(raw, @"(?:(?:đáp\s*án|phương\s*án|chọn|câu|answer|choice|option)\s*[:\-\.]?\s*)?([A-D])\b", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups[1].Success)
        {
            return match.Groups[1].Value.ToUpperInvariant();
        }

        // 3. Quét tìm ký tự A, B, C, D đầu tiên
        foreach (char ch in raw)
        {
            char upper = char.ToUpperInvariant(ch);
            if (upper >= 'A' && upper <= 'D') return upper.ToString();
        }

        // 4. Quét số 0..3 trong chuỗi
        foreach (char ch in raw)
        {
            if (ch >= '0' && ch <= '3') return ((char)('A' + (ch - '0'))).ToString();
            if (ch >= '1' && ch <= '4') return ((char)('A' + (ch - '1'))).ToString();
        }

        return "A";
    }

    private static string ExtractJsonNodeAsString(JsonNode? node, string fallback = "")
    {
        if (node == null) return fallback;
        if (node is JsonValue jVal)
        {
            if (jVal.TryGetValue<string>(out var s)) return s;
            if (jVal.TryGetValue<int>(out var i))
            {
                if (i >= 0 && i <= 3) return ((char)('A' + i)).ToString();
                return i.ToString();
            }
            return jVal.ToString();
        }
        return node.ToString() ?? fallback;
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
                    CorrectOption = NormalizeCorrectOption(ExtractRegexField(block, "correctOption") ?? ExtractRegexField(block, "correct_option")),
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
