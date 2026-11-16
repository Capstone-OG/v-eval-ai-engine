using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
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

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length <= 10) return "****";
        return $"{key[..6]}...{key[^6..]}";
    }

    private List<string> ResolveApiKeys()
    {
        var keys = new List<string>();

        // 1. Ưu tiên khóa chính AiSettings:GeminiApiKey
        var singleOrDelimited = _configuration.GetSection("AiSettings")["GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(singleOrDelimited))
        {
            var splitKeys = singleOrDelimited.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            keys.AddRange(splitKeys);
        }

        // 2. Khóa xoay tua dự phòng AiSettings:GeminiApiKeys
        var keysSection = _configuration.GetSection("AiSettings:GeminiApiKeys").Get<string[]>();
        if (keysSection != null && keysSection.Length > 0)
        {
            keys.AddRange(keysSection);
        }

        // 3. Biến môi trường hệ thống
        var envKeys = Environment.GetEnvironmentVariable("GEMINI_API_KEYS") 
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEYS");
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
            "gemini-3.5-flash",
            "gemini-3.5-flash-lite",
            "gemini-flash-lite-latest"
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
        CancellationToken cancellationToken,
        List<string>? base64Images = null)
    {
        JsonNode userContentNode;
        if (base64Images != null && base64Images.Count > 0)
        {
            var userArray = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt
                }
            };
            foreach (var b64 in base64Images)
            {
                userArray.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:image/jpeg;base64,{b64}",
                        ["detail"] = "low"
                    }
                });
            }
            userContentNode = userArray;
        }
        else
        {
            userContentNode = prompt;
        }

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
                    ["content"] = userContentNode
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

    /// <summary>
    /// Trích xuất và nén tối ưu hóa hình ảnh các trang PDF đại diện cho các tài liệu dạng scan/ảnh (không có lớp text số hóa)
    /// Giữ số lượng ảnh tinh gọn (mặc định tối đa 5 trang đại diện, sắc nét 120 DPI) để giảm tải payload và tránh timeout.
    /// </summary>
    private List<string> ExtractDownscaledPageImages(
        byte[] pdfBytes,
        int targetStart,
        int targetEnd,
        int maxImages = 5)
    {
        var result = new List<string>();
        try
        {
            using var pdf = PdfDocument.Open(pdfBytes);
            var matchingPages = pdf.GetPages()
                .Where(p => p.Number >= targetStart && p.Number <= targetEnd)
                .ToList();

            if (matchingPages.Count == 0) return result;

            List<Page> selectedPages;
            if (matchingPages.Count <= maxImages)
            {
                selectedPages = matchingPages;
            }
            else
            {
                selectedPages = new List<Page> { matchingPages[0] };

                int middleSlots = maxImages - 2;
                if (middleSlots > 0)
                {
                    double step = (double)(matchingPages.Count - 1) / (middleSlots + 1);
                    for (int i = 1; i <= middleSlots; i++)
                    {
                        int index = (int)Math.Round(i * step);
                        if (index > 0 && index < matchingPages.Count - 1 && !selectedPages.Contains(matchingPages[index]))
                        {
                            selectedPages.Add(matchingPages[index]);
                        }
                    }
                }

                if (!selectedPages.Contains(matchingPages[^1]))
                {
                    selectedPages.Add(matchingPages[^1]);
                }
                selectedPages = selectedPages.OrderBy(p => p.Number).ToList();
            }

            foreach (var page in selectedPages)
            {
                byte[]? jpegBytes = null;

                // 1. Thử lấy ảnh gốc nhúng trong trang qua PdfPig (dành cho file PDF scan toàn trang)
                var rawImages = page.GetImages().ToList();
                if (rawImages.Count > 0)
                {
                    var primaryImg = rawImages.OrderByDescending(i => i.WidthInSamples * i.HeightInSamples).FirstOrDefault();
                    if (primaryImg != null)
                    {
                        var rawBytes = primaryImg.RawBytes.ToArray();
                        using var originalBitmap = SKBitmap.Decode(rawBytes);
                        if (originalBitmap != null)
                        {
                            int maxDim = Math.Max(originalBitmap.Width, originalBitmap.Height);
                            float scale = maxDim > 1024 ? 1024f / maxDim : 1.0f;
                            int targetW = (int)(originalBitmap.Width * scale);
                            int targetH = (int)(originalBitmap.Height * scale);

                            using var resized = originalBitmap.Resize(new SKImageInfo(targetW, targetH), SKSamplingOptions.Default);
                            using var skImage = SKImage.FromBitmap(resized);
                            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 75);
                            jpegBytes = encoded.ToArray();
                        }
                    }
                }

                // 2. Nếu trang dạng vector không có ảnh nhúng, dùng PDFtoImage kết xuất sang bitmap độ phân giải rõ nét Dpi = 120
                if (jpegBytes == null)
                {
                    using var ms = new MemoryStream(pdfBytes);
                    using var renderedBitmap = Conversion.ToImage(ms, page: page.Number - 1, options: new RenderOptions { Dpi = 120 });
                    if (renderedBitmap != null)
                    {
                        int maxDim = Math.Max(renderedBitmap.Width, renderedBitmap.Height);
                        float scale = maxDim > 1024 ? 1024f / maxDim : 1.0f;
                        int targetW = (int)(renderedBitmap.Width * scale);
                        int targetH = (int)(renderedBitmap.Height * scale);

                        using var resized = renderedBitmap.Resize(new SKImageInfo(targetW, targetH), SKSamplingOptions.Default);
                        using var skImage = SKImage.FromBitmap(resized);
                        using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 75);
                        jpegBytes = encoded.ToArray();
                    }
                }

                if (jpegBytes != null && jpegBytes.Length > 0)
                {
                    result.Add(Convert.ToBase64String(jpegBytes));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi trích xuất hình ảnh trang PDF sang multimodal.");
        }

        return result;
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
        string? ocrMode = "STEM_FORMULAS",
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int totalPagesProcessed = 0;
        string extractedText = string.Empty;
        var pageImagesBase64 = new List<string>();
        byte[]? pdfBytes = null;
        int maxVisionImages = _configuration.GetValue<int>("AiSettings:MaxVisionPages", 5);
        if (maxVisionImages <= 0) maxVisionImages = 5;

        // 1. Đọc văn bản từ tệp nếu có
        if (fileStream != null && !string.IsNullOrEmpty(fileName))
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".pdf")
            {
                using var memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, cancellationToken);
                pdfBytes = memoryStream.ToArray();

                int actualTextChars = 0;
                int targetStart = startPage.HasValue && startPage.Value > 0 ? startPage.Value : 1;
                int targetEnd = endPage.HasValue && endPage.Value >= targetStart ? endPage.Value : int.MaxValue;

                try
                {
                    using var pdf = PdfDocument.Open(pdfBytes);
                    var sb = new StringBuilder();

                    foreach (var page in pdf.GetPages())
                    {
                        if (page.Number < targetStart) continue;
                        if (page.Number > targetEnd) break;
                        totalPagesProcessed++;
                        string pText = page.Text ?? string.Empty;
                        actualTextChars += pText.Trim().Length;
                        sb.AppendLine($"--- Trang {page.Number} ---");
                        sb.AppendLine(pText);
                        if (sb.Length > 250000) break;
                    }
                    extractedText = sb.ToString();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PdfPig không đọc được chữ số hóa, chuyển sang trích xuất thô.");
                }

                // Phát hiện PDF dạng ảnh scan / OCR không có text số hóa, mật độ chữ quá thưa (< 150 ký tự/trang) hoặc người dùng yêu cầu VISION_AI
                bool isSparsePdf = actualTextChars < 300 || (totalPagesProcessed > 0 && (actualTextChars / Math.Max(1, totalPagesProcessed)) < 150);
                if ((isSparsePdf || ocrMode == "VISION_AI") && pdfBytes != null)
                {
                    _logger.LogInformation("Kích hoạt động cơ trích xuất Multimodal Vision AI (Chế độ: {Mode}, Ký tự lớp text: {Chars} trên {Pages} trang)...", ocrMode ?? "Auto", actualTextChars, totalPagesProcessed);
                    pageImagesBase64 = ExtractDownscaledPageImages(pdfBytes, targetStart, targetEnd, maxImages: maxVisionImages);
                    _logger.LogInformation("Đã trích xuất và tối ưu hóa {Count} trang ảnh scan chất lượng cao cho Multimodal Vision.", pageImagesBase64.Count);
                    if (isSparsePdf || ocrMode == "VISION_AI")
                    {
                        extractedText = $"[Tài liệu PDF (Chế độ {ocrMode}) gồm {totalPagesProcessed} trang. Dữ liệu các trang đã được trích xuất tối ưu hóa hình ảnh và truyền trực tiếp qua kênh Multimodal Vision AI.]\n" + (ocrMode == "VISION_AI" ? string.Empty : extractedText);
                    }
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

        if (string.IsNullOrWhiteSpace(extractedText) && pageImagesBase64.Count == 0)
        {
            throw new InvalidOperationException("Không trích xuất được nội dung văn bản hoặc hình ảnh từ tài liệu được tải lên.");
        }

        string textSnippet = extractedText.Length > 200000 ? extractedText[..200000] : extractedText;

        bool isExamPatterns = string.Equals(extractionDepth, "EXAM_PATTERNS", StringComparison.OrdinalIgnoreCase) || 
                              string.Equals(extractionDepth, "deep", StringComparison.OrdinalIgnoreCase);

        string depthTemplateName = isExamPatterns
            ? "CurriculumDepthExamPatterns.prompt.txt"
            : "CurriculumDepthTheoryExtended.prompt.txt";

        string depthDirective = await _promptTemplateService.RenderPromptAsync(
            depthTemplateName,
            new Dictionary<string, string> { ["domainName"] = domainName },
            cancellationToken);

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
        int minPatternsThreshold = _configuration.GetValue<int>("AiSettings:MinPatternsThreshold", 3);
        if (minPatternsThreshold <= 0) minPatternsThreshold = 3;
        bool expectMultiPatterns = extractedText.Length > 400 || pageImagesBase64.Count > 0;
        ImportCurriculumRequestDto? bestPartialParsed = null;
        int bestPartialPatternCount = 0;
        int requestTimeoutSeconds = _configuration.GetValue<int>("AiSettings:CurriculumRequestTimeoutSeconds", 120);
        if (requestTimeoutSeconds < 30) requestTimeoutSeconds = 120;

        if (apiKeys.Count > 0)
        {
            foreach (var key in apiKeys)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Tiến trình bóc tách đã bị hủy từ phía Client/Gateway.");
                    break;
                }

                bool keyInvalidOrQuotaExceeded = false;
                string maskedKey = MaskKey(key);

                foreach (var modelName in geminiModels)
                {
                    if (cancellationToken.IsCancellationRequested || keyInvalidOrQuotaExceeded) break;

                    try
                    {
                        string url = $"{geminiBaseUrl.TrimEnd('/')}/models/{modelName}:generateContent?key={key}";
                        var partsArray = new JsonArray
                        {
                            new JsonObject { ["text"] = prompt }
                        };

                        if (pageImagesBase64.Count > 0)
                        {
                            foreach (var b64 in pageImagesBase64)
                            {
                                partsArray.Add(new JsonObject
                                {
                                    ["inlineData"] = new JsonObject
                                    {
                                        ["mimeType"] = "image/jpeg",
                                        ["data"] = b64
                                    }
                                });
                            }
                        }

                        var genConfig = new JsonObject
                        {
                            ["temperature"] = 0.1,
                            ["maxOutputTokens"] = 32768,
                            ["responseMimeType"] = "application/json"
                        };

                        var payload = new JsonObject
                        {
                            ["contents"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["parts"] = partsArray
                                }
                            },
                            ["generationConfig"] = genConfig
                        };

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                        };

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                        var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                        var statusCode = (int)response.StatusCode;

                        if (response.IsSuccessStatusCode)
                        {
                            string respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                            var docNode = JsonNode.Parse(respJson);
                            string? contentText = docNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();

                            if (!string.IsNullOrEmpty(contentText))
                            {
                                contentText = SanitizeAndRepairGeminiJson(contentText);
                                if (contentText.TrimStart().StartsWith("["))
                                {
                                    contentText = $"{{\"domainId\": \"{domainId}\", \"domainName\": \"{domainName}\", \"chapters\": {contentText}}}";
                                }

                                ImportCurriculumRequestDto? parsed = null;

                                try
                                {
                                    parsed = JsonSerializer.Deserialize<ImportCurriculumRequestDto>(contentText, new JsonSerializerOptions 
                                    { 
                                        PropertyNameCaseInsensitive = true,
                                        AllowTrailingCommas = true,
                                        ReadCommentHandling = JsonCommentHandling.Skip
                                    });
                                }
                                catch (Exception jsonEx)
                                {
                                    _logger.LogWarning("Strict JSON deserialize gặp lỗi ({Msg}), chuyển sang bộ phân tích JsonNode mềm dẻo...", jsonEx.Message);
                                    parsed = TryParseCurriculumFromLooseJson(contentText, domainId, domainName, fileName, extractedText);
                                }

                                if (IsCurriculumIncomplete(parsed))
                                {
                                    _logger.LogWarning("Strict JSON trích xuất thiếu bài học hoặc dạng bài ({Lessons} bài, {Patterns} dạng), kích hoạt JsonNode mềm dẻo...",
                                        parsed?.Chapters.Sum(c => c.Lessons?.Count ?? 0) ?? 0,
                                        parsed?.Chapters.Sum(c => c.Lessons?.Sum(l => l.Patterns?.Count ?? 0) ?? 0) ?? 0);
                                    parsed = TryParseCurriculumFromLooseJson(contentText, domainId, domainName, fileName, extractedText);
                                }

                                if (IsCurriculumIncomplete(parsed))
                                {
                                    _logger.LogWarning("JsonNode phân tích không trọn vẹn, kích hoạt bộ bóc tách Regex Real-Data Engine...");
                                    parsed = ExtractCurriculumViaRegex(contentText, domainId, domainName, fileName, extractedText);
                                }

                                if (parsed != null && parsed.Chapters.Count > 0)
                                {
                                    FinalizeAndCleanParsedCurriculum(parsed, domainId, domainName, fileName, extractedText);
                                    int extractedLessons = parsed.Chapters.Sum(c => c.Lessons?.Count ?? 0);
                                    int extractedPatterns = parsed.Chapters.Sum(c => c.Lessons?.Sum(l => l.Patterns?.Count ?? 0) ?? 0);

                                    if (extractedPatterns > 0)
                                    {
                                        parsed.ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
                                        parsed.IsRealExtraction = true;
                                        parsed.SourceFileName = fileName ?? "Direct_Text_Input";
                                        parsed.TotalPagesProcessed = totalPagesProcessed > 0 ? totalPagesProcessed : 1;
                                        parsed.TotalCharactersExtracted = extractedText.Length;

                                        if (!expectMultiPatterns || extractedPatterns >= minPatternsThreshold)
                                        {
                                            stopwatch.Stop();
                                            _logger.LogInformation("C# AI Engine trích xuất thành công {Count} chương ({Lessons} bài học, {Patterns} dạng bài) DỮ LIỆU THẬT trong {Elapsed:F2}s ({Ms} ms) từ tệp '{FileName}' bằng mô hình '{Model}'.", 
                                                parsed.Chapters.Count,
                                                extractedLessons,
                                                extractedPatterns,
                                                parsed.ProcessingTimeSeconds, stopwatch.ElapsedMilliseconds, parsed.SourceFileName, modelName);
                                            return parsed;
                                        }

                                        if (extractedPatterns > bestPartialPatternCount)
                                        {
                                            bestPartialParsed = parsed;
                                            bestPartialPatternCount = extractedPatterns;
                                        }

                                        _logger.LogWarning("Mô hình '{Model}' chỉ trích xuất được {Patterns} dạng bài (< {MinPatterns} dạng bài tối thiểu). Đã lưu tạm kết quả và chuyển sang mô hình tiếp theo để bóc tách đầy đủ các dạng bài...",
                                            modelName, extractedPatterns, minPatternsThreshold);
                                        continue;
                                    }
                                }
                            }
                        }
                        else
                        {
                            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            if (errBody.Length > 200) errBody = errBody.Substring(0, 200) + "...";

                            if (statusCode == 401 || statusCode == 403 || statusCode == 429)
                            {
                                _logger.LogWarning("Gemini API Key '{MaskedKey}' gặp lỗi Key/Quota HTTP {StatusCode} ({ErrorBody}). Chuyển sang API Key tiếp theo.", 
                                    maskedKey, statusCode, errBody);
                                keyInvalidOrQuotaExceeded = true;
                                break;
                            }
                            else if (statusCode == 503 || statusCode == 500 || statusCode == 504 || statusCode == 404 || statusCode == 400)
                            {
                                _logger.LogWarning("Gemini API model '{Model}' gặp lỗi Model/Server HTTP {StatusCode} ({ErrorBody}). Chuyển ngay sang MODEL tiếp theo với cùng API key.", 
                                    modelName, statusCode, errBody);
                                continue;
                            }
                            else
                            {
                                _logger.LogWarning("Gemini API model '{Model}' trả về HTTP {StatusCode} ({ErrorBody}). Thử model tiếp theo.", 
                                    modelName, statusCode, errBody);
                                continue;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Gemini API model '{Model}' với key '{MaskedKey}' bị timeout sau {Timeout}s. Chuyển ngay sang MODEL tiếp theo.",
                            modelName, maskedKey, requestTimeoutSeconds);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogWarning("Tiến trình bóc tách đã bị hủy từ phía Client/Gateway ({Message}). Dừng toàn bộ các model dự phòng.", ex.Message);
                            break;
                        }
                        _logger.LogWarning("Gọi Gemini API model '{Model}' thất bại ({Message}), tiếp tục kiểm tra model tiếp theo.", modelName, ex.Message);
                        continue;
                    }
                }
            }
        }

        if (bestPartialParsed != null)
        {
            stopwatch.Stop();
            bestPartialParsed.ProcessingTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
            _logger.LogInformation("Trả về kết quả bóc tách tốt nhất hiện có ({Patterns} dạng bài) sau khi duyệt các mô hình Gemini.", bestPartialPatternCount);
            return bestPartialParsed;
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
                    string? gptResult = await TryCallOpenAiCurriculumAsync(gptModel, openAiKey, prompt, cancellationToken, pageImagesBase64);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        string contentText = SanitizeAndRepairGeminiJson(gptResult);
                        if (contentText.TrimStart().StartsWith("["))
                        {
                            contentText = $"{{\"domainId\": \"{domainId}\", \"domainName\": \"{domainName}\", \"chapters\": {contentText}}}";
                        }

                        ImportCurriculumRequestDto? parsed = null;

                        try
                        {
                            parsed = JsonSerializer.Deserialize<ImportCurriculumRequestDto>(contentText, new JsonSerializerOptions 
                            { 
                                PropertyNameCaseInsensitive = true,
                                AllowTrailingCommas = true,
                                ReadCommentHandling = JsonCommentHandling.Skip
                            });
                        }
                        catch (Exception jsonEx)
                        {
                            _logger.LogWarning("OpenAI '{Model}' Strict JSON deserialize gặp lỗi ({Msg}), chuyển sang bộ phân tích JsonNode mềm dẻo...", gptModel, jsonEx.Message);
                            parsed = TryParseCurriculumFromLooseJson(contentText, domainId, domainName, fileName, extractedText);
                        }

                        if (IsCurriculumIncomplete(parsed))
                        {
                            _logger.LogWarning("OpenAI '{Model}' JsonNode phân tích thiếu bài học/dạng bài, kích hoạt bộ bóc tách Regex Real-Data Engine...", gptModel);
                            parsed = ExtractCurriculumViaRegex(contentText, domainId, domainName, fileName, extractedText);
                        }

                        if (parsed != null && parsed.Chapters.Count >= minChaptersThreshold)
                        {
                            FinalizeAndCleanParsedCurriculum(parsed, domainId, domainName, fileName, extractedText);
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
            : "Tất cả các mô hình AI trực tuyến (Gemini / OpenAI) đều gặp sự cố quá tải (503/429/Timeout) hoặc không trả về dữ liệu hợp lệ.";
        throw new InvalidOperationException($"Không thể bóc tách tài liệu '{fileName ?? "Direct_Text_Input"}' từ AI: {errDetail}");
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
            foreach (var key in apiKeys)
            {
                if (cancellationToken.IsCancellationRequested) break;
                bool keyInvalidOrQuotaExceeded = false;
                string maskedKey = MaskKey(key);

                foreach (var modelName in geminiModels)
                {
                    if (cancellationToken.IsCancellationRequested || keyInvalidOrQuotaExceeded) break;

                    try
                    {
                        string url = $"{geminiBaseUrl.TrimEnd('/')}/models/{modelName}:generateContent?key={key}";
                        var patternGenConfig = new JsonObject
                        {
                            ["temperature"] = 0.1,
                            ["maxOutputTokens"] = 8192,
                            ["responseMimeType"] = "application/json"
                        };

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
                            ["generationConfig"] = patternGenConfig
                        };

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                        };

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                        var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                        var statusCode = (int)response.StatusCode;

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
                                        AllowTrailingCommas = true,
                                        ReadCommentHandling = JsonCommentHandling.Skip
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
                            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            if (errBody.Length > 200) errBody = errBody.Substring(0, 200) + "...";

                            if (statusCode == 401 || statusCode == 403 || statusCode == 429)
                            {
                                _logger.LogWarning("Gemini API Key '{MaskedKey}' gặp lỗi Key/Quota HTTP {StatusCode} ({ErrorBody}). Chuyển sang API Key tiếp theo.",
                                    maskedKey, statusCode, errBody);
                                keyInvalidOrQuotaExceeded = true;
                                break;
                            }
                            else if (statusCode == 503 || statusCode == 500 || statusCode == 504 || statusCode == 404 || statusCode == 400)
                            {
                                _logger.LogWarning("Gemini API model '{Model}' gặp lỗi tải máy chủ/không khả dụng HTTP {StatusCode} ({ErrorBody}). Chuyển ngay sang MODEL tiếp theo với cùng API key.",
                                    modelName, statusCode, errBody);
                                continue;
                            }
                            else
                            {
                                _logger.LogWarning("Gemini API pattern analysis model '{Model}' trả về HTTP {StatusCode} ({ErrorBody}). Thử model tiếp theo.",
                                    modelName, statusCode, errBody);
                                continue;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Gemini API pattern analysis model '{Model}' với key '{MaskedKey}' bị timeout sau 35s. Chuyển sang MODEL tiếp theo.",
                            modelName, maskedKey);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        _logger.LogWarning("Gemini AI pattern analysis model '{Model}' error: {Message}. Thử model tiếp theo.", modelName, ex.Message);
                        continue;
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
                                AllowTrailingCommas = true,
                                ReadCommentHandling = JsonCommentHandling.Skip
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
            var antiDupVars = new Dictionary<string, string>
            {
                ["existingCount"] = request.ExistingQuestions.Count.ToString(),
                ["samples"] = samples
            };
            antiDuplicationNote = "\n" + await _promptTemplateService.RenderPromptAsync("PracticeQuestionAntiDuplication.prompt.txt", antiDupVars, cancellationToken);
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
            foreach (var key in apiKeys)
            {
                if (cancellationToken.IsCancellationRequested) break;
                bool keyInvalidOrQuotaExceeded = false;
                string maskedKey = MaskKey(key);

                foreach (var modelName in geminiModels)
                {
                    if (cancellationToken.IsCancellationRequested || keyInvalidOrQuotaExceeded) break;

                    try
                    {
                        string url = $"{geminiBaseUrl.TrimEnd('/')}/models/{modelName}:generateContent?key={key}";
                        var qGenConfig = new JsonObject
                        {
                            ["temperature"] = 0.7,
                            ["maxOutputTokens"] = 8192,
                            ["responseMimeType"] = "application/json"
                        };

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
                            ["generationConfig"] = qGenConfig
                        };

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                        };

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                        var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                        var statusCode = (int)response.StatusCode;

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
                                    parsedNode = JsonNode.Parse(contentText, new JsonNodeOptions(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
                            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            if (errBody.Length > 200) errBody = errBody.Substring(0, 200) + "...";

                            if (statusCode == 401 || statusCode == 403 || statusCode == 429)
                            {
                                _logger.LogWarning("Gemini API Key '{MaskedKey}' gặp lỗi Key/Quota HTTP {StatusCode} ({ErrorBody}). Chuyển sang API Key tiếp theo.",
                                    maskedKey, statusCode, errBody);
                                keyInvalidOrQuotaExceeded = true;
                                break;
                            }
                            else if (statusCode == 503 || statusCode == 500 || statusCode == 504 || statusCode == 404 || statusCode == 400)
                            {
                                _logger.LogWarning("Gemini API model '{Model}' gặp lỗi tải máy chủ/không khả dụng HTTP {StatusCode} ({ErrorBody}). Chuyển ngay sang MODEL tiếp theo với cùng API key.",
                                    modelName, statusCode, errBody);
                                continue;
                            }
                            else
                            {
                                _logger.LogWarning("Gemini API generate questions model '{Model}' trả về HTTP {StatusCode} ({ErrorBody}). Thử model tiếp theo.",
                                    modelName, statusCode, errBody);
                                continue;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Gemini API generate questions model '{Model}' với key '{MaskedKey}' bị timeout sau 35s. Chuyển sang MODEL tiếp theo.",
                            modelName, maskedKey);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        _logger.LogWarning("Gemini AI generate pattern questions model '{Model}' error: {Message}. Thử model tiếp theo.", modelName, ex.Message);
                        continue;
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
                            var docNode = JsonNode.Parse(contentText, new JsonNodeOptions(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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

    public static string SanitizeAndRepairGeminiJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "{}";

        string cleaned = rawJson.Trim();

        // -0.0 Bóc tách khối JSON từ code block markdown ```json ... ``` hoặc giữa { / [ và } / ]
        var matchFence = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (matchFence.Success)
        {
            cleaned = matchFence.Groups[1].Value.Trim();
        }
        else
        {
            int firstBrace = cleaned.IndexOf('{');
            int firstBracket = cleaned.IndexOf('[');
            int startIdx = -1;
            if (firstBrace >= 0 && firstBracket >= 0) startIdx = Math.Min(firstBrace, firstBracket);
            else if (firstBrace >= 0) startIdx = firstBrace;
            else if (firstBracket >= 0) startIdx = firstBracket;

            if (startIdx >= 0)
            {
                int lastBrace = cleaned.LastIndexOf('}');
                int lastBracket = cleaned.LastIndexOf(']');
                int endIdx = Math.Max(lastBrace, lastBracket);
                if (endIdx > startIdx)
                {
                    cleaned = cleaned.Substring(startIdx, endIdx - startIdx + 1).Trim();
                }
                else
                {
                    cleaned = cleaned.Substring(startIdx).Trim();
                }
            }
        }

        // -0.1 Xóa sạch chú thích kiểu C-style (/* ... */) và C++ line comments (// ...) nếu AI sinh vào JSON
        cleaned = Regex.Replace(cleaned, @"/\*[\s\S]*?\*/", "");
        cleaned = Regex.Replace(cleaned, @"(?m)^\s*//.*$", "");
        cleaned = Regex.Replace(cleaned, @"(?m)(?<=[,\{\}\[\]])\s*//.*$", "");

        // -0.2 Xóa trailing commas trước dấu đóng ngoặc
        cleaned = Regex.Replace(cleaned, @",\s*([\}\]])", "$1");

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
        char lastNonWsOutside = '\0';
        bool isPropertyKey = false;

        for (int i = 0; i < cleaned.Length; i++)
        {
            char c = cleaned[i];

            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                    isPropertyKey = (lastNonWsOutside == '{' || lastNonWsOutside == ',');
                }
                else if (!char.IsWhiteSpace(c))
                {
                    lastNonWsOutside = c;
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

                bool isClosingQuote = false;

                if (isPropertyKey)
                {
                    // Tên thuộc tính BẮT BUỘC kết thúc bằng dấu hai chấm ':'
                    if (nextChar == ':')
                    {
                        isClosingQuote = true;
                    }
                }
                else
                {
                    // Giá trị thuộc tính chuỗi
                    if (nextChar == '}' || nextChar == ']' || nextChar == '\0')
                    {
                        isClosingQuote = true;
                    }
                    else if (nextChar == ',')
                    {
                        // Kiểm tra xem sau dấu phẩy có phải là phần tử hoặc thuộc tính tiếp theo không
                        int afterCommaIdx = nextIdx + 1;
                        while (afterCommaIdx < cleaned.Length && char.IsWhiteSpace(cleaned[afterCommaIdx]))
                        {
                            afterCommaIdx++;
                        }

                        if (afterCommaIdx < cleaned.Length)
                        {
                            char ac = cleaned[afterCommaIdx];
                            if (ac == '}' || ac == ']' || ac == '{' || ac == '[')
                            {
                                isClosingQuote = true;
                            }
                            else if (ac == '"')
                            {
                                string remainder = cleaned.Substring(afterCommaIdx);
                                if (Regex.IsMatch(remainder, @"^""[a-zA-Z0-9_]{1,50}""\s*:"))
                                {
                                    isClosingQuote = true;
                                }
                            }
                        }
                        else
                        {
                            isClosingQuote = true;
                        }
                    }
                    else if (nextChar == '"')
                    {
                        // Trường hợp AI quên dấu phẩy giữa 2 thuộc tính: "prop1": "val1" \n "prop2": "val2"
                        string sub = cleaned.Substring(nextIdx);
                        if (Regex.IsMatch(sub, @"^""[a-zA-Z0-9_]{1,50}""\s*:"))
                        {
                            inString = false;
                            sb.Append("\",");
                            i = nextIdx - 1;
                            continue;
                        }
                    }
                }

                if (isClosingQuote)
                {
                    inString = false;
                    sb.Append(c);
                }
                else
                {
                    // Dấu ngoặc kép không escape ở giữa nội dung tiếng Việt / danh pháp hóa học (ví dụ: "Aspirin", "Xà phòng hóa", "Đoạn thẳng"...)
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

    internal static bool IsCurriculumIncomplete(ImportCurriculumRequestDto? dto)
    {
        if (dto == null || dto.Chapters == null || dto.Chapters.Count == 0) return true;
        int totalLessons = dto.Chapters.Sum(c => c.Lessons?.Count ?? 0);
        if (totalLessons == 0) return true;
        int totalPatterns = dto.Chapters.Sum(c => c.Lessons?.Sum(l => l.Patterns?.Count ?? 0) ?? 0);
        if (totalPatterns == 0) return true;
        return false;
    }

    internal static ImportCurriculumRequestDto? TryParseCurriculumFromLooseJson(string rawJson, string domainId, string domainName, string? fileName = null, string? extractedText = null)
    {
        try
        {
            var node = JsonNode.Parse(rawJson, new JsonNodeOptions(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node == null) return null;

            var result = new ImportCurriculumRequestDto
            {
                DomainId = GetNodeString(node, "domainId", "domain_id") ?? domainId,
                DomainName = GetNodeString(node, "domainName", "domain_name") ?? domainName
            };

            JsonArray? chaptersNode = null;

            if (node is JsonArray rootArr)
            {
                chaptersNode = rootArr;
            }
            else if (node is JsonObject rootObj)
            {
                chaptersNode = GetNodeArray(rootObj, "chapters", "Chapters", "chapterList", "chapter_list", "data", "curriculum");
                if (chaptersNode == null)
                {
                    // Nếu rootObj không có mảng chapters nhưng chính nó là một chapter
                    string? chName = GetNodeString(rootObj, "chapterName", "chapter_name", "name", "title", "chapter", "chuong");
                    var lessons = GetNodeArray(rootObj, "lessons", "Lessons", "lessonList", "sections", "topics", "items");
                    var directPats = GetNodeArray(rootObj, "patterns", "Patterns", "patternList", "dangBai");
                    if (!string.IsNullOrWhiteSpace(chName) || lessons != null || directPats != null)
                    {
                        chaptersNode = new JsonArray { rootObj.DeepClone() };
                    }
                }
            }

            if (chaptersNode == null) return null;

            string fallbackChapName = "Chương 1: Trọng tâm chuyên đề";
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                fallbackChapName = Path.GetFileNameWithoutExtension(fileName)
                    .Replace("(BẢN HS)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(BẢN GV)", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }

            foreach (var chNode in chaptersNode)
            {
                if (chNode == null) continue;
                string? parsedChName = GetNodeString(chNode, "chapterName", "chapter_name", "name", "title", "chapter", "chuong");
                if (string.IsNullOrWhiteSpace(parsedChName)) parsedChName = fallbackChapName;

                var ch = new ImportChapterDto
                {
                    ChapterName = parsedChName,
                    Weight = GetNodeDouble(chNode, 0.25, "weight", "Weight"),
                    Description = GetNodeString(chNode, "description", "summary")
                };

                var lessonsNode = GetNodeArray(chNode, "lessons", "Lessons", "lessonList", "lesson_list", "sections", "Sections", "topics", "Topics", "items", "children", "baiHoc", "danhSachBaiHoc", "chuyenDe");
                if (lessonsNode != null && lessonsNode.Count > 0)
                {
                    foreach (var lesNode in lessonsNode)
                    {
                        if (lesNode == null) continue;
                        var les = new ImportLessonDto
                        {
                            LessonName = GetNodeString(lesNode, "lessonName", "lesson_name", "name", "title", "section", "topic", "baiHoc", "bai") ?? "Bài học",
                            Weight = GetNodeDouble(lesNode, 0.1, "weight", "Weight"),
                            Summary = GetNodeString(lesNode, "summary", "description")
                        };

                        var patternsNode = GetNodeArray(lesNode, "patterns", "Patterns", "patternList", "pattern_list", "dangBai", "danhSachDangBai", "items", "subTopics");
                        if (patternsNode != null)
                        {
                            foreach (var patNode in patternsNode)
                            {
                                if (patNode == null) continue;
                                var pat = new ImportPatternDto
                                {
                                    PatternName = GetNodeString(patNode, "patternName", "pattern_name", "name", "title", "dangBai", "dang") ?? "Dạng bài",
                                    Weight = GetNodeDouble(patNode, 0.05, "weight", "Weight"),
                                    Classification = GetNodeString(patNode, "classification"),
                                    CoreSkill = GetNodeString(patNode, "coreSkill", "core_skill"),
                                    TheoryRecap = GetNodeString(patNode, "theoryRecap", "theory_recap", "theory") ?? string.Empty,
                                    SolutionMethod = GetNodeString(patNode, "solutionMethod", "solution_method", "method") ?? string.Empty,
                                    VideoUrl = GetNodeString(patNode, "videoUrl", "video_url"),
                                    DurationSeconds = GetNodeInt(patNode, 600, "durationSeconds", "duration_seconds")
                                };

                                var questionsNode = GetNodeArray(patNode, "questions", "Questions", "questionList", "question_list", "cauHoi", "danhSachCauHoi", "exercises", "items");
                                if (questionsNode != null)
                                {
                                    foreach (var qNode in questionsNode)
                                    {
                                        if (qNode == null) continue;
                                        pat.Questions.Add(new ImportQuestionDto
                                        {
                                            DifficultyLevel = GetNodeInt(qNode, 2, "difficultyLevel", "difficulty_level", "level"),
                                            ContentLatex = GetNodeString(qNode, "contentLatex", "content_latex", "content", "question", "prompt", "cauHoi") ?? string.Empty,
                                            OptionA = GetNodeString(qNode, "optionA", "option_a", "a", "A") ?? string.Empty,
                                            OptionB = GetNodeString(qNode, "optionB", "option_b", "b", "B") ?? string.Empty,
                                            OptionC = GetNodeString(qNode, "optionC", "option_c", "c", "C") ?? string.Empty,
                                            OptionD = GetNodeString(qNode, "optionD", "option_d", "d", "D") ?? string.Empty,
                                            CorrectOption = NormalizeCorrectOption(GetNodeString(qNode, "correctOption", "correct_option", "answer", "dapAn", "dap_an")),
                                            Explanation = GetNodeString(qNode, "explanation", "solution", "giaiChiTiet", "giai_chi_tiet")
                                        });
                                    }
                                }

                                les.Patterns.Add(pat);
                            }
                        }

                        ch.Lessons.Add(les);
                    }
                }
                else
                {
                    // Xử lý khi AI trả về patterns trực tiếp trên cấp chapter
                    var directPatternsNode = GetNodeArray(chNode, "patterns", "Patterns", "patternList", "pattern_list", "dangBai", "danhSachDangBai");
                    if (directPatternsNode != null && directPatternsNode.Count > 0)
                    {
                        var syntheticLesson = new ImportLessonDto
                        {
                            LessonName = !string.IsNullOrWhiteSpace(ch.ChapterName) ? ch.ChapterName : "Bài 1: Trọng tâm chuyên đề",
                            Weight = 0.2
                        };

                        foreach (var patNode in directPatternsNode)
                        {
                            if (patNode == null) continue;
                            var pat = new ImportPatternDto
                            {
                                PatternName = GetNodeString(patNode, "patternName", "pattern_name", "name", "title", "dangBai", "dang") ?? "Dạng bài",
                                Weight = GetNodeDouble(patNode, 0.05, "weight", "Weight"),
                                Classification = GetNodeString(patNode, "classification"),
                                CoreSkill = GetNodeString(patNode, "coreSkill", "core_skill"),
                                TheoryRecap = GetNodeString(patNode, "theoryRecap", "theory_recap", "theory") ?? string.Empty,
                                SolutionMethod = GetNodeString(patNode, "solutionMethod", "solution_method", "method") ?? string.Empty,
                                VideoUrl = GetNodeString(patNode, "videoUrl", "video_url"),
                                DurationSeconds = GetNodeInt(patNode, 600, "durationSeconds", "duration_seconds")
                            };

                            var questionsNode = GetNodeArray(patNode, "questions", "Questions", "questionList", "question_list", "cauHoi", "danhSachCauHoi", "exercises", "items");
                            if (questionsNode != null)
                            {
                                foreach (var qNode in questionsNode)
                                {
                                    if (qNode == null) continue;
                                    pat.Questions.Add(new ImportQuestionDto
                                    {
                                        DifficultyLevel = GetNodeInt(qNode, 2, "difficultyLevel", "difficulty_level", "level"),
                                        ContentLatex = GetNodeString(qNode, "contentLatex", "content_latex", "content", "question", "prompt", "cauHoi") ?? string.Empty,
                                        OptionA = GetNodeString(qNode, "optionA", "option_a", "a", "A") ?? string.Empty,
                                        OptionB = GetNodeString(qNode, "optionB", "option_b", "b", "B") ?? string.Empty,
                                        OptionC = GetNodeString(qNode, "optionC", "option_c", "c", "C") ?? string.Empty,
                                        OptionD = GetNodeString(qNode, "optionD", "option_d", "d", "D") ?? string.Empty,
                                        CorrectOption = NormalizeCorrectOption(GetNodeString(qNode, "correctOption", "correct_option", "answer", "dapAn")),
                                        Explanation = GetNodeString(qNode, "explanation", "solution")
                                    });
                                }
                            }

                            syntheticLesson.Patterns.Add(pat);
                        }

                        ch.Lessons.Add(syntheticLesson);
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

    private static string? GetNodeString(JsonNode? node, params string[] propertyNames)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in propertyNames)
            {
                foreach (var kvp in obj)
                {
                    if (kvp.Key.Equals(prop, StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
                    {
                        try { return kvp.Value.GetValue<string>(); }
                        catch { return kvp.Value.ToString(); }
                    }
                }
            }
        }
        return null;
    }

    private static double GetNodeDouble(JsonNode? node, double fallback, params string[] propertyNames)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in propertyNames)
            {
                foreach (var kvp in obj)
                {
                    if (kvp.Key.Equals(prop, StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
                    {
                        try { return kvp.Value.GetValue<double>(); }
                        catch 
                        { 
                            if (double.TryParse(kvp.Value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                                return d;
                        }
                    }
                }
            }
        }
        return fallback;
    }

    private static int GetNodeInt(JsonNode? node, int fallback, params string[] propertyNames)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in propertyNames)
            {
                foreach (var kvp in obj)
                {
                    if (kvp.Key.Equals(prop, StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
                    {
                        try { return kvp.Value.GetValue<int>(); }
                        catch 
                        { 
                            if (int.TryParse(kvp.Value.ToString(), out var i))
                                return i;
                        }
                    }
                }
            }
        }
        return fallback;
    }

    private static JsonArray? GetNodeArray(JsonNode? node, params string[] propertyNames)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in propertyNames)
            {
                foreach (var kvp in obj)
                {
                    if (kvp.Key.Equals(prop, StringComparison.OrdinalIgnoreCase) && kvp.Value is JsonArray arr)
                    {
                        return arr;
                    }
                }
            }
        }
        else if (node is JsonArray arr)
        {
            return arr;
        }
        return null;
    }

    private static void FinalizeAndCleanParsedCurriculum(ImportCurriculumRequestDto parsed, string domainId, string domainName, string? fileName = null, string? extractedText = null)
    {
        if (!string.IsNullOrWhiteSpace(domainId))
        {
            parsed.DomainId = domainId;
        }
        if (!string.IsNullOrWhiteSpace(domainName))
        {
            parsed.DomainName = domainName;
        }

        string fallbackChapName = "Chương 1: Trọng tâm chuyên đề";
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            fallbackChapName = Path.GetFileNameWithoutExtension(fileName)
                .Replace("(BẢN HS)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("(BẢN GV)", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        if (parsed.Chapters == null || parsed.Chapters.Count == 0)
        {
            return;
        }

        foreach (var chap in parsed.Chapters)
        {
            if (string.IsNullOrWhiteSpace(chap.ChapterName))
            {
                chap.ChapterName = fallbackChapName;
            }

            if (chap.Lessons == null) chap.Lessons = new List<ImportLessonDto>();

            foreach (var les in chap.Lessons)
            {
                if (les.Patterns == null || les.Patterns.Count == 0) continue;

                // Deduplicate patterns in the same lesson (loại bỏ dạng bài trùng lặp)
                var uniquePatterns = new List<ImportPatternDto>();
                foreach (var pat in les.Patterns)
                {
                    var normName = Regex.Replace(pat.PatternName ?? string.Empty, @"^Dạng\s*\d+\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
                    var existing = uniquePatterns.FirstOrDefault(u =>
                    {
                        var uNorm = Regex.Replace(u.PatternName ?? string.Empty, @"^Dạng\s*\d+\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
                        return string.Equals(uNorm, normName, StringComparison.OrdinalIgnoreCase);
                    });

                    if (existing == null)
                    {
                        uniquePatterns.Add(pat);
                    }
                    else
                    {
                        // Merge câu hỏi từ dạng bài trùng lặp nếu chưa có
                        if (pat.Questions != null && pat.Questions.Count > 0)
                        {
                            if (existing.Questions == null) existing.Questions = new List<ImportQuestionDto>();
                            foreach (var q in pat.Questions)
                            {
                                if (!existing.Questions.Any(eq => eq.ContentLatex == q.ContentLatex))
                                {
                                    existing.Questions.Add(q);
                                }
                            }
                        }
                    }
                }

                // Đánh số thứ tự chuẩn hóa liên tục Dạng 1 -> Dạng N
                for (int pi = 0; pi < uniquePatterns.Count; pi++)
                {
                    var p = uniquePatterns[pi];
                    string rawName = Regex.Replace(p.PatternName ?? string.Empty, @"^Dạng\s*\d+\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
                    if (string.IsNullOrWhiteSpace(rawName)) rawName = $"Trọng tâm kiến thức {les.LessonName} phần {pi + 1}";
                    p.PatternName = $"Dạng {pi + 1}: {rawName}";
                }
                les.Patterns = uniquePatterns;

                foreach (var pat in les.Patterns)
                {
                    pat.PatternName = CleanSingleQuoteSurrounding(pat.PatternName);
                    pat.Classification = CleanSingleQuoteSurrounding(pat.Classification);
                    pat.CoreSkill = CleanSingleQuoteSurrounding(pat.CoreSkill);
                    pat.TheoryRecap = NormalizeCurriculumLatex(pat.TheoryRecap);
                    pat.SolutionMethod = NormalizeCurriculumLatex(pat.SolutionMethod);

                    if (pat.Questions != null)
                    {
                        foreach (var q in pat.Questions)
                        {
                            q.CorrectOption = CleanSingleQuoteSurrounding(q.CorrectOption);
                            q.ContentLatex = NormalizeCurriculumLatex(q.ContentLatex);
                            q.Explanation = NormalizeCurriculumLatex(q.Explanation);
                            q.OptionA = CleanOptionString(q.OptionA);
                            q.OptionB = CleanOptionString(q.OptionB);
                            q.OptionC = CleanOptionString(q.OptionC);
                            q.OptionD = CleanOptionString(q.OptionD);
                        }
                    }
                }
            }
        }
    }

    internal static string NormalizeCurriculumLatex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text;

        // 0. Phục hồi ngay lập tức các ký tự điều khiển ASCII bị nuốt
        s = s.Replace("\x0crac", @"\frac")
             .Replace("\x09imes", @"\times")
             .Replace("\x09ext", @"\text")
             .Replace("\x08ar", @"\bar")
             .Replace("\x08eta", @"\beta")
             .Replace("\x0c", @"\f")
             .Replace("\x09", @"\t")
             .Replace("\x08", @"\b");

        // 0.1. BẢO VỆ TUYỆT ĐỐI CÁC KHỐI ĐỒ THỊ & BẢNG BIẾN THIÊN ([MATH_GRAPH_3D], [MATH_GRAPH], [VARIATION_TABLE], [MATH_GEOMETRY_2D]):
        // Tuyệt đối không chạy regex bọc $...$ hay nối dòng `: ` vào bên trong các thẻ cấu trúc đồ thị!
        s = Regex.Replace(s, @"\[\s*\$?(\/?)\s*\$?(MATH_GRAPH_3D|MATH_GRAPH|VARIATION_TABLE|MATH_GEOMETRY_2D)\$?\s*\]", "[$1$2]", RegexOptions.IgnoreCase);
        var preservedBlocks = new List<string>();
        s = Regex.Replace(
            s,
            @"(\[(?:MATH_GRAPH_3D|MATH_GRAPH|VARIATION_TABLE|MATH_GEOMETRY_2D)\][\s\S]*?\[\/(?:MATH_GRAPH_3D|MATH_GRAPH|VARIATION_TABLE|MATH_GEOMETRY_2D)\]|\[GRAPH:[\s\S]*?\])",
            m =>
            {
                string rawBlock = m.Groups[1].Value;
                // Nếu khối đồ thị đã bị dính dấu $ từ trước, làm sạch dấu $ và phục hồi tên thẻ chuẩn
                rawBlock = rawBlock.Replace("$", "");
                preservedBlocks.Add(rawBlock);
                string idxCode = new string((preservedBlocks.Count - 1).ToString().Select(ch => (char)('a' + (ch - '0'))).ToArray());
                return $"@@mathlabblock{idxCode}@@";
            },
            RegexOptions.IgnoreCase);

        // Dọn dẹp lỗi cú pháp đặt dấu $ lẫn lộn
        // Sửa lỗi dấu $ đặt sai vị trí bên trong ngoặc nhọn: {k - 1$} -> {k - 1}$
        s = Regex.Replace(s, @"\{([^{}\$]+)\$\}", "{$1}$");

        // Sửa lỗi đóng $$ ở cuối dòng không có mở $$
        s = Regex.Replace(s, @"(?m)^(?!\$\$)([^\r\n]*?\\(?:xrightarrow|rightleftharpoons|longrightarrow)[^\r\n]*?)\$\$?$", m => "$$" + m.Groups[1].Value.Trim().Trim('$') + "$$");

        // Giải phóng ngay văn bản tiếng Việt bị nuốt nhầm vào giữa cặp $...$ hoặc $$...$$
        var vietnameseWordsPattern = @"\b(?:suy\s+ra|dựa\s+vào|thu\s+được|phản\s+ứng|hoàn\s+toàn|tác\s+dụng|dung\s+dịch|chất|kết\s+tủa|khí|nhiệt\s+độ|hiệu\s+suất|khối\s+lượng|số\s+mol|gốc\s+acid|gốc\s+alcohol|đồng\s+phân|danh\s+pháp|tính\s+chất|khái\s+niệm|khi\s+thay|thì\s+được|ở\s+nhóm|của|bằng|trong|với|tối\s+đa|liên\s+kết)\b";
        s = Regex.Replace(s, @"\$\$([\s\S]*?)\$\$", m =>
        {
            var inner = m.Groups[1].Value;
            var outsideText = Regex.Replace(Regex.Replace(inner, @"\\text\{[^{}]*\}", ""), @"[_^]\{[^{}]*\}", "");
            if (Regex.IsMatch(outsideText, vietnameseWordsPattern, RegexOptions.IgnoreCase)) return inner;
            return m.Value;
        });

        s = Regex.Replace(s, @"\$([^\$\r\n]+?)\$", m =>
        {
            var inner = m.Groups[1].Value;
            var outsideText = Regex.Replace(Regex.Replace(inner, @"\\text\{[^{}]*\}", ""), @"[_^]\{[^{}]*\}", "");
            if (Regex.IsMatch(outsideText, vietnameseWordsPattern, RegexOptions.IgnoreCase) ||
                Regex.IsMatch(outsideText, @"\b[a-zA-Zàáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]{3,}\s+[a-zA-Zàáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]{2,}\b", RegexOptions.IgnoreCase))
            {
                return inner;
            }
            return m.Value;
        });

        // Chuẩn hóa từ tiếng Việt trong chỉ số dưới: m_{chất béo}, m_{chấtbéo}, n_{muối}, m_{muối}
        s = Regex.Replace(s, @"([a-zA-Z])_\{chấtbéo\}", @"$1_{\text{chất béo}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{chất\s*béo\}", @"$1_{\text{chất béo}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{muối\}", @"$1_{\text{muối}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{ester\}", @"$1_{\text{ester}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{glycerol\}", @"$1_{\text{glycerol}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{phảnứng\}", @"$1_{\text{phản ứng}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{thựctế\}", @"$1_{\text{thực tế}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{lýthuyết\}", @"$1_{\text{lý thuyết}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{líthuyết\}", @"$1_{\text{lí thuyết}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{(?!\s*\\text\{)([a-zA-Zàáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ\s()]{2,})\}", @"$1_{\text{$2}}");

        // Khắc phục khoảng trắng sai vị trí sau dấu gạch dưới chỉ số e.g. (OH)_ 3 -> (OH)_3
        s = Regex.Replace(s, @"_\s+(\d+|[a-zA-Z]|\{[^}]+\})", "_$1");

        // Chuẩn hóa khoảng trắng giữa gốc hydrocarbon và nhóm chức hữu cơ e.g. C_{15}H_{31} COOH -> C_{15}H_{31}COOH
        s = Regex.Replace(s, @"([A-Z][a-zA-Z0-9_\\\{\}\^]*_\{\d+\})\s+(COOH|OH|CHO|COONa|NH_2|COOCH_3)\b", "$1$2");

        // 1. Phục hồi công thức tính hiệu suất phản ứng nếu bị lỗi undefined
        s = Regex.Replace(s, @"(?i)H\\?%\s*undefined\s*%?\s*\$*", @"H\% = \frac{m_{\text{thực tế}}}{m_{\text{lí thuyết}}} \times 100\%");
        s = Regex.Replace(s, @"(?i)H\\?%\s*=\s*undefined\s*%?\s*\$*", @"H\% = \frac{m_{\text{thực tế}}}{m_{\text{lí thuyết}}} \times 100\%");

        // 2. Loại bỏ các token rác undefined đứng cô lập hoặc lơ lửng
        s = Regex.Replace(s, @"(?i)(?:^|\r?\n)\s*\\?undefined\s*(?=\r?\n|$)", "\n");
        s = Regex.Replace(s, @"(?i)\\?undefined", "");

        // 3. Tách rời tiêu đề markdown ### nếu bị dính liền sau câu hoặc công thức
        s = Regex.Replace(s, @"([^\r\n])\s*(###\s+[^\r\n]+)", "$1\n\n$2");

        // 4. Nối các dòng bị ngắt cụt gây gãy layout
        s = Regex.Replace(s, @"\r?\n\s*:\s*", ": ");
        s = Regex.Replace(s, @"(\*[\s]*\([A-F]\))\r?\n\s*", "$1 ");

        // Nối công thức ester bị ngắt giữa chuỗi C-H và Oxy (kể cả có khoảng trắng hoặc xuống dòng)
        s = Regex.Replace(s, @"(?i)(\$?C_\{?[0-9n\+\-]+\}?H_\{?[0-9a-zA-Z\+\-]+\}?\$?)\s*(?:\r?\n|\s+)\s*(\$?O_\{?[0-9]+\}?\$?)", m => "$" + m.Groups[1].Value.Replace("$", "") + m.Groups[2].Value.Replace("$", "") + "$");

        // Khắc phục công thức ester dính liền O_2 ngay sau $C_nH_{2n}$ hoặc khoảng trắng e.g. $C_nH_{2n}$O_2 -> $C_nH_{2n}O_2$
        s = Regex.Replace(s, @"(?i)(\$C_\{?[0-9n\+\-]+\}?H_\{?[0-9a-zA-Z\+\-]+\}?)\$\s*(O_\{?[0-9]+\}?)", "$1$2$");
        s = Regex.Replace(s, @"(?<![\$\w])(C_\{?[0-9n\+\-]+\}?H_\{?[0-9a-zA-Z\+\-]+\}?)\$\s*(O_\{?[0-9]+\}?)\$?", m => "$" + m.Groups[1].Value + m.Groups[2].Value + "$", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<![\$\w])(C_\{?[0-9n\+\-]+\}?H_\{?[0-9a-zA-Z\+\-]+\}?)\s*(O_\{?[0-9]+\}?)(?![\$\w])", m => "$" + m.Groups[1].Value + m.Groups[2].Value + "$", RegexOptions.IgnoreCase);

        // Sửa lỗi đóng mở $ bị lỗi trong {arg$} -> {arg}$
        s = Regex.Replace(s, @"\{([^{}\$]+)\$\}", "{$1}$");

        // Nối công thức hóa học bị ngắt dòng sau dấu nối hóa trị - hoặc = (kể cả có \text{})
        s = Regex.Replace(s, @"(?i)(\$?[A-Za-z0-9_\\\{\}']+[-=])\s*\r?\n\s*(\$?[A-Za-z0-9_\\\{\}']+)", m => "$" + m.Groups[1].Value.Replace("$", "") + m.Groups[2].Value.Replace("$", "") + "$");

        // Nối chuỗi este đa chức bị ngắt dòng nhiều cấp
        s = Regex.Replace(s, @"(?i)([A-Za-z0-9_\\\{\}']+-)\s*\r?\n\s*([A-Za-z0-9_\\\{\}']+-)\s*\r?\n\s*([A-Za-z0-9_\\\{\}']+)", m => "$" + m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "$");

        // Nối nhóm ngoặc chỉ số )_2, )_3 bị rớt xuống dòng riêng
        s = Regex.Replace(s, @"([A-Za-z0-9_\\\{\}']+)\s*\r?\n\s*(\)[_0-9]+)", "$1$2");

        // Sửa điều kiện bất đẳng thức có dấu $ bị đặt nhầm: (n \ge 2$) hoặc ($n$ \ge 4$) hoặc (n \ge 2)
        s = Regex.Replace(s, @"\(\s*\$?([a-zA-Z])\$?\s*\\(?:ge|le|gt|lt)\s*(\d+)\s*\$?\s*\)", m => $"(${m.Groups[1].Value} \\ge {m.Groups[2].Value})");
        s = Regex.Replace(s, @"\(\s*([a-zA-Z])\s*\\(?:ge|le|gt|lt)\s*(\d+)\s*\$\s*\)", m => $"(${m.Groups[1].Value} \\ge {m.Groups[2].Value})");

        // Sửa lỗi dấu $ đóng nhầm trước số thập phân: \times$ 0,5 -> \times 0,5$
        s = Regex.Replace(s, @"\\times\s*\$\s*(\d+[,\.]?\d*)", @"\times $1$");

        s = Regex.Replace(s, @"\\(equiv|times|rightarrow|leftarrow|rightleftharpoons)([A-Za-z0-9])", @"\$1 $2");
        s = Regex.Replace(s, @"([A-Za-z0-9_]+)\$\s*=\s*\$([A-Za-z0-9_]+)", "$1=$2");
        s = Regex.Replace(s, @"\$\s*(-[A-Za-z0-9]+-)\s*,?", "$1,");

        // Chuẩn hóa số thập phân có khoảng trắng sau dấu phẩy e.g. 9, 18 gam -> 9,18 gam
        s = Regex.Replace(s, @"(\d+),\s+(\d+)", "$1,$2");

        // Chuẩn hóa khoảng trắng lệnh LaTeX và dấu ngoặc nhọn: \text {mol} -> \text{mol}, \frac {a} {b} -> \frac{a}{b}
        s = Regex.Replace(s, @"\\(text|mathrm|mathbf|mathit|mathsf|mathtt|mathbb|frac|sqrt)\s+\{", @"\$1{");
        s = Regex.Replace(s, @"(\\frac\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\})\s+(\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\})", "$1$2");
        s = Regex.Replace(s, @"\{(\s*[0-9a-zA-Z\+\-\*\/]+\s*)\}", m => "{" + m.Groups[1].Value.Trim() + "}");
        // 1. Dọn dẹp triệt để các mã lệnh \\text{gam}, \\text{mol}, text{gam} bị lọt vào văn bản tiếng Việt thường:
        // Đun nóng 12,0\\text{gam} -> Đun nóng 12,0 gam
        // 0,35\\text{mol} -> 0,35 mol
        s = Regex.Replace(s, @"(?<![A-Za-z_])([0-9]+(?:[,\.][0-9]+)?)\s*\\*text\s*\{\s*(mol|gam|g|kg|lít|lit|ml)\s*\}", "$1 $2", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=[0-9,\.]|\w|\})\s*text\s*(mol|gam|g|kg|lít|lit|ml)\b", @" \text{$1}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\btext(mol|gam|g|kg)\b", @"\text{$1}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\\text\s*(mol|gam|g|kg|lít|lit|ml)\b", @"\text{$1}", RegexOptions.IgnoreCase);

        // Chuẩn hóa biến đơn lẻ đứng trước đơn vị đo: a mol -> $a$ mol, V lít -> $V$ lít, m gam -> $m$ gam
        s = Regex.Replace(s, @"(?<![\$\w])([amVnxy])\s+(mol|gam|g|kg|lít|lit|ml)\b", @"$$$1$ $2", RegexOptions.IgnoreCase);

        // Chuẩn hóa chỉ số dưới tiếng Việt hóa học: k_{gốc} -> k_{\text{gốc}}, m_{dd} -> m_{\text{dd}}
        s = Regex.Replace(s, @"([a-zA-Z])_\{gốc\}", @"$1_{\text{gốc}}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-zA-Z])_\{dd\}", @"$1_{\text{dd}}", RegexOptions.IgnoreCase);

        // 2. Thu gọn triệt để các dấu gạch chéo kép \\ trước lệnh LaTeX để tránh KaTeX hiểu nhầm thành lệnh xuống dòng:
        s = Regex.Replace(s, @"\\{2,}(text|frac|times|longrightarrow|xrightarrow|rightleftharpoons|mathrm|mathbf)\b", @"\$1");

        // 3. Phục hồi ngoặc nhọn bị thiếu ở chỉ số dưới trước dấu bằng: n_{H_2O = -> n_{H_2O} =
        s = Regex.Replace(s, @"n_\{([A-Za-z0-9]+)\s*=", "n_{$1} =");

        // Sửa lỗi dấu $ đặt ngay trước hoặc sau dấu bằng trong công thức e.g. n_{\text{triglyceride}} $=\frac{...} -> n_{\text{triglyceride}} = \frac{...}
        s = Regex.Replace(s, @"(?<=\w|\})\s*\$\s*=\s*\$?", " = ");
        s = Regex.Replace(s, @"\$\s*=\s*\$?|(?<=\w|\})\s*\$\s*=", " = ");

        // 4. Tự động bọc toàn diện chuỗi biểu thức tính toán nhiều vế và phương trình bảo toàn nguyên tố/khối lượng:
        // Ví dụ: 2n_X + 2n_{O_2} = 2n_{CO_2} + n_{H_2O} \longrightarrow 2n_X + 2 \times 0,35 = 2 \times 0,3 + 0,3 \longrightarrow n_X = 0,1 mol
        // 2n_{\text{ester}} + 2n_{O_2} = 2n_{CO_2} + n_{H_2O}
        // n = \frac{0,3}{0,1} = 3
        const string opPattern = @"(?:[-+\=><]|\\times|\\cdot|\\pm|\\longrightarrow|\\rightarrow|\\implies|\\Rightarrow|\\ge|\\le|\\approx|\\equiv|\\rightleftharpoons|\\xrightarrow(?:\s*\{[^{}]*\})*)";
        const string subPattern = @"(?:_\{(?:[^{}]*|\{[^{}]*\})*\}|_[a-zA-Z0-9]+)?";
        string unitPattern = @"(?:\s*\\*text\s*\{\s*(?:mol|gam|g|kg|lít|lit|ml)\s*\}|\s*\\*text\s*(?:mol|gam|g|kg|lít|lit|ml)\b|\s*(?:mol|gam|g|kg|lít|lit|ml))";
        const string expPattern = @"(?:\^\s*(?:\{[^{}]*\}|[0-9a-zA-Z]))?";
        string coreAtom = @"(?:\d*\s*[a-zA-Z]" + subPattern + expPattern + @"|\\frac\s*\{[^{}]*\}\s*\{[^{}]*\}|[0-9]+(?:[,\.][0-9]+)?" + expPattern + unitPattern + @"?)";
        string atomPattern = @"(?:\(\s*)*" + coreAtom + @"(?:\s*\))*";
        string fullEqPattern = @"(?<![\$\\\w\{_\^])(" + atomPattern + @"(?:[ \t]*" + opPattern + @"[ \t]*" + atomPattern + @")+)(?![\$\\\w\}_])";
        var eqSrc = s;
        s = Regex.Replace(eqSrc, fullEqPattern, m =>
        {
            // Bỏ qua nếu khớp nằm bên trong cặp $...$ đã có sẵn
            int dollarsBefore = 0;
            for (int di = 0; di < m.Index; di++) if (eqSrc[di] == '$') dollarsBefore++;
            if (dollarsBefore % 2 == 1) return m.Value;
            var raw = m.Groups[1].Value;
            // Tách ngoặc không cân bằng ở hai đầu ra ngoài công thức
            string lead = "", trail = "";
            while (true)
            {
                var tr = raw.Trim();
                int open = tr.Count(c => c == '('), close = tr.Count(c => c == ')');
                if (open > close && tr.StartsWith("(")) { int k = raw.IndexOf('('); lead += raw.Substring(0, k + 1); raw = raw.Substring(k + 1); }
                else if (close > open && tr.EndsWith(")")) { int k = raw.LastIndexOf(')'); trail = raw.Substring(k) + trail; raw = raw.Substring(0, k); }
                else if (tr.StartsWith("(") && tr.EndsWith(")") && open == close)
                {
                    // Ngoặc bao trọn toàn bộ biểu thức -> đưa ra ngoài công thức
                    int depth = 0; bool wrapsAll = true;
                    for (int ci = 0; ci < tr.Length; ci++)
                    {
                        if (tr[ci] == '(') depth++;
                        else if (tr[ci] == ')') { depth--; if (depth == 0 && ci < tr.Length - 1) { wrapsAll = false; break; } }
                    }
                    if (!wrapsAll) break;
                    int k1 = raw.IndexOf('('); int k2 = raw.LastIndexOf(')');
                    lead += raw.Substring(0, k1 + 1); trail = raw.Substring(k2) + trail; raw = raw.Substring(k1 + 1, k2 - k1 - 1);
                }
                else break;
            }
            var leadWs = Regex.Match(raw, @"^\s*").Value;
            var trailWs = Regex.Match(raw, @"\s*$").Value;
            var clean = raw.Replace("$", "").Trim();
            // Khôi phục đơn vị đo bên trong KaTeX math mode:
            clean = Regex.Replace(clean, @"(?<=\d)\s*(?:\\+text\s*\{\s*|\\*text\s*)(mol|gam|g|kg|lít|lit|ml)\}?", @"\text{ $1}", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"(?<=\d)\s+(mol|gam|g|kg|lít|lit|ml)\b", @"\text{ $1}", RegexOptions.IgnoreCase);
            return lead + leadWs + "$" + clean + "$" + trailWs + trail;
        });

        // Chuẩn hóa tiêu đề mục phân tích & lời giải chuẩn (sửa cả trường hợp bị lồng **Phân tích & **Lời giải:****):
        s = Regex.Replace(s, @"\*\*(?:Phân tích|Hướng dẫn)\s*&\s*\*+Lời giải:\*+", "**Phân tích & Lời giải:**", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\*\*(?:Phân tích|Hướng dẫn)\s*&?\s*Lời giải:\*\*", "**Phân tích & Lời giải:**", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(^|[\r\n•\-]\s*)(?!\*)((?:Phân tích\s*&\s*Lời giải|Hướng dẫn giải|Lời giải|Phương pháp giải|Bài toán mẫu|Bài mẫu|Ví dụ minh họa|Ví dụ)):\*\*", "$1**$2:**", RegexOptions.IgnoreCase);

        // Sửa lỗi đóng mở $ giữa công thức có dấu chấm/phẩy kết thúc trước chữ tiếng Việt e.g. $C_{17}H_{33}. 1 mol -> $C_{17}H_{33}$. 1 mol
        s = Regex.Replace(s, @"(\$(?:[A-Za-z0-9_\\\{\}\^]+))\.\s+([0-9a-zA-Zàáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ])", "$1$. $2");

        // Sửa phương trình phản ứng bị đóng mở $ vụn vặt: RCOOH$+ R'OH -> RCOOH + R'OH
        s = Regex.Replace(s, @"([A-Za-z0-9_\x27]+)\s*\$\s*([+=])\s*\$?\s*([A-Za-z0-9_\x27]+)", "$1 $2 $3");

        // 3. Hàn gắn các công thức LaTeX bị ngắt dòng ở giữa dấu ngoặc nhọn { } (tuyệt đối không ghép theo $)
        var lines = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var mergedLines = new List<string>();
        string buffer = string.Empty;
        int openBraces = 0;
        int mergeCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (string.IsNullOrEmpty(buffer))
            {
                int b = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] == '{' && (i == 0 || line[i - 1] != '\\')) b++;
                    else if (line[i] == '}' && (i == 0 || line[i - 1] != '\\')) b--;
                }

                if (b > 0 && (line.Contains("\\") || line.Contains("_{") || line.Contains("^{")))
                {
                    buffer = line.TrimEnd();
                    openBraces = b;
                    mergeCount = 0;
                }
                else
                {
                    mergedLines.Add(line);
                }
            }
            else
            {
                int b = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] == '{' && (i == 0 || line[i - 1] != '\\')) b++;
                    else if (line[i] == '}' && (i == 0 || line[i - 1] != '\\')) b--;
                }

                buffer += " " + line.Trim();
                openBraces += b;
                mergeCount++;

                if (openBraces <= 0 || mergeCount >= 6)
                {
                    mergedLines.Add(buffer);
                    buffer = string.Empty;
                    openBraces = 0;
                    mergeCount = 0;
                }
            }
        }
        if (!string.IsNullOrEmpty(buffer)) mergedLines.Add(buffer);
        s = string.Join("\n", mergedLines);

        // 4. Dọn dẹp các dấu $ dính với toán tử đơn vị
        s = Regex.Replace(s, @"\$\s*\\times\s*\$", " \\times ");
        s = Regex.Replace(s, @"\$\s*\\text\{(mol|gam|g|kg|lít|lit|ml)\}\s*\$", " \\text{$1} ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\$\s*\\text\{(mol|gam|g|kg|lít|lit|ml)\}", " \\text{$1}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\\text\{(mol|gam|g|kg|lít|lit|ml)\}\s*\$", "\\text{$1}", RegexOptions.IgnoreCase);

        // 5. Sửa các dòng có dấu $ lẻ ở cuối dòng (như 'A. HCOOC_2H_5$' -> 'A. $HCOOC_2H_5$')
        s = Regex.Replace(s, @"^([A-D][\.\:\)]\s*)([^\$\r\n]+)\$$", m => m.Groups[1].Value + "$" + m.Groups[2].Value + "$", RegexOptions.Multiline);
        s = Regex.Replace(s, @"\$\s*\.\s*\$", ".");

        // 6. Xử lý phương trình hóa học và công thức toán học đứng riêng dòng không có $ (Xử lý theo từng dòng riêng biệt, TUYỆT ĐỐI KHÔNG ghép qua dòng mới)
        var eqLines = s.Split('\n');
        for (int i = 0; i < eqLines.Length; i++)
        {
            var trimmedLine = eqLines[i].Trim();
            if (Regex.IsMatch(trimmedLine, @"\\(?:rightleftharpoons|xrightarrow|longrightarrow|rightarrow|leftrightharpoons|leftrightarrow)\b"))
            {
                // Tách nhãn mở đầu như "Phương trình: ..." hoặc "PTHH: ..."
                int colonIdx = trimmedLine.IndexOf(':');
                if (colonIdx > 0 && colonIdx < 30)
                {
                    string label = trimmedLine.Substring(0, colonIdx + 1).Trim();
                    string eq = trimmedLine.Substring(colonIdx + 1).Trim().TrimEnd('.').Trim();
                    string eqWithoutText = Regex.Replace(eq, @"\\text\{[^{}]*\}", "");
                    if (!Regex.IsMatch(eqWithoutText, @"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", RegexOptions.IgnoreCase))
                    {
                        eq = Regex.Replace(eq, @"^\${1,2}", "");
                        eq = Regex.Replace(eq, @"\${1,2}$", "").Trim();
                        eq = eq.Replace("$", "");
                        eqLines[i] = label + "\n$$" + eq + "$$";
                    }
                }
                else
                {
                    string trimmedWithoutText = Regex.Replace(trimmedLine, @"\\text\{[^{}]*\}", "");
                    if (!Regex.IsMatch(trimmedWithoutText, @"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", RegexOptions.IgnoreCase))
                    {
                        string eq = Regex.Replace(trimmedLine.TrimEnd('.').Trim(), @"^\${1,2}", "");
                        eq = Regex.Replace(eq, @"\${1,2}$", "").Trim();
                        eq = eq.Replace("$", "");
                        eqLines[i] = "$$" + eq + "$$";
                    }
                }
            }
            else
            {
                // Kiểm tra công thức toán/hóa chứa dấu = và \frac hoặc \sqrt (không chứa từ tiếng Việt ngoài \text{})
                string trimmedWithoutText = Regex.Replace(trimmedLine, @"\\text\{[^{}]*\}", "");
                if (trimmedLine.Contains("=") && Regex.IsMatch(trimmedLine, @"\\(?:frac|sqrt|lim|sum|prod|int)\b") &&
                    !Regex.IsMatch(trimmedWithoutText, @"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", RegexOptions.IgnoreCase))
                {
                    string eq = Regex.Replace(trimmedLine.TrimEnd('.').Trim(), @"^\${1,2}", "");
                    eq = Regex.Replace(eq, @"\${1,2}$", "").Trim();
                    eq = eq.Replace("$", "");
                    eqLines[i] = "$$" + eq + "$$";
                }
            }
        }
        s = string.Join("\n", eqLines);

        // 7. Tách chuỗi theo các khối math để TUYỆT ĐỐI KHÔNG can thiệp vào bên trong công thức đã bọc
        var mathParts = Regex.Split(s, @"(\$\$[\s\S]*?\$\$|\$[^\$\r\n]+?\$|\\\[[\s\S]*?\\\]|\\\([\s\S]*?\\\))");
        for (int pi = 0; pi < mathParts.Length; pi++)
        {
            var p = mathParts[pi];
            if (string.IsNullOrEmpty(p)) continue;
            if (p.StartsWith("$$") || p.StartsWith("$") || p.StartsWith("\\[") || p.StartsWith("\\("))
            {
                // Giữ nguyên khối math đã chuẩn hóa
                continue;
            }

            // Hàm trợ giúp bọc an toàn trên các đoạn văn bản chưa chứa $:
            string WrapUntouchedChunk(string textChunk, string pattern, MatchEvaluator evaluator)
            {
                var subChunks = Regex.Split(textChunk, @"(\$[^\$\r\n]+?\$)");
                for (int sci = 0; sci < subChunks.Length; sci++)
                {
                    if (subChunks[sci].StartsWith("$") && subChunks[sci].EndsWith("$")) continue;
                    subChunks[sci] = Regex.Replace(subChunks[sci], pattern, evaluator);
                }
                return string.Join("", subChunks);
            }

            // Chỉ bọc công thức trên các đoạn phi-math:
            // 7.1. Bọc công thức cấu tạo ester có liên kết đôi/đơn (như CH_3COOCH=CH_2, CH_2=CH-COOCH_3, RCOOR')
            p = WrapUntouchedChunk(p, @"(?<![\$\w])([A-Z][a-zA-Z0-9_']*(?:COO|COOH|COONa)[A-Za-z0-9_\-='\(]+)(?![\$\w])", m => "$" + m.Groups[1].Value + "$");

            // 7.2. Tự động bọc công thức có chỉ số dưới dạng C_{15}H_{31}COOH hoặc (RCOO)_3C_3H_5 trong văn bản thường
            p = WrapUntouchedChunk(p, @"(?<![\$\\\w])((?:\([A-Z][a-zA-Z0-9_\\\{\}\^]*\)\d*|[A-Z][a-z]?(?:_\{\d+\}|_\d+)?)+[A-Za-z0-9_\\\{\}]*)(?![\$\\\w\}])", m =>
            {
                var g = m.Groups[1].Value;
                if (g.Contains('_') || g.Contains('(') || Regex.IsMatch(g, @"^(?:COOH|COONa|CH_3|C_2H_5)"))
                    return "$" + g + "$";
                return g;
            });

            // 7.3. Bọc ion có điện tích như H^+, OH^-, SO_4^{2-}, CH_3COO^-
            p = WrapUntouchedChunk(p, @"(?<![$\\\w])((?:[A-Z][a-z]?(?:_\d+)?)+\^\{?[+-]?\d*[+-]?\}?)(?![$\w])", m => "$" + m.Groups[1].Value + "$");

            // 7.4. Bọc công thức hóa học đơn phân tử hoặc đa nguyên tố đứng độc lập (như C_3H_6O_2, CO_2, H_2O, O_2, N_2, H_2, CH_3COOH, C_2H_5OH, H_2SO_4)
            p = WrapUntouchedChunk(p, @"(?<![\$\\\w\{_])([A-Z][a-z]?(?:_\d+|_\{\d+\}|_\{?[0-9a-zA-Z\+\-]+\}?|\d+)*(?:[A-Z][a-z]?(?:_\d+|_\{\d+\}|_\{?[0-9a-zA-Z\+\-]+\}?|\d+)*)*)(?![\$\\\w\}_])", m =>
            {
                var g = m.Groups[1].Value;
                if (g.Length > 1 && (g.Contains('_') || Regex.IsMatch(g, @"(?:COOH|COONa|OH|CHO|SO_4|NO_3)") || (Regex.IsMatch(g, @"[A-Z]") && Regex.IsMatch(g, @"\d"))))
                {
                    return "$" + g + "$";
                }
                return g;
            });

            // Khắc phục triệt để lỗi lồng dấu $ bên trong ngoặc đơn e.g. $($C_{17}H_{35}COO$)_3C_3H_5$
            p = Regex.Replace(p, @"\$\(\$([^\$]+)\$([^\$]*)\$", @"$($1)$2$");

            mathParts[pi] = p;
        }
        s = string.Join("", mathParts);

        // Dọn dẹp dấu $ dính nhầm vào đơn vị ở cuối câu e.g. "gam$." -> "gam." hoặc "mol$" -> "mol"
        s = Regex.Replace(s, @"\s*(mol|gam|g|kg|lít|lit|ml)\$\.", " $1.", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*(mol|gam|g|kg|lít|lit|ml)\$(?=[\s\.,;\*\)]|$)", " $1", RegexOptions.IgnoreCase);

        // 8. Cân bằng dấu $ đơn lẻ bị cô lập trên từng dòng
        var finalLines = s.Split('\n');
        for (int i = 0; i < finalLines.Length; i++)
        {
            var l = finalLines[i];
            int dollarCount = l.Count(c => c == '$');
            if (dollarCount % 2 != 0)
            {
                // Nếu kết thúc bằng $ thừa hoặc $. thì bỏ $ cuối
                if (Regex.IsMatch(l.TrimEnd(), @"\$\.?$"))
                {
                    l = Regex.Replace(l.TrimEnd(), @"\$\.$", ".");
                    l = Regex.Replace(l.TrimEnd(), @"\$$", "");
                }
                else
                {
                    // Tự động đóng $ cho công thức mở dở đứng trước từ tiếng Việt
                    l = Regex.Replace(l, @"(?<![\$\w])\$([A-Za-z0-9_+\-^\\\{\}\x27]+)(?=\s+[a-zA-Zàáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ])", m => "$" + m.Groups[1].Value + "$");
                    if (l.Count(c => c == '$') % 2 != 0)
                    {
                        l = Regex.Replace(l, @"(?<![\$\w])\$([A-Za-z0-9_+\-^\\\{\}\x27]+)(?=[\s\.,;\*\)]|$)", m => "$" + m.Groups[1].Value + "$");
                    }
                    if (l.Count(c => c == '$') % 2 != 0)
                    {
                        l = l.Replace("$", "");
                    }
                }
                finalLines[i] = l;
            }
        }
        s = string.Join("\n", finalLines);

        // 9. Loại bỏ các dấu $ lồng bên trong dấu ngoặc nhọn { ... $ ... }
        while (Regex.IsMatch(s, @"\{([^{}]*)\$([^{}]*)\}"))
        {
            s = Regex.Replace(s, @"\{([^{}]*)\$([^{}]*)\}", "{$1$2}");
        }

        // 10. Phục hồi nguyên vẹn các khối đồ thị và bảng biến thiên đã bảo vệ ở bước 0.1
        for (int bi = 0; bi < preservedBlocks.Count; bi++)
        {
            string idxCode = new string(bi.ToString().Select(ch => (char)('a' + (ch - '0'))).ToArray());
            s = s.Replace($"@@mathlabblock{idxCode}@@", preservedBlocks[bi]);
        }

        return s;
    }

    private static string CleanSingleQuoteSurrounding(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return string.Empty;
        var s = val.Trim();
        while (s.Length > 0 && (s.StartsWith("'") || s.StartsWith("\"") || s.StartsWith("`") || s.StartsWith("‘") || s.StartsWith("“")))
            s = s.Substring(1).Trim();
        while (s.Length > 0 && (s.EndsWith("'") || s.EndsWith("\"") || s.EndsWith("`") || s.EndsWith("’") || s.EndsWith("”")))
            s = s.Substring(0, s.Length - 1).Trim();
        return s;
    }

    private static string CleanOptionString(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return string.Empty;
        var s = CleanSingleQuoteSurrounding(val);

        s = Regex.Replace(s, @"^(?:\([A-D]\)|[A-D][\.\:\)])\s*", "", RegexOptions.IgnoreCase).Trim();

        s = CleanSingleQuoteSurrounding(s);

        // Nối dòng nếu phương án hóa học bị rớt dòng
        s = Regex.Replace(s, @"\r?\n\s*", "");

        // Sửa dấu nháy đơn dính vào số thập phân: 0',01 -> 0,01 hoặc 0,'01 -> 0,01
        s = Regex.Replace(s, @"(?<=\d)['’](?=,\d)", "");
        s = Regex.Replace(s, @"(?<=\d,)['’](?=\d)", "");
        s = Regex.Replace(s, @"(?<=\d)['’](?=\d)", ".");

        // Nếu là công thức hóa học dạng phẳng không có chỉ số dưới (ví dụ CH3COOCH3, C2H5OH, H2SO4)
        if (Regex.IsMatch(s, @"^[A-Z][a-zA-Z0-9\(\)\-+=]*$") && Regex.IsMatch(s, @"\d"))
        {
            s = Regex.Replace(s, @"([A-Z][a-z]?|\))(\d+)", "$1_$2");
        }

        // Nếu là công thức chứa chỉ số dưới chưa bọc KaTeX, tự động bọc trong $...$
        if (Regex.IsMatch(s, @"^[A-Za-z0-9_\(\)\+\-\*\^=]+$") && s.Contains('_') && !s.StartsWith("$"))
        {
            s = "$" + s + "$";
        }

        return s;
    }

    private static ImportCurriculumRequestDto? ExtractCurriculumViaRegex(string text, string domainId, string domainName, string? fileName = null, string? extractedText = null)
    {
        try
        {
            var result = new ImportCurriculumRequestDto
            {
                DomainId = domainId,
                DomainName = domainName
            };

            var domMatch = Regex.Match(text, @"""(?:domainId|domain_id)""\s*:\s*""([^""]+)""");
            if (domMatch.Success) result.DomainId = domMatch.Groups[1].Value;

            var domNameMatch = Regex.Match(text, @"""(?:domainName|domain_name)""\s*:\s*""([^""]+)""");
            if (domNameMatch.Success) result.DomainName = domNameMatch.Groups[1].Value;

            var chMatches = Regex.Matches(text, @"""(?:chapterName|chapter_name|name|title|chuong)""\s*:\s*""([^""]+)""");
            if (chMatches.Count == 0) return null;

            string fallbackChapName = "Chương 1: Trọng tâm chuyên đề";
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                fallbackChapName = Path.GetFileNameWithoutExtension(fileName)
                    .Replace("(BẢN HS)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(BẢN GV)", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }

            for (int c = 0; c < chMatches.Count; c++)
            {
                var chMatch = chMatches[c];
                int chStart = chMatch.Index;
                int chEnd = (c + 1 < chMatches.Count) ? chMatches[c + 1].Index : text.Length;
                string chBlock = text.Substring(chStart, chEnd - chStart);

                string chName = chMatch.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(chName)) chName = fallbackChapName;

                var ch = new ImportChapterDto
                {
                    ChapterName = chName,
                    Weight = 0.35
                };

                var lesMatches = Regex.Matches(chBlock, @"""(?:lessonName|lesson_name|name|title|section|topic|baiHoc)""\s*:\s*""([^""]+)""");
                if (lesMatches.Count == 0)
                {
                    // Thử tìm patterns trực tiếp dưới chapter
                    var directPatMatches = Regex.Matches(chBlock, @"""(?:patternName|pattern_name|name|title|dangBai)""\s*:\s*""([^""]+)""");
                    if (directPatMatches.Count > 0)
                    {
                        var syntheticLesson = new ImportLessonDto
                        {
                            LessonName = chName,
                            Weight = 0.2
                        };

                        for (int p = 0; p < directPatMatches.Count; p++)
                        {
                            var patMatch = directPatMatches[p];
                            int patStart = patMatch.Index;
                            int patEnd = (p + 1 < directPatMatches.Count) ? directPatMatches[p + 1].Index : chBlock.Length;
                            string patBlock = chBlock.Substring(patStart, patEnd - patStart);

                            syntheticLesson.Patterns.Add(new ImportPatternDto
                            {
                                PatternName = patMatch.Groups[1].Value,
                                Classification = ExtractRegexField(patBlock, "classification") ?? "Dạng bài trọng tâm",
                                CoreSkill = ExtractRegexField(patBlock, "coreSkill") ?? ExtractRegexField(patBlock, "core_skill") ?? "Tư duy giải toán",
                                TheoryRecap = ExtractRegexField(patBlock, "theoryRecap") ?? ExtractRegexField(patBlock, "theory_recap") ?? string.Empty,
                                SolutionMethod = ExtractRegexField(patBlock, "solutionMethod") ?? ExtractRegexField(patBlock, "solution_method") ?? string.Empty,
                                DurationSeconds = 600,
                                Weight = 0.05
                            });
                        }

                        ch.Lessons.Add(syntheticLesson);
                    }
                }
                else
                {
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

                        var patMatches = Regex.Matches(lesBlock, @"""(?:patternName|pattern_name|name|title|dangBai)""\s*:\s*""([^""]+)""");
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

                            var qMatches = Regex.Matches(patBlock, @"""(?:contentLatex|content_latex|content|question|cauHoi)""\s*:\s*""(.*?)(?<!\\)""", RegexOptions.Singleline);
                            for (int q = 0; q < qMatches.Count; q++)
                            {
                                var qMatch = qMatches[q];
                                int qStart = qMatch.Index;
                                int qEnd = (q + 1 < qMatches.Count) ? qMatches[q + 1].Index : patBlock.Length;
                                string qBlock = patBlock.Substring(qStart, qEnd - qStart);

                                int diff = 2;
                                var diffMatch = Regex.Match(qBlock, @"""(?:difficultyLevel|difficulty_level|level)""\s*:\s*(\d+)");
                                if (diffMatch.Success && int.TryParse(diffMatch.Groups[1].Value, out int dVal))
                                {
                                    diff = dVal;
                                }

                                string correct = NormalizeCorrectOption(ExtractRegexField(qBlock, "correctOption") ?? ExtractRegexField(qBlock, "correct_option") ?? ExtractRegexField(qBlock, "answer"));

                                string qContent = ExtractRegexField(qBlock, "contentLatex") ?? ExtractRegexField(qBlock, "content_latex") ?? qMatch.Groups[1].Value;
                                pat.Questions.Add(new ImportQuestionDto
                                {
                                    DifficultyLevel = diff,
                                    ContentLatex = qContent,
                                    OptionA = ExtractRegexField(qBlock, "optionA") ?? ExtractRegexField(qBlock, "option_a") ?? string.Empty,
                                    OptionB = ExtractRegexField(qBlock, "optionB") ?? ExtractRegexField(qBlock, "option_b") ?? string.Empty,
                                    OptionC = ExtractRegexField(qBlock, "optionC") ?? ExtractRegexField(qBlock, "option_c") ?? string.Empty,
                                    OptionD = ExtractRegexField(qBlock, "optionD") ?? ExtractRegexField(qBlock, "option_d") ?? string.Empty,
                                    CorrectOption = correct,
                                    Explanation = ExtractRegexField(qBlock, "explanation") ?? ExtractRegexField(qBlock, "solution")
                                });
                            }

                            les.Patterns.Add(pat);
                        }

                        if (les.Patterns.Count > 0)
                        {
                            ch.Lessons.Add(les);
                        }
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
