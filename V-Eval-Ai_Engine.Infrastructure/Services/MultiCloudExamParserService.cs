using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ bóc tách đề thi PDF đa đám mây (SGK-Unified Page-Batched Gemini Vision AI + Auto-Solve & OpenAI/PdfPig Failover)
/// </summary>
public class MultiCloudExamParserService : IExamParserService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MultiCloudExamParserService> _logger;
    private readonly PdfImageExtractor _imageExtractor;
    private readonly IPromptTemplateService _promptTemplateService;
    private static int _keyRotationIndex = 0;

    public MultiCloudExamParserService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<MultiCloudExamParserService> logger,
        PdfImageExtractor imageExtractor,
        IPromptTemplateService promptTemplateService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _imageExtractor = imageExtractor;
        _promptTemplateService = promptTemplateService;
    }

    private List<string> ResolveApiKeys()
    {
        var keys = new List<string>();

        // 1. Lấy từ danh sách mảng AiSettings:GeminiApiKeys nếu có
        var keysSection = _configuration.GetSection("AiSettings:GeminiApiKeys").Get<string[]>();
        if (keysSection != null && keysSection.Length > 0)
        {
            keys.AddRange(keysSection);
        }

        // 2. Lấy từ AiSettings:GeminiApiKey (chuỗi đơn hoặc phân tách bởi dấu phẩy/chấm phẩy)
        var singleOrDelimited = _configuration.GetSection("AiSettings")["GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(singleOrDelimited))
        {
            var splitKeys = singleOrDelimited.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            keys.AddRange(splitKeys);
        }

        // 3. Lấy từ biến môi trường nếu có
        var envKeys = Environment.GetEnvironmentVariable("GEMINI_API_KEYS") 
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEYS");
        if (!string.IsNullOrWhiteSpace(envKeys))
        {
            keys.AddRange(envKeys.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        return keys
            .Select(k => k.Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_"))
            .Distinct()
            .ToList();
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

        return new List<string> { "gpt-4o", "gpt-4o-mini", "o3-mini", "chatgpt-4o-latest" };
    }

    private List<string> ResolveGeminiModels()
    {
        // Đồng bộ 100% chuỗi model đang vận hành ổn định của phân hệ SGK (PdfPigTextbookParserService)
        var models = _configuration.GetSection("AiSettings:GeminiModels").Get<string[]>();
        if (models != null && models.Length > 0)
        {
            return models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var single = _configuration["AiSettings:GeminiModel"];
        if (!string.IsNullOrWhiteSpace(single))
        {
            return new List<string> { single.Trim() };
        }

        return new List<string>
        {
            "gemini-3.1-flash-lite",
            "gemini-3.5-flash-lite",
            "gemini-flash-lite-latest",
            "gemini-3.5-flash"
        };
    }

    private static List<string> GetRotatedKeys(List<string> keys)
    {
        if (keys.Count <= 1) return keys;
        int start = Math.Abs(Interlocked.Increment(ref _keyRotationIndex)) % keys.Count;
        var rotated = new List<string>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            rotated.Add(keys[(start + i) % keys.Count]);
        }
        return rotated;
    }

    /// <summary>
    /// Phân giải Endpoint Gemini đồng bộ 100% với phân hệ SGK (generativelanguage.googleapis.com/v1beta),
    /// đồng thời cho phép ghi đè qua AiSettings:GeminiBaseUrl nếu cần đi qua proxy/gateway tùy chỉnh.
    /// </summary>
    public static string ResolveGeminiEndpointUrl(string modelName, string apiKey, string? configuredBaseUrl = null)
    {
        string baseUrl = !string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? configuredBaseUrl.Trim().TrimEnd('/')
            : "https://generativelanguage.googleapis.com/v1beta";

        return $"{baseUrl}/models/{modelName}:generateContent?key={apiKey}";
    }

    private async Task<string?> TryCallOpenAiAsync(
        string modelName, 
        string apiKey, 
        string base64Data, 
        string ocrPrompt, 
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
                    ["content"] = ocrPrompt
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Hãy đọc tài liệu đề thi và trích xuất đầy đủ toàn bộ 120 câu hỏi sang đúng định dạng JSON: { \"passages\": [...], \"single_questions\": [...] }."
                        },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:application/pdf;base64,{base64Data}"
                            }
                        }
                    }
                }
            },
            ["response_format"] = new JsonObject { ["type"] = "json_object" }
        };

        if (modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase))
        {
            requestBody["max_completion_tokens"] = 65536;
        }
        else
        {
            requestBody["temperature"] = 0.0;
            requestBody["max_tokens"] = 16384;
        }

        string openAiUrl = _configuration["AiSettings:OpenAiBaseUrl"] ?? "https://api.openai.com/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, openAiUrl)
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI mô hình '{Model}' trả về HTTP {Code}: {Content}", modelName, response.StatusCode, content);
            return null;
        }

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var textProp))
            {
                return textProp.GetString();
            }
        }

        return null;
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length <= 10) return "****";
        return $"{key[..6]}...{key[^6..]}";
    }

    public async Task<ParsedExamDto> ParsePdfAsync(
        Stream pdfStream,
        string fileName,
        CancellationToken cancellationToken = default,
        Action<string>? onProgress = null)
    {
        var apiKeys = ResolveApiKeys();
        var openAiKey = ResolveOpenAiKey();

        _logger.LogInformation("Bắt đầu đọc dữ liệu PDF '{FileName}' (Gemini Keys: {KeyCount}, OpenAI: {HasOpenAi})...",
            fileName, apiKeys.Count, !string.IsNullOrEmpty(openAiKey));

        // 1. Đọc stream PDF thành byte[]
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

        int totalPages = 1;
        try
        {
            using var docInfo = PdfDocument.Open(pdfBytes);
            totalPages = Math.Max(1, docInfo.NumberOfPages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể đếm số trang bằng PdfPig cho '{FileName}'.", fileName);
        }

        onProgress?.Invoke($"Đang khởi tạo trích xuất hình ảnh & chia cụm trang ({totalPages} trang)...");

        // 1.5. Trích xuất toàn bộ ảnh gốc cục bộ từ PDF bằng PdfPig + PDFtoImage
        string examId = Guid.NewGuid().ToString("N")[..8];
        string webRootPath = GetWebRootPath();
        List<ExtractedPdfImage> localImages = new();
        try
        {
            _logger.LogInformation("Bắt đầu trích xuất nhanh hình ảnh cục bộ từ PDF (ExamId: {ExamId})...", examId);
            localImages = _imageExtractor.ExtractImages(pdfBytes, webRootPath, examId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể trích xuất ảnh cục bộ từ PDF, tiếp tục luồng Vision AI...");
        }

        if (apiKeys.Count == 0 && string.IsNullOrEmpty(openAiKey))
        {
            _logger.LogWarning("Chưa cấu hình API Key hợp lệ cho cả OpenAI và Google Gemini. Tự động dùng Local Fallback Parser (PdfPig)...");
            onProgress?.Invoke("Không có API Key Cloud — đang chạy bộ phân tích cục bộ PdfPig...");
            return await RunLocalFallbackParserAsync(pdfBytes, fileName, localImages, webRootPath, examId, cancellationToken);
        }

        var geminiModels = ResolveGeminiModels();

        // =========================================================================
        // LUỒNG CHÍNH (SGK-UNIFIED): PAGE-BATCHED HIGH-CLARITY GEMINI VISION AI + AUTO-SOLVE
        // Render từng cụm 2 trang ảnh JPEG sắc nét -> gửi tới đúng chuỗi model SGK -> AI đọc KaTeX,
        // bảng biểu, đồ thị, gạch chân tiếng Anh VÀ tự giải chọn đáp án đúng + viết lời giải KaTeX!
        // =========================================================================
        if (apiKeys.Count > 0 && geminiModels.Count > 0)
        {
            var batchedResult = await ParseExamWithPageBatchedVisionAsync(
                pdfBytes,
                fileName,
                totalPages,
                apiKeys,
                geminiModels,
                onProgress,
                cancellationToken);

            int batchedTotalQ = (batchedResult?.SingleQuestions?.Count ?? 0)
                                + (batchedResult?.Passages?.Sum(p => p.Questions.Count) ?? 0);

            if (batchedResult != null && batchedTotalQ > 0)
            {
                CleanExamDto(batchedResult);
                if (localImages.Count > 0)
                {
                    _imageExtractor.MapImagesToExam(batchedResult, localImages, webRootPath, examId);
                }

                _logger.LogInformation(
                    "Bóc tách & giải đề '{FileName}' thành công qua Page-Batched Gemini Vision AI: {TotalQ} câu hỏi ({Passages} chùm, {Singles} câu đơn) trên {Pages} trang.",
                    fileName, batchedTotalQ, batchedResult.TotalPassages, batchedResult.TotalSingleQuestions, batchedResult.TotalPages);

                return batchedResult;
            }

            _logger.LogWarning("Page-Batched Gemini Vision AI không trả về câu hỏi nào cho '{FileName}'. Thử dự phòng OpenAI/Local...", fileName);
        }

        // LUỒNG DỰ PHÒNG 1: OpenAI GPT-4o Vision (nếu có OpenAI API key)
        if (!string.IsNullOrEmpty(openAiKey))
        {
            onProgress?.Invoke("Đang thử phương án dự phòng OpenAI Vision...");
            string ocrPrompt = await _promptTemplateService.RenderPromptAsync("ExamIngestionVerbatimOcr.prompt.txt", cancellationToken: cancellationToken);
            string base64Data = Convert.ToBase64String(pdfBytes);
            var openAiModels = ResolveOpenAiModels();
            int maxAttempts = Math.Max(1, _configuration.GetValue<int>("AiSettings:MaxAttempts", 5));
            int attemptCount = 0;

            foreach (var gptModel in openAiModels)
            {
                if (attemptCount >= maxAttempts) break;
                attemptCount++;
                try
                {
                    string? gptResult = await TryCallOpenAiAsync(gptModel, openAiKey, base64Data, ocrPrompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        var parsed = TryDeserializeExamJson(gptResult);
                        int totalQ = (parsed?.SingleQuestions?.Count ?? 0) + (parsed?.Passages?.Sum(p => p.Questions.Count) ?? 0);
                        if (parsed != null && totalQ > 0)
                        {
                            CleanExamDto(parsed);
                            parsed.Format = $"V-ACT Exam ({gptModel})";
                            parsed.FileName = fileName;
                            parsed.TotalPages = Math.Max(totalPages, GetMaxPageNumber(parsed));
                            parsed.TotalPassages = parsed.Passages.Count;
                            parsed.TotalSingleQuestions = parsed.SingleQuestions.Count;
                            if (localImages.Count > 0)
                            {
                                _imageExtractor.MapImagesToExam(parsed, localImages, webRootPath, examId);
                            }
                            return parsed;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Lỗi kết nối tới OpenAI '{ModelName}'.", gptModel);
                }
            }
        }

        // LUỒNG DỰ PHÒNG 2 (CUỐI CÙNG): Local Fallback Parser (Python pymupdf -> C# PdfPigLocalExamParser)
        _logger.LogWarning("Tất cả mô hình Cloud AI đều không hoàn thành. Tự động kích hoạt Local Fallback Parser...");
        onProgress?.Invoke("Cloud AI tạm bận — đang chạy bộ phân tích cục bộ PdfPig...");
        return await RunLocalFallbackParserAsync(pdfBytes, fileName, localImages, webRootPath, examId, cancellationToken);
    }

    private async Task<ParsedExamDto?> ParseExamWithPageBatchedVisionAsync(
        byte[] pdfBytes,
        string fileName,
        int totalPages,
        List<string> apiKeys,
        List<string> geminiModels,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        int pagesPerBatch = Math.Clamp(_configuration.GetValue<int>("AiSettings:ExamParserPagesPerBatch", 2), 1, 4);
        int renderDpi = Math.Clamp(_configuration.GetValue<int>("AiSettings:ExamParserRenderDpi", 115), 80, 150);
        int batchTimeoutSeconds = Math.Clamp(_configuration.GetValue<int>("AiSettings:ExamParserBatchTimeoutSeconds", 75), 30, 180);
        int maxConcurrency = Math.Clamp(Math.Min(apiKeys.Count, 2), 1, 3);

        var pageBatches = new List<List<int>>();
        for (int p = 1; p <= totalPages; p += pagesPerBatch)
        {
            var batch = new List<int>();
            for (int offset = 0; offset < pagesPerBatch && p + offset <= totalPages; offset++)
            {
                batch.Add(p + offset);
            }
            pageBatches.Add(batch);
        }

        _logger.LogInformation(
            "Khởi chạy Page-Batched Gemini Vision AI cho '{FileName}': {TotalPages} trang -> {BatchCount} cụm ({PagesPerBatch} trang/cụm, DPI {Dpi}, song song {Concurrency}, models: [{Models}]).",
            fileName, totalPages, pageBatches.Count, pagesPerBatch, renderDpi, maxConcurrency, string.Join(", ", geminiModels));

        onProgress?.Invoke($"Đang đọc & giải đề bằng Gemini Vision AI ({geminiModels[0]}) — 0/{pageBatches.Count} cụm trang...");

        var batchResults = new ParsedExamDto?[pageBatches.Count];
        int completedBatches = 0;
        int runningQuestions = 0;
        int aiSucceededBatches = 0;

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>();

        for (int idx = 0; idx < pageBatches.Count; idx++)
        {
            int batchIndex = idx;
            var pageNumbers = pageBatches[idx];

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (batchIndex > 0)
                    {
                        await Task.Delay(350, cancellationToken);
                    }

                    var (dto, usedModel) = await ProcessSingleVisionBatchAsync(
                        pdfBytes,
                        fileName,
                        pageNumbers,
                        totalPages,
                        renderDpi,
                        batchTimeoutSeconds,
                        apiKeys,
                        geminiModels,
                        cancellationToken);

                    if (dto != null)
                    {
                        batchResults[batchIndex] = dto;
                        Interlocked.Increment(ref aiSucceededBatches);
                        int batchQ = (dto.SingleQuestions?.Count ?? 0) + (dto.Passages?.Sum(p => p.Questions.Count) ?? 0);
                        int newTotalQ = Interlocked.Add(ref runningQuestions, batchQ);
                        int done = Interlocked.Increment(ref completedBatches);

                        onProgress?.Invoke(
                            $"Gemini Vision AI ({usedModel}): Hoàn tất cụm {done}/{pageBatches.Count} (Trang {pageNumbers[0]}–{pageNumbers[^1]} · đã bóc & giải {newTotalQ} câu)...");
                    }
                    else
                    {
                        // Dự phòng riêng cho cụm trang bị lỗi bằng PdfPig cục bộ, bảo toàn nguyên vẹn các cụm AI khác
                        var localSlice = PdfPigLocalExamParser.ParsePageRange(pdfBytes, fileName, pageNumbers);
                        batchResults[batchIndex] = localSlice;
                        int sliceQ = (localSlice.SingleQuestions?.Count ?? 0) + (localSlice.Passages?.Sum(p => p.Questions.Count) ?? 0);
                        int newTotalQ = Interlocked.Add(ref runningQuestions, sliceQ);
                        int done = Interlocked.Increment(ref completedBatches);

                        onProgress?.Invoke(
                            $"Hoàn tất cụm {done}/{pageBatches.Count} (Trang {pageNumbers[0]}–{pageNumbers[^1]} · đã bóc {newTotalQ} câu)...");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        if (aiSucceededBatches == 0)
        {
            return null;
        }

        var nonNullBatches = batchResults.Where(r => r != null).Cast<ParsedExamDto>().ToList();
        var merged = MergeBatchExamResults(nonNullBatches, fileName, totalPages);
        merged.Format = aiSucceededBatches == pageBatches.Count
            ? $"V-ACT Exam (Gemini Vision AI · {geminiModels[0]})"
            : $"V-ACT Exam (Gemini Vision AI {aiSucceededBatches}/{pageBatches.Count} cụm)";

        onProgress?.Invoke($"Hoàn tất bóc tách & tự giải {merged.SingleQuestions.Count + merged.Passages.Sum(p => p.Questions.Count)} câu hỏi!");
        return merged;
    }

    private async Task<(ParsedExamDto? Result, string UsedModel)> ProcessSingleVisionBatchAsync(
        byte[] pdfBytes,
        string fileName,
        List<int> pageNumbers,
        int totalPages,
        int renderDpi,
        int batchTimeoutSeconds,
        List<string> apiKeys,
        List<string> geminiModels,
        CancellationToken cancellationToken)
    {
        var pageImages = new List<(int PageNumber, string Base64)>();
        foreach (int pNum in pageNumbers)
        {
            string? b64 = RenderExamPageJpegBase64(pdfBytes, pNum, renderDpi);
            if (!string.IsNullOrWhiteSpace(b64))
            {
                pageImages.Add((pNum, b64));
            }
        }

        if (pageImages.Count == 0)
        {
            return (null, string.Empty);
        }

        string batchPrompt = BuildExamBatchVisionPrompt(fileName, pageNumbers, totalPages);
        var partsArray = new JsonArray { new JsonObject { ["text"] = batchPrompt } };
        foreach (var item in pageImages)
        {
            partsArray.Add(new JsonObject { ["text"] = $"=== TRANG {item.PageNumber}/{totalPages} ===" });
            partsArray.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = "image/jpeg",
                    ["data"] = item.Base64
                }
            });
        }

        string? configuredGeminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"];
        var rotatedKeys = GetRotatedKeys(apiKeys);

        // Vòng lặp chuẩn SGK: Model ngoài, Key xoay vòng trong — Fail-Fast trên 503/500/504/404/400
        // Tuyệt đối không gửi thinkingConfig hay nested responseSchema (gây HTTP 400 trên một số model Lite),
        // giống 100% payload đã kiểm chứng của PdfPigTextbookParserService (SGK).
        var payloadObj = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = partsArray
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["temperature"] = 0.0,
                ["maxOutputTokens"] = 16384
            }
        };
        string jsonPayload = payloadObj.ToJsonString();

        foreach (var modelName in geminiModels)
        {
            foreach (var apiKey in rotatedKeys)
            {
                string maskedKey = MaskKey(apiKey);
                string url = ResolveGeminiEndpointUrl(modelName, apiKey, configuredGeminiBaseUrl);

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(batchTimeoutSeconds));

                    using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    using var response = await _httpClient.PostAsync(url, content, cts.Token);
                    string body = await response.Content.ReadAsStringAsync(cancellationToken);
                    int statusCode = (int)response.StatusCode;

                    if (response.IsSuccessStatusCode)
                    {
                        string? rawText = ExtractTextPartsIgnoringThoughts(body);
                        var parsed = TryDeserializeExamJson(rawText);
                        if (parsed != null)
                        {
                            // Đảm bảo mỗi câu đều có page_number hợp lệ trong cụm trang
                            int defaultPage = pageNumbers[0];
                            foreach (var q in parsed.SingleQuestions)
                                if (q.PageNumber <= 0) q.PageNumber = defaultPage;
                            foreach (var p in parsed.Passages)
                                foreach (var q in p.Questions)
                                    if (q.PageNumber <= 0) q.PageNumber = defaultPage;

                            int count = parsed.SingleQuestions.Count + parsed.Passages.Sum(p => p.Questions.Count);
                            if (count > 0 || parsed.Passages.Count > 0)
                            {
                                _logger.LogInformation(
                                    "Vision Batch Trang {Start}-{End} thành công với '{Model}' [{Key}]: {Count} câu, {Passages} chùm.",
                                    pageNumbers[0], pageNumbers[^1], modelName, maskedKey, count, parsed.Passages.Count);
                                return (parsed, modelName);
                            }

                            // Trang bìa / hướng dẫn không có câu hỏi nào -> trả về DTO rỗng hợp lệ để không rơi vào fallback
                            return (parsed, modelName);
                        }

                        _logger.LogWarning(
                            "Vision Batch Trang {Start}-{End} với '{Model}' [{Key}] trả 200 nhưng không parse được JSON. Thử key/model tiếp theo...",
                            pageNumbers[0], pageNumbers[^1], modelName, maskedKey);
                        continue;
                    }

                    string errPreview = body.Length > 240 ? body[..240] : body;
                    if (statusCode is 503 or 500 or 504 or 404 or 400)
                    {
                        _logger.LogWarning(
                            "Vision Batch Trang {Start}-{End} Model '{Model}' trả HTTP {Status}: {Err}. Chuyển ngay model kế tiếp...",
                            pageNumbers[0], pageNumbers[^1], modelName, statusCode, errPreview);
                        break; // Fail-fast sang model tiếp theo giống SGK
                    }

                    _logger.LogWarning(
                        "Vision Batch Trang {Start}-{End} Key [{Key}] trên '{Model}' trả HTTP {Status}: {Err}. Đổi key tiếp theo...",
                        pageNumbers[0], pageNumbers[^1], maskedKey, modelName, statusCode, errPreview);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Vision Batch Trang {Start}-{End} Model '{Model}' quá hạn {Sec}s. Đổi ngay model tiếp theo.",
                        pageNumbers[0], pageNumbers[^1], modelName, batchTimeoutSeconds);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Lỗi gọi Gemini Vision Batch Trang {Start}-{End} với '{Model}' [{Key}].",
                        pageNumbers[0], pageNumbers[^1], modelName, maskedKey);
                }
            }
        }

        return (null, string.Empty);
    }

    private static string? RenderExamPageJpegBase64(byte[] pdfBytes, int pageNumber, int dpi)
    {
        try
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            using var skBitmap = Conversion.ToImage(pdfStream, page: pageNumber - 1, options: new RenderOptions { Dpi = dpi });
            if (skBitmap == null) return null;

            const int capDim = 1400; // Đủ sắc nét để đọc rõ công thức phân số/căn/logarit, bảng 6x6 và gạch chân tiếng Anh
            const int jpegQuality = 78;

            int maxDim = Math.Max(skBitmap.Width, skBitmap.Height);
            if (maxDim > capDim)
            {
                float scale = (float)capDim / maxDim;
                int targetW = Math.Max(1, (int)(skBitmap.Width * scale));
                int targetH = Math.Max(1, (int)(skBitmap.Height * scale));
                using var resized = skBitmap.Resize(new SKImageInfo(targetW, targetH), SKSamplingOptions.Default);
                if (resized != null)
                {
                    using var resizedMs = new MemoryStream();
                    resized.Encode(resizedMs, SKEncodedImageFormat.Jpeg, jpegQuality);
                    return Convert.ToBase64String(resizedMs.ToArray());
                }
            }

            using var ms = new MemoryStream();
            skBitmap.Encode(ms, SKEncodedImageFormat.Jpeg, jpegQuality);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static string BuildExamBatchVisionPrompt(string fileName, List<int> pageNumbers, int totalPages)
    {
        string pageRange = pageNumbers.Count == 1
            ? $"Trang {pageNumbers[0]}"
            : $"Trang {pageNumbers[0]} đến Trang {pageNumbers[^1]}";

        return $@"BẠN LÀ CHUYÊN GIA SỐ HÓA ĐỀ THI & GIẢI ĐỀ THI ĐGNL / TN THPT VIỆT NAM ĐỘ CHÍNH XÁC CAO.
Bạn đang xem ảnh chụp của **{pageRange}** (trên tổng {totalPages} trang) từ tệp đề thi '{fileName}'.

NHIỆM VỤ BẮT BUỘC:
1. BÓC TÁCH ĐẦY ĐỦ 100% MỌI CÂU HỎI VÀ ĐOẠN VĂN / NGỮ LIỆU DÙNG CHUNG XUẤT HIỆN TRÊN {pageRange}:
   - Chép nguyên văn chính xác từng con số, dấu cộng trừ, hệ số, số mũ, biến số và đủ 4 phương án A, B, C, D.
   - Ghi đúng `question_number` (số câu in trên đề) và `page_number` (số trang chứa câu đó).
   - Nếu trang chỉ là trang bìa hoặc không có câu hỏi/đoạn văn nào, trả về `passages: []` và `single_questions: []`.

2. CHUẨN HÓA 100% CÔNG THỨC TOÁN / LÝ / HÓA / SINH SANG KATEX (`$...$` HOẶC `$$...$$`):
   - Mọi công thức, phương trình, căn thức, phân số, logarit, tích phân, giới hạn, hàm số từng khúc, khoảng/đoạn, hệ bất phương trình, ký hiệu biến số ĐỀU BẮT BUỘC viết bằng KaTeX chuẩn:
     + Căn thức: `$\sqrt{{-x^2 + mx}} = 2x - 1$`
     + Phân số & Logarit: `$\log_{{24}} 15 = \frac{{ma + ab}}{{n + ab}}$`
     + Hàm từng khúc / Hệ PT: `$$f(x) = \begin{{cases}} \frac{{x^2 - 4}}{{x - 2}}, & x < 2 \\ m + 1, & x = 2 \end{{cases}}$$`
     + Lượng giác / Tích phân / Giới hạn: `$\cos 2x + 3\sin x - 2 = 0$`, `$\int_0^2 f(x)\,dx = 7$`, `$\lim_{{n \to +\infty}} \frac{{5n - 3}}{{n + 2}}$`, `$(-\infty; \frac{{1}}{{2}}]$`
     + Ký hiệu tam giác biến thiên là `$\Delta$`, đơn vị mũ âm là `$\text{{mol}}\cdot\text{{l}}^{{-1}}\cdot\text{{phút}}^{{-1}}$`.
   - Trong JSON, mọi dấu gạch chéo ngược LaTeX PHẢI được escape chuẩn (`\\frac`, `\\sqrt`, `\\cases`, `\\int`, `\\lim`, `\\sin`, `\\cos`, `\\pi`, `\\infty`, `\\le`, `\\ge`).

3. BẢNG SỐ LIỆU, BIỂU ĐỒ, SƠ ĐỒ MẠNG LƯỚI & HÌNH HỌC:
   - **Bảng số liệu** (VD: bảng khoảng cách 6x6 giữa các thị trấn A–F, bảng số liệu thí nghiệm Lý/Hóa/Sinh/Địa): BẮT BUỘC xuất ĐẦY ĐỦ 100% tất cả hàng và cột dưới dạng Markdown Table chuẩn (`| ... |`) hoặc HTML `<table>` có `rowspan`/`colspan` nếu bảng có tiêu đề 2 tầng. KHÔNG ĐƯỢC bỏ sót bảng!
   - **Sơ đồ đồ thị mạng lưới có trọng số / khoảng cách / chi phí** (VD: sơ đồ nối cáp giữa các thị trấn A, B, C, D, E, F kèm chi phí từng tuyến): BẮT BUỘC liệt kê đầy đủ mọi cạnh nối và trọng số dưới dạng bảng Markdown (`| Tuyến nối | Chi phí |`) ngay trong `content` của câu hỏi để không mất bất kỳ dữ liệu nào!
   - **Biểu đồ cột / tròn**: Ghi rõ loại biểu đồ kèm đầy đủ tên nhãn và số liệu `%` (VD: `*([Hình vẽ]: Biểu đồ tròn thể hiện cơ cấu: A 22%, B 26%...)*`) để hệ thống tự dựng biểu đồ tương tác.
   - **Bảng biến thiên / Đồ thị hàm số / Hình học không gian 3D**: Khi đề có bảng biến thiên hoặc đồ thị, ưu tiên dùng thẻ `[VARIATION_TABLE]...[/VARIATION_TABLE]` hoặc `[MATH_GRAPH]...[/MATH_GRAPH]` nếu phù hợp.

4. CÂU HỎI TIẾNG ANH GẠCH CHÂN TÌM LỖI SAI & ĐIỀN TỪ:
   - Với câu điền từ có chỗ trống (VD: `____`), giữ nguyên dấu gạch ngang chỗ trống `______` đúng vị trí trong câu, KHÔNG đẩy xuống cuối câu!
   - Với câu tìm lỗi sai có từ/cụm từ gạch chân A, B, C, D: bọc chính xác phần gạch chân bằng thẻ `<u>...</u>` kèm chữ cái tương ứng, ví dụ: `Mathematics, <u>which</u> (A) many students believe <u>is</u> (B) too difficult, actually <u>require</u> (C) logical thinking <u>rather than</u> (D) memorization.` và điền `options` tương ứng (ví dụ `A: ""which""`, `B: ""is""`, `C: ""require""`, `D: ""rather than""`).

5. QUAN TRỌNG NHẤT — AI TỰ GIẢI CHÍNH XÁC ĐỂ CHỌN ĐÁP ÁN ĐÚNG (`correct_option`), VIẾT LỜI GIẢI (`explanation`) VÀ PHÂN BẬC BLOOM (`difficulty_level`):
   - **TUYỆT ĐỐI BỎ QUA** mọi vết khoanh tay / bút mực đỏ, xanh, chì của học sinh in trên ảnh đề (vì đó có thể là bài làm SAI!).
   - Bạn PHẢI **TỰ GIẢI CHÍNH XÁC** từng câu hỏi bằng kiến thức chuyên gia của môn học tương ứng (Toán, Ngữ văn, Tiếng Anh, Vật lí, Hóa học, Sinh học, Lịch sử, Địa lí, Tư duy Logic) để xác định `correct_option` (`""A""`, `""B""`, `""C""`, hoặc `""D""`).
   - Ghi rõ `explanation`: lời giải ngắn gọn, chính xác từng bước kèm công thức KaTeX giải thích rõ tại sao chọn `correct_option`.
   - Gán chính xác `difficulty_level` từ 1 đến 6 theo Bloom Taxonomy (`1`: Nhận biết, `2`: Thông hiểu, `3`: Vận dụng, `4`: Phân tích, `5`: Đánh giá, `6`: Sáng tạo).
   - Gán `suggested_skill_name` ngắn gọn, ĐÚNG MÔN HỌC và CHUYÊN ĐỀ (VD: `""Tiếng Anh · Thì quá khứ hoàn thành""`, `""Toán · Phương trình chứa căn thức""`, `""Toán · Tích phân & Diện tích hình phẳng""`, `""Toán rời rạc · Cây khung nhỏ nhất""`, `""Hóa học · Ester - Lipid""`).

6. CHÙM ĐỌC HIỂU / NGỮ LIỆU DÙNG CHUNG (`passages`):
   - Đoạn văn / dữ liệu dùng chung cho nhiều câu (VD: `""Dựa vào thông tin dưới đây để trả lời các câu từ 73 đến 74""` hoặc `""Questions 53-60: Read the passage carefully.""`) đưa vào `passages[]` với đúng `start_question`, `end_question` và nội dung đầy đủ `content`.
   - Nếu đoạn văn bắt đầu ở trang này nhưng một phần câu hỏi nằm ở trang sau (ngoài cụm trang hiện tại), VẪN xuất passage vào `passages[]` kèm `start_question`, `end_question` (các câu có trong cụm trang hiện tại đưa vào `questions`, các câu trang sau sẽ tự động được ghép ở cụm kế tiếp).

7. ĐỊNH DẠNG JSON BẮT BUỘC (CHỈ TRẢ VỀ DUY NHẤT 1 JSON OBJECT HỢP LỆ, KHÔNG GIẢI THÍCH THỪA):
{{
  ""passages"": [
    {{
      ""start_question"": 73,
      ""end_question"": 74,
      ""content"": ""Nội dung đoạn văn / bảng số liệu Markdown / công thức KaTeX..."",
      ""questions"": [
        {{
          ""question_number"": 73,
          ""page_number"": {pageNumbers[0]},
          ""content"": ""Nội dung câu hỏi đầy đủ công thức KaTeX ($...$)..."",
          ""options"": {{ ""A"": ""..."", ""B"": ""..."", ""C"": ""..."", ""D"": ""..."" }},
          ""correct_option"": ""A"",
          ""explanation"": ""Lời giải chi tiết từng bước kèm KaTeX..."",
          ""difficulty_level"": 3,
          ""suggested_skill_name"": ""Toán · Hệ bất phương trình bậc nhất hai ẩn""
        }}
      ]
    }}
  ],
  ""single_questions"": [
    {{
      ""question_number"": 62,
      ""page_number"": {pageNumbers[0]},
      ""content"": ""Cho phương trình $\\sqrt{{-x^2 + mx}} = 2x - 1$ với $m$ là tham số thực..."",
      ""options"": {{ ""A"": ""$(-\\infty; \\frac{{1}}{{2}}]$"", ""B"": ""$(\\frac{{1}}{{2}}; 2]$"", ""C"": ""$(-\\infty; \\frac{{1}}{{2}})$"", ""D"": ""$(-\\infty; 2)$"" }},
      ""correct_option"": ""C"",
      ""explanation"": ""Điều kiện $x \\ge \\frac{{1}}{{2}}$... Suy ra phương trình vô nghiệm khi $m < \\frac{{1}}{{2}}$."",
      ""difficulty_level"": 3,
      ""suggested_skill_name"": ""Toán · Phương trình chứa căn thức""
    }}
  ]
}}";
    }

    private static string? ExtractTextPartsIgnoringThoughts(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            var root = JsonNode.Parse(responseBody);
            var parts = root?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
            if (parts == null || parts.Count == 0) return null;

            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (part?["thought"]?.GetValue<bool>() == true) continue;
                string? txt = part?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine(txt);
                }
            }
            return sb.ToString().Trim();
        }
        catch
        {
            return null;
        }
    }

    private static ParsedExamDto? TryDeserializeExamJson(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        string cleaned = rawText.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNl = cleaned.IndexOf('\n');
            if (firstNl > 0) cleaned = cleaned[(firstNl + 1)..];
            if (cleaned.EndsWith("```")) cleaned = cleaned[..^3].Trim();
        }

        int firstBrace = cleaned.IndexOf('{');
        int lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            cleaned = cleaned[firstBrace..(lastBrace + 1)];
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        try
        {
            return JsonSerializer.Deserialize<ParsedExamDto>(cleaned, jsonOptions);
        }
        catch
        {
            try
            {
                string sanitized = SanitizeJsonForLatex(cleaned);
                return JsonSerializer.Deserialize<ParsedExamDto>(sanitized, jsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Hợp nhất các cụm trang đã bóc tách thành một đề thi hoàn chỉnh, tự động ghép chùm đọc hiểu liên trang
    /// và khử trùng lặp câu hỏi ưu tiên bản giàu nội dung/lời giải hơn.
    /// </summary>
    public static ParsedExamDto MergeBatchExamResults(
        IEnumerable<ParsedExamDto> batchResults,
        string fileName,
        int totalPages)
    {
        var mergedPassages = new List<ParsedPassageDto>();
        var questionsByNumber = new Dictionary<int, ParsedQuestionDto>();

        void UpsertQuestion(ParsedQuestionDto q)
        {
            if (q == null || q.QuestionNumber <= 0) return;
            q.CorrectOption = NormalizeCorrectOption(q.CorrectOption);
            q.DifficultyLevel = Math.Clamp(q.DifficultyLevel <= 0 ? 2 : q.DifficultyLevel, 1, 6);
            q.Options ??= new Dictionary<string, string>();
            foreach (var k in new[] { "A", "B", "C", "D" })
            {
                if (!q.Options.ContainsKey(k)) q.Options[k] = string.Empty;
            }

            if (!questionsByNumber.TryGetValue(q.QuestionNumber, out var existing))
            {
                questionsByNumber[q.QuestionNumber] = q;
                return;
            }

            // Ưu tiên bản có lời giải chi tiết hơn hoặc nội dung đầy đủ hơn
            int existingScore = ScoreQuestionQuality(existing);
            int candidateScore = ScoreQuestionQuality(q);
            if (candidateScore > existingScore)
            {
                questionsByNumber[q.QuestionNumber] = q;
            }
        }

        foreach (var batch in batchResults)
        {
            if (batch == null) continue;

            foreach (var p in batch.Passages ?? Enumerable.Empty<ParsedPassageDto>())
            {
                if (p == null) continue;
                foreach (var q in p.Questions ?? Enumerable.Empty<ParsedQuestionDto>())
                {
                    UpsertQuestion(q);
                }

                if (p.StartQuestion > 0 && p.EndQuestion >= p.StartQuestion)
                {
                    var existingPassage = mergedPassages.FirstOrDefault(ep =>
                        ep.StartQuestion == p.StartQuestion && ep.EndQuestion == p.EndQuestion);

                    if (existingPassage == null)
                    {
                        mergedPassages.Add(new ParsedPassageDto
                        {
                            StartQuestion = p.StartQuestion,
                            EndQuestion = p.EndQuestion,
                            Content = p.Content?.Trim() ?? string.Empty,
                            ImageUrl = p.ImageUrl,
                            Questions = new List<ParsedQuestionDto>()
                        });
                    }
                    else
                    {
                        string newContent = p.Content?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(newContent))
                        {
                            if (string.IsNullOrWhiteSpace(existingPassage.Content))
                            {
                                existingPassage.Content = newContent;
                            }
                            else if (!existingPassage.Content.Contains(newContent, StringComparison.OrdinalIgnoreCase) &&
                                     !newContent.Contains(existingPassage.Content, StringComparison.OrdinalIgnoreCase))
                            {
                                existingPassage.Content = $"{existingPassage.Content}\n\n{newContent}";
                            }
                            else if (newContent.Length > existingPassage.Content.Length)
                            {
                                existingPassage.Content = newContent;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(existingPassage.ImageUrl) && !string.IsNullOrWhiteSpace(p.ImageUrl))
                        {
                            existingPassage.ImageUrl = p.ImageUrl;
                        }
                    }
                }
            }

            foreach (var q in batch.SingleQuestions ?? Enumerable.Empty<ParsedQuestionDto>())
            {
                UpsertQuestion(q);
            }
        }

        mergedPassages = mergedPassages
            .OrderBy(p => p.StartQuestion)
            .ThenBy(p => p.EndQuestion)
            .ToList();

        var singleQuestions = new List<ParsedQuestionDto>();
        foreach (var q in questionsByNumber.Values.OrderBy(x => x.QuestionNumber))
        {
            var parentPassage = mergedPassages.FirstOrDefault(p =>
                q.QuestionNumber >= p.StartQuestion && q.QuestionNumber <= p.EndQuestion);

            if (parentPassage != null)
            {
                parentPassage.Questions.Add(q);
            }
            else
            {
                singleQuestions.Add(q);
            }
        }

        foreach (var p in mergedPassages)
        {
            p.Questions = p.Questions.OrderBy(q => q.QuestionNumber).ToList();
        }

        var merged = new ParsedExamDto
        {
            Format = "V-ACT Exam",
            FileName = fileName,
            Passages = mergedPassages,
            SingleQuestions = singleQuestions,
            TotalPassages = mergedPassages.Count,
            TotalSingleQuestions = singleQuestions.Count
        };
        merged.TotalPages = Math.Max(totalPages, GetMaxPageNumber(merged));
        return merged;
    }

    private static int ScoreQuestionQuality(ParsedQuestionDto q)
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(q.Content)) score += Math.Min(q.Content.Length, 400);
        if (!string.IsNullOrWhiteSpace(q.Explanation)) score += 300;
        if (q.Options != null)
        {
            score += q.Options.Values.Count(v => !string.IsNullOrWhiteSpace(v)) * 60;
        }
        return score;
    }

    private static int GetMaxPageNumber(ParsedExamDto dto)
    {
        int maxPage = 1;
        foreach (var q in dto.SingleQuestions)
            if (q.PageNumber > maxPage) maxPage = q.PageNumber;
        foreach (var p in dto.Passages)
            foreach (var q in p.Questions)
                if (q.PageNumber > maxPage) maxPage = q.PageNumber;
        return maxPage;
    }

    public static string NormalizeCorrectOption(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "A";
        string trimmed = raw.Trim().ToUpperInvariant();
        if (trimmed is "A" or "B" or "C" or "D") return trimmed;
        if (trimmed is "0") return "A";
        if (trimmed is "1") return "B";
        if (trimmed is "2") return "C";
        if (trimmed is "3") return "D";

        var m = Regex.Match(trimmed, @"\b([A-D])\b");
        if (m.Success) return m.Groups[1].Value;

        var lead = Regex.Match(trimmed, @"^[\(\[]?([A-D])[\)\]\.:]?");
        if (lead.Success) return lead.Groups[1].Value;

        return "A";
    }

    /// <summary>
    /// Bộ bóc tách dự phòng cục bộ (Local Fallback Parser) khi cả OpenAI và Gemini bị lỗi 503/quá tải.
    /// Thử chạy script Python exam_parser.py nếu môi trường có sẵn pymupdf; nếu thiếu pymupdf hoặc lỗi Python,
    /// tự động chuyển sang bộ bóc tách thuần C# PdfPigLocalExamParser (không cần Python/pymupdf).
    /// </summary>
    private async Task<ParsedExamDto> RunLocalFallbackParserAsync(
        byte[] pdfBytes, 
        string fileName, 
        List<ExtractedPdfImage> localImages, 
        string webRootPath, 
        string examId, 
        CancellationToken cancellationToken)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        try
        {
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
            if (scriptPath != null)
            {
                try
                {
                    await File.WriteAllBytesAsync(tempFile, pdfBytes, cancellationToken);
                    string pythonExe = ResolvePythonExecutable();
                    _logger.LogInformation("Thử chạy Local Python Fallback Parser '{ScriptPath}' bằng '{PythonExe}'...", scriptPath, pythonExe);

                    using var process = new System.Diagnostics.Process();
                    process.StartInfo.FileName = pythonExe;
                    process.StartInfo.Arguments = $"\"{scriptPath}\" \"{tempFile}\"";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;

                    process.Start();
                    string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                    string err = await process.StandardError.ReadToEndAsync(cancellationToken);
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

                        _logger.LogInformation("Local Python Fallback Parser hoàn tất: {Questions} câu hỏi đơn, {Passages} chùm câu hỏi.",
                            localResult.TotalSingleQuestions, localResult.TotalPassages);

                        if (localImages.Count > 0)
                        {
                            _imageExtractor.MapImagesToExam(localResult, localImages, webRootPath, examId);
                        }

                        return localResult;
                    }

                    _logger.LogWarning("Local Python Fallback Parser không khả dụng (Exit={Code}, Err={Err}). Tự động chuyển sang C# PdfPigLocalExamParser...",
                        process.ExitCode, err.Length > 240 ? err[..240] : err);
                }
                catch (Exception pyEx) when (pyEx is not OperationCanceledException)
                {
                    _logger.LogWarning(pyEx, "Không thể chạy Python exam_parser.py. Tự động chuyển sang C# PdfPigLocalExamParser...");
                }
            }

            // Tầng dự phòng thuần C# (100% không phụ thuộc Python hay pymupdf)
            _logger.LogInformation("Đang bóc tách đề thi cục bộ bằng C# PdfPigLocalExamParser cho '{FileName}'...", fileName);
            var pdfPigResult = PdfPigLocalExamParser.Parse(pdfBytes, fileName);
            CleanExamDto(pdfPigResult);

            if (localImages.Count > 0)
            {
                _imageExtractor.MapImagesToExam(pdfPigResult, localImages, webRootPath, examId);
            }

            int totalParsed = pdfPigResult.SingleQuestions.Count + pdfPigResult.Passages.Sum(p => p.Questions.Count);
            _logger.LogInformation("C# PdfPigLocalExamParser hoàn tất: {Total} câu hỏi ({Passages} chùm, {Singles} câu đơn) trên {Pages} trang.",
                totalParsed, pdfPigResult.TotalPassages, pdfPigResult.TotalSingleQuestions, pdfPigResult.TotalPages);

            return pdfPigResult;
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private string ResolvePythonExecutable()
    {
        var configured = _configuration["AiSettings:PythonExecutable"] ?? Environment.GetEnvironmentVariable("PYTHON_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        string[] venvCandidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "..", "rag-service", ".venv", "Scripts", "python.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "rag-service", ".venv", "Scripts", "python.exe")
            }
            : new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "..", "rag-service", ".venv", "bin", "python"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "rag-service", ".venv", "bin", "python")
            };

        var foundVenv = venvCandidates.FirstOrDefault(File.Exists);
        if (foundVenv != null) return foundVenv;

        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    private static string SanitizeJsonForLatex(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        // 1. Khôi phục các ký tự điều khiển ASCII bị nuốt do unescaped backslashes (\f, \t, \b, \r, \n)
        var result = json.Replace("\x0crac", "\\\\frac")
                         .Replace("\x0corall", "\\\\forall")
                         .Replace("\x09imes", "\\\\times")
                         .Replace("\x09ext", "\\\\text")
                         .Replace("\x09an", "\\\\tan")
                         .Replace("\x09heta", "\\\\theta")
                         .Replace("\x09au", "\\\\tau")
                         .Replace("\x09o ", "\\\\to ")
                         .Replace("\x08ar", "\\\\bar")
                         .Replace("\x08egin", "\\\\begin")
                         .Replace("\x08eta", "\\\\beta");

        // 2. Chuyển các lệnh LaTeX unescaped thành \\... để hợp lệ trong JSON
        result = Regex.Replace(
            result,
            @"(?<!\\)\\(frac|times|text|Delta|bar|begin|end|beta|theta|tau|rho|right|left|le|ge|leq|geq|neq|approx|cdot|mu|omega|alpha|sigma|pi|sqrt|sin|cos|tan|cot|log|ln|int|lim|sum|prod|infty|mathbb|mathcal|mathrm|in|notin|forall|exists|pm|mp|vec|overrightarrow|widehat|overline|underline|cup|cap|subset|emptyset|quad|qquad|to|Rightarrow|Leftrightarrow|implies|cases)(?=[^a-zA-Z]|$)",
            "\\\\$1");

        // 3. Sửa lỗi tam giác Delta bị nhầm thành ar{ ext{A}}
        result = Regex.Replace(result, @"ar\{\s*ext\{A\}\}", "\\\\Delta");
        result = Regex.Replace(result, @"\\bar\{\\text\{A\}\}", "\\\\Delta");
        result = Regex.Replace(result, @"\\bar\{A\}", "\\\\Delta");
        result = result.Replace("∆", "\\\\Delta");

        return result;
    }

    private static string CleanLatexString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        var s = text;
        s = s.Replace("\x0crac", "\\frac")
             .Replace("\x0corall", "\\forall")
             .Replace("\x09imes", "\\times")
             .Replace("\x09ext", "\\text")
             .Replace("\x09an", "\\tan")
             .Replace("\x09heta", "\\theta")
             .Replace("\x09au", "\\tau")
             .Replace("\x08ar", "\\bar")
             .Replace("\x08egin", "\\begin")
             .Replace("\x08eta", "\\beta");

        s = Regex.Replace(s, @"ar\{\s*ext\{A\}\}", "\\Delta");
        s = Regex.Replace(s, @"\\bar\{\\text\{A\}\}", "\\Delta");
        s = Regex.Replace(s, @"\\bar\{A\}", "\\Delta");
        s = s.Replace("∆", "\\Delta");

        return s;
    }

    private static void CleanExamDto(ParsedExamDto exam)
    {
        if (exam == null) return;

        void CleanQuestion(ParsedQuestionDto q)
        {
            q.Content = CleanLatexString(q.Content);
            q.Explanation = CleanLatexString(q.Explanation);
            q.CorrectOption = NormalizeCorrectOption(q.CorrectOption);
            q.DifficultyLevel = Math.Clamp(q.DifficultyLevel <= 0 ? 2 : q.DifficultyLevel, 1, 6);
            if (q.Options != null)
            {
                var keys = q.Options.Keys.ToList();
                foreach (var k in keys) q.Options[k] = CleanLatexString(q.Options[k]);
            }
        }

        foreach (var p in exam.Passages)
        {
            p.Content = CleanLatexString(p.Content);
            foreach (var q in p.Questions) CleanQuestion(q);
        }
        foreach (var q in exam.SingleQuestions) CleanQuestion(q);
    }

    private string GetWebRootPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var wwwroot = Path.Combine(currentDir, "wwwroot");
        if (Directory.Exists(wwwroot)) return wwwroot;

        var apiWwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(apiWwwroot)) return apiWwwroot;

        Directory.CreateDirectory(wwwroot);
        return wwwroot;
    }

    // =========================================================================
    // PHÂN LOẠI CÂU HỎI ĐỀ THI VÀO CÂY KHUNG NĂNG LỰC & KHUNG CHƯƠNG TRÌNH
    // =========================================================================

    public async Task<ClassifyExamNodesResponseDto> ClassifyQuestionsToNodesAsync(
        ClassifyExamNodesRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var response = new ClassifyExamNodesResponseDto();
        if (request?.Questions == null || request.Questions.Count == 0 ||
            request.TaxonomyNodes == null || request.TaxonomyNodes.Count == 0)
        {
            return response;
        }

        var nodes = request.TaxonomyNodes;
        var apiKeys = ResolveApiKeys();
        var geminiModels = ResolveGeminiModels();

        var mappedByQNum = new Dictionary<int, ExamQuestionNodeMappingDto>();
        bool usedGemini = false;

        if (apiKeys.Count > 0 && geminiModels.Count > 0)
        {
            // Nén danh mục nút cây chương trình dạng chỉ mục [idx] để tối ưu token
            var catalogBuilder = new StringBuilder();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                catalogBuilder.Append('[').Append(i).Append("] ");
                catalogBuilder.Append(n.DomainName).Append(" | ");
                catalogBuilder.Append(n.ChapterName);
                if (!string.IsNullOrWhiteSpace(n.LessonName))
                {
                    catalogBuilder.Append(" > ").Append(n.LessonName);
                }
                if (!string.IsNullOrWhiteSpace(n.PatternName) &&
                    !string.Equals(n.PatternName, n.LessonName, StringComparison.OrdinalIgnoreCase))
                {
                    catalogBuilder.Append(" > ").Append(n.PatternName);
                }
                catalogBuilder.AppendLine();
            }
            string catalogText = catalogBuilder.ToString();

            const int batchSize = 30;
            for (int offset = 0; offset < request.Questions.Count; offset += batchSize)
            {
                var batch = request.Questions.Skip(offset).Take(batchSize).ToList();
                var batchMappings = await TryClassifyBatchWithGeminiAsync(
                    batch,
                    nodes,
                    catalogText,
                    request.SubjectCode,
                    request.ExamTitle,
                    apiKeys,
                    geminiModels,
                    cancellationToken);

                if (batchMappings != null && batchMappings.Count > 0)
                {
                    usedGemini = true;
                    foreach (var m in batchMappings)
                    {
                        mappedByQNum[m.QuestionNumber] = m;
                    }
                }
            }
        }

        // Bù đắp bằng bộ chấm điểm ngữ nghĩa cục bộ cho mọi câu chưa được AI ánh xạ (hoặc khi offline)
        foreach (var q in request.Questions)
        {
            if (mappedByQNum.TryGetValue(q.QuestionNumber, out var aiMapped))
            {
                response.Mappings.Add(aiMapped);
            }
            else
            {
                var fallback = ClassifyQuestionLexically(q, nodes, request.SubjectCode);
                if (fallback != null)
                {
                    response.Mappings.Add(fallback);
                }
            }
        }

        response.ClassifiedCount = response.Mappings.Count;
        response.Provider = usedGemini ? "Gemini" : "LexicalFallback";
        return response;
    }

    private async Task<List<ExamQuestionNodeMappingDto>?> TryClassifyBatchWithGeminiAsync(
        List<ExamQuestionClassifyInputDto> batch,
        List<ExamTaxonomyNodeRefDto> nodes,
        string catalogText,
        string? subjectCode,
        string? examTitle,
        List<string> apiKeys,
        List<string> geminiModels,
        CancellationToken cancellationToken)
    {
        var qBuilder = new StringBuilder();
        foreach (var q in batch)
        {
            string snippet = (q.Content ?? string.Empty).Trim();
            if (snippet.Length > 420) snippet = snippet[..420] + "...";
            qBuilder.Append("Câu ").Append(q.QuestionNumber).Append(": ").Append(snippet);
            if (!string.IsNullOrWhiteSpace(q.SuggestedSkillName))
            {
                qBuilder.Append(" | Gợi ý kỹ năng: ").Append(q.SuggestedSkillName);
            }
            if (!string.IsNullOrWhiteSpace(q.PassageExcerpt))
            {
                string passSnippet = q.PassageExcerpt.Trim();
                if (passSnippet.Length > 260) passSnippet = passSnippet[..260] + "...";
                qBuilder.Append(" | Ngữ cảnh chùm bài: ").Append(passSnippet);
            }
            qBuilder.AppendLine();
        }

        string prompt = $@"Bạn là chuyên gia khảo thí ĐGNL ĐHQG-HCM và chương trình GDPT 2018.
Nhiệm vụ: Ánh xạ từng câu hỏi đề thi bên dưới vào ĐÚNG 1 chỉ mục `[node_index]` phù hợp nhất trong DANH MỤC CÂY CHƯƠNG TRÌNH (Miền | Chương > Bài > Dạng bài).

THÔNG TIN ĐỀ THI:
- Tên đề: {examTitle ?? "Đề thi tổng hợp"}
- Mã môn chính: {subjectCode ?? "GENERAL"}

DANH MỤC CÂY CHƯƠNG TRÌNH ({nodes.Count} nút):
{catalogText}

DANH SÁCH CÂU HỎI CẦN PHÂN LOẠI:
{qBuilder}

QUY TẮC BẮT BUỘC:
1. Chỉ chọn `node_index` hợp lệ từ 0 đến {nodes.Count - 1}.
2. Đọc kỹ nội dung câu hỏi và ngữ cảnh chùm bài để phân biệt chính xác môn học (Toán, Vật lí, Hóa học, Sinh học, Ngữ văn/Tiếng Việt, Tiếng Anh, Lịch sử, Địa lí, GDKT&PL, Logic).
3. `confidence` từ 0.0 đến 1.0 phản ánh mức độ chắc chắn.
4. `bloom_level` từ 1 đến 6 (1=Nhớ, 2=Hiểu, 3=Vận dụng, 4=Phân tích, 5=Đánh giá, 6=Sáng tạo).
5. `reason` ngắn gọn tối đa 15 từ tiếng Việt giải thích vì sao thuộc Chương/Bài này.";

        var genConfig = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["temperature"] = 0.0,
            ["maxOutputTokens"] = 8192
        };

        var payloadObj = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = prompt + "\n\nChỉ trả về JSON thuần đúng định dạng: { \"mappings\": [ { \"question_number\": 1, \"node_index\": 0, \"confidence\": 0.92, \"bloom_level\": 2, \"reason\": \"...\" } ] }" }
                    }
                }
            },
            ["generationConfig"] = genConfig
        };

        string? configuredGeminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"];
        var rotatedKeys = GetRotatedKeys(apiKeys);
        string payloadStr = payloadObj.ToJsonString();

        foreach (var modelName in geminiModels)
        {
            foreach (var apiKey in rotatedKeys)
            {
                string requestUrl = ResolveGeminiEndpointUrl(modelName, apiKey, configuredGeminiBaseUrl);
                try
                {
                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                    {
                        Content = new StringContent(payloadStr, Encoding.UTF8, "application/json")
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var httpRes = await _httpClient.SendAsync(requestMessage, linked.Token);
                    if (!httpRes.IsSuccessStatusCode)
                    {
                        int statusCode = (int)httpRes.StatusCode;
                        _logger.LogWarning("ClassifyNodes Gemini {Model} trả về HTTP {Status}", modelName, statusCode);
                        if (statusCode is 503 or 500 or 504 or 404 or 400) break;
                        continue;
                    }

                    string body = await httpRes.Content.ReadAsStringAsync(cancellationToken);
                    var text = ExtractTextPartsIgnoringThoughts(body);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    using var resDoc = JsonDocument.Parse(text);
                    if (!resDoc.RootElement.TryGetProperty("mappings", out var mappingsEl) || mappingsEl.ValueKind != JsonValueKind.Array)
                        continue;

                    var results = new List<ExamQuestionNodeMappingDto>();
                    foreach (var item in mappingsEl.EnumerateArray())
                    {
                        int qNum = item.TryGetProperty("question_number", out var qnEl) ? qnEl.GetInt32() : 0;
                        int nodeIdx = item.TryGetProperty("node_index", out var niEl) ? niEl.GetInt32() : -1;
                        if (qNum <= 0 || nodeIdx < 0 || nodeIdx >= nodes.Count) continue;

                        double conf = item.TryGetProperty("confidence", out var cEl) ? cEl.GetDouble() : 0.85;
                        int? bloom = item.TryGetProperty("bloom_level", out var bEl) && bEl.ValueKind == JsonValueKind.Number
                            ? Math.Clamp(bEl.GetInt32(), 1, 6)
                            : null;
                        string? reason = item.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String
                            ? rEl.GetString()
                            : null;

                        var node = nodes[nodeIdx];
                        results.Add(new ExamQuestionNodeMappingDto
                        {
                            QuestionNumber = qNum,
                            SkillId = node.SkillId,
                            DomainId = node.DomainId,
                            DomainName = node.DomainName,
                            ChapterId = node.ChapterId,
                            ChapterName = node.ChapterName,
                            LessonId = node.LessonId,
                            LessonName = node.LessonName,
                            PatternId = node.PatternId,
                            PatternName = node.PatternName,
                            Confidence = Math.Clamp(Math.Round(conf, 2), 0.0, 1.0),
                            BloomLevel = bloom,
                            Reason = reason
                        });
                    }

                    if (results.Count > 0) return results;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ClassifyNodes lỗi khi gọi Gemini model {Model}, thử model kế tiếp...", modelName);
                }
            }
        }

        return null;
    }

    public static ExamQuestionNodeMappingDto? ClassifyQuestionLexically(
        ExamQuestionClassifyInputDto question,
        List<ExamTaxonomyNodeRefDto> nodes,
        string? subjectCode = null)
    {
        if (question == null || nodes == null || nodes.Count == 0) return null;

        string skillNorm = NormalizeVietnameseFold(question.SuggestedSkillName);
        string contentNorm = NormalizeVietnameseFold(question.Content);
        string passageNorm = NormalizeVietnameseFold(question.PassageExcerpt);

        var skillTokens = ExtractMeaningfulTokens(skillNorm);
        var contentTokens = ExtractMeaningfulTokens(contentNorm);
        var passageTokens = ExtractMeaningfulTokens(passageNorm);

        ExamTaxonomyNodeRefDto? bestNode = null;
        double bestScore = -1.0;

        foreach (var node in nodes)
        {
            string patternNorm = NormalizeVietnameseFold(node.PatternName);
            string lessonNorm = NormalizeVietnameseFold(node.LessonName);
            string chapterNorm = NormalizeVietnameseFold(node.ChapterName);
            string domainNorm = NormalizeVietnameseFold(node.DomainName);

            var nodeTokens = ExtractMeaningfulTokens($"{patternNorm} {lessonNorm} {chapterNorm}");
            if (nodeTokens.Count == 0) continue;

            double score = 0.0;

            // Khớp cụm trực tiếp (boost mạnh)
            if (!string.IsNullOrEmpty(skillNorm) && skillNorm.Length >= 4)
            {
                if (patternNorm.Contains(skillNorm) || skillNorm.Contains(patternNorm) && patternNorm.Length >= 4)
                    score += 5.0;
                else if (lessonNorm.Contains(skillNorm) || (skillNorm.Contains(lessonNorm) && lessonNorm.Length >= 4))
                    score += 4.0;
                else if (chapterNorm.Contains(skillNorm))
                    score += 2.5;
            }

            // Chồng lấn từ khóa có nghĩa
            score += CountOverlap(skillTokens, nodeTokens) * 2.2;
            score += CountOverlap(contentTokens, nodeTokens) * 1.1;
            score += CountOverlap(passageTokens, nodeTokens) * 0.6;

            // Thưởng khớp mã môn của đề thi (nếu đề chuyên môn, không phải tổng hợp GENERAL)
            if (!string.IsNullOrWhiteSpace(subjectCode) &&
                !subjectCode.Equals("GENERAL", StringComparison.OrdinalIgnoreCase))
            {
                string upper = subjectCode.Trim().ToUpperInvariant();
                bool domainMatch = upper switch
                {
                    "MATH" => domainNorm.Contains("toan") || chapterNorm.Contains("toan"),
                    "PHYS" => chapterNorm.Contains("vat li") || chapterNorm.Contains("vat ly"),
                    "CHEM" => chapterNorm.Contains("hoa hoc"),
                    "BIOL" => chapterNorm.Contains("sinh hoc"),
                    "VIET" => domainNorm.Contains("tieng viet") || domainNorm.Contains("ngu van") || chapterNorm.Contains("ngu van"),
                    "ENGL" => domainNorm.Contains("tieng anh") || chapterNorm.Contains("tieng anh"),
                    "HIST" => chapterNorm.Contains("lich su"),
                    "GEO" => chapterNorm.Contains("dia li") || chapterNorm.Contains("dia ly"),
                    "LOGIC" => domainNorm.Contains("logic") || domainNorm.Contains("tu duy"),
                    _ => false
                };
                if (domainMatch) score += 1.5;
            }

            // Chuẩn hóa theo độ dài tránh ưu ái nhãn quá dài
            double normalizedScore = score / Math.Sqrt(Math.Max(nodeTokens.Count, 3));
            if (normalizedScore > bestScore)
            {
                bestScore = normalizedScore;
                bestNode = node;
            }
        }

        if (bestNode == null || bestScore <= 0.15) return null;

        double confidence = Math.Clamp(Math.Round(0.45 + Math.Min(bestScore * 0.14, 0.47), 2), 0.45, 0.92);
        return new ExamQuestionNodeMappingDto
        {
            QuestionNumber = question.QuestionNumber,
            SkillId = bestNode.SkillId,
            DomainId = bestNode.DomainId,
            DomainName = bestNode.DomainName,
            ChapterId = bestNode.ChapterId,
            ChapterName = bestNode.ChapterName,
            LessonId = bestNode.LessonId,
            LessonName = bestNode.LessonName,
            PatternId = bestNode.PatternId,
            PatternName = bestNode.PatternName,
            Confidence = confidence,
            Reason = "Khớp từ khóa học thuật với cây Khung chương trình"
        };
    }

    private static int CountOverlap(HashSet<string> source, HashSet<string> target)
    {
        int count = 0;
        foreach (var token in source)
        {
            if (target.Contains(token)) count++;
        }
        return count;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "cua", "va", "la", "cac", "cho", "trong", "khi", "sau", "day", "nao", "duoi",
        "theo", "voi", "mot", "nhung", "duoc", "khong", "co", "bang", "tu", "den",
        "chuong", "bai", "dang", "lop", "cau", "hoi", "tong", "hop", "trac", "nghiem"
    };

    private static HashSet<string> ExtractMeaningfulTokens(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized)) return set;
        foreach (Match m in Regex.Matches(normalized, @"[a-z0-9]{2,}"))
        {
            string tok = m.Value;
            if (!StopWords.Contains(tok))
            {
                set.Add(tok);
            }
        }
        return set;
    }

    private static string NormalizeVietnameseFold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string normalized = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                if (c == 'đ' || c == 'Đ') sb.Append('d');
                else sb.Append(char.ToLowerInvariant(c));
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}

