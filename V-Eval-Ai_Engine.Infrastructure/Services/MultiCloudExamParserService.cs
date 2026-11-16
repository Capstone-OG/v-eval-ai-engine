using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ bóc tách đề thi PDF đa đám mây (Multi-Cloud Cascade Failover: OpenAI GPT-4o Vision <-> Google Gemini Multimodal) trực tiếp từ C#
/// </summary>
public class MultiCloudExamParserService : IExamParserService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MultiCloudExamParserService> _logger;
    private readonly PdfImageExtractor _imageExtractor;
    private readonly IPromptTemplateService _promptTemplateService;

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
            "gemini-flash-lite-latest",
            "gemini-3.5-flash-lite",
            "gemini-3.1-flash-lite",
            "gemini-3.8-flash",
            "gemini-3.6-flash"
        };
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

    public async Task<ParsedExamDto> ParsePdfAsync(Stream pdfStream, string fileName, CancellationToken cancellationToken = default)
    {
        var apiKeys = ResolveApiKeys();
        var openAiKey = ResolveOpenAiKey();

        if (apiKeys.Count == 0 && string.IsNullOrEmpty(openAiKey))
        {
            throw new InvalidOperationException("Chưa cấu hình API Key hợp lệ cho cả OpenAI và Google Gemini trong appsettings.");
        }

        _logger.LogInformation("Bắt đầu đọc dữ liệu PDF '{FileName}' và chuẩn bị bóc tách (Gemini Keys: {KeyCount}, OpenAI: {HasOpenAi})...", 
            fileName, apiKeys.Count, !string.IsNullOrEmpty(openAiKey));

        // Nạp Prompt template từ hệ thống PromptTemplateService (Zero-Hardcode)
        string ocrPrompt = await _promptTemplateService.RenderPromptAsync("ExamIngestionVerbatimOcr.prompt.txt", cancellationToken: cancellationToken);

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

        string base64Data = Convert.ToBase64String(pdfBytes);
        _logger.LogInformation("Mã hóa PDF thành công ({SizeKb:F1} KB). Chuẩn bị gửi sang Vision AI...", pdfBytes.Length / 1024.0);

        // 1.5. Trích xuất toàn bộ ảnh gốc cục bộ từ PDF bằng PdfPig (100% Cục bộ trong 0.1s)
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
            _logger.LogWarning(ex, "Không thể trích xuất ảnh cục bộ từ PDF, tiếp tục luồng OCR...");
        }

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

        // 3. Chuẩn bị Request Parts cho Gemini Vision
        var requestParts = new JsonArray
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
                ["text"] = ocrPrompt
            }
        };

        var genConfig = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = schema,
            ["temperature"] = 0.0,
            ["maxOutputTokens"] = 65536
        };

        var requestPayload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = requestParts
                }
            },
            ["generationConfig"] = genConfig
        };

        int maxAttempts = _configuration.GetValue<int>("AiSettings:MaxAttempts", 5);
        if (maxAttempts <= 0) maxAttempts = 5;

        int minQuestionThreshold = _configuration.GetValue<int>("AiSettings:MinQuestionThreshold", 50);
        if (minQuestionThreshold <= 0) minQuestionThreshold = 50;

        int attemptCount = 0;
        string rawJsonText = string.Empty;
        string bestJsonText = string.Empty;
        int maxExtractedCount = 0;
        bool isSuccess = false;

        // BƯỚC 1: Kiểm tra OpenAI API Key (Ưu tiên GPT-4o / GPT-4o-mini nếu có key)
        if (!string.IsNullOrEmpty(openAiKey))
        {
            var openAiModels = ResolveOpenAiModels();
            _logger.LogInformation("Phát hiện OpenAI API Key [{MaskedKey}]. Tiến hành thử {Count} mô hình GPT theo thứ tự ưu tiên (giới hạn {MaxAttempts} lần thử)...", 
                MaskKey(openAiKey), openAiModels.Count, maxAttempts);

            foreach (var gptModel in openAiModels)
            {
                if (attemptCount >= maxAttempts)
                {
                    _logger.LogWarning("Đã đạt giới hạn tối đa {MaxAttempts} lần thử. Dừng gọi thêm mô hình.", maxAttempts);
                    break;
                }

                attemptCount++;
                _logger.LogInformation("[Lần thử {Attempt}/{MaxAttempts}] Đang gửi yêu cầu tới mô hình OpenAI '{ModelName}'...", 
                    attemptCount, maxAttempts, gptModel);

                try
                {
                    string? gptResult = await TryCallOpenAiAsync(gptModel, openAiKey, base64Data, ocrPrompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        gptResult = SanitizeJsonForLatex(gptResult);
                        try
                        {
                            var testParsed = JsonSerializer.Deserialize<ParsedExamDto>(gptResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            int totalQ = (testParsed?.SingleQuestions?.Count ?? 0) + (testParsed?.Passages?.Sum(p => p.Questions.Count) ?? 0);

                            if (totalQ > maxExtractedCount)
                            {
                                maxExtractedCount = totalQ;
                                bestJsonText = gptResult;
                            }

                            if (totalQ >= minQuestionThreshold)
                            {
                                _logger.LogInformation("Mô hình OpenAI '{ModelName}' trích xuất thành công xuất sắc {Total} câu hỏi (vượt ngưỡng {MinThreshold})!", gptModel, totalQ, minQuestionThreshold);
                                rawJsonText = gptResult;
                                isSuccess = true;
                                break;
                            }
                            else
                            {
                                _logger.LogWarning("Mô hình OpenAI '{ModelName}' chỉ trích xuất được {Total} câu. Thử lựa chọn kế tiếp...", gptModel, totalQ);
                            }
                        }
                        catch (Exception parseEx)
                        {
                            _logger.LogWarning("Không thể parse JSON từ OpenAI '{ModelName}': {Message}", gptModel, parseEx.Message);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Lỗi kết nối tới OpenAI '{ModelName}'. Thử phương án tiếp theo...", gptModel);
                }
            }
        }
        else
        {
            _logger.LogInformation("OpenAI API Key chưa được cấu hình hoặc là placeholder mặc định. Hệ thống tự động chuyển sang Google Gemini.");
        }

        // BƯỚC 2: Thử Google Gemini (Nếu GPT chưa thành công và số lần thử < maxAttempts)
        if (!isSuccess && attemptCount < maxAttempts && apiKeys.Count > 0)
        {
            var geminiModels = ResolveGeminiModels();
            _logger.LogInformation("Tiến hành chạy Google Gemini: {ModelCount} mô hình, {KeyCount} API Key dự phòng (còn lại {Remaining} lần thử)...", 
                geminiModels.Count, apiKeys.Count, maxAttempts - attemptCount);

            foreach (var currentKey in apiKeys)
            {
                if (attemptCount >= maxAttempts || isSuccess) break;
                bool keyInvalidOrQuotaExceeded = false;
                string maskedKey = MaskKey(currentKey);

                foreach (var modelName in geminiModels)
                {
                    if (attemptCount >= maxAttempts || keyInvalidOrQuotaExceeded || isSuccess) break;

                    // gemini-3.1-flash-lite mặc định bật suy luận ngầm (thinking), cần tắt thinkingBudget để in thẳng JSON câu hỏi
                    if (modelName.Contains("3.1", StringComparison.OrdinalIgnoreCase))
                    {
                        genConfig["thinkingConfig"] = new JsonObject
                        {
                            ["thinkingBudget"] = 0
                        };
                    }
                    else
                    {
                        genConfig.Remove("thinkingConfig");
                    }

                    var payloadJson = requestPayload.ToJsonString();
                    attemptCount++;
                    string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";
                    string requestUrl = $"{geminiBaseUrl.TrimEnd('/')}/models/{modelName}:generateContent?key={currentKey}";
                    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                    {
                        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                    };

                    _logger.LogInformation("[Lần thử {Attempt}/{MaxAttempts}] Đang gửi yêu cầu tới mô hình '{ModelName}' bằng API Key [{MaskedKey}]...", 
                        attemptCount, maxAttempts, modelName, maskedKey);

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                        var response = await _httpClient.SendAsync(requestMessage, linkedCts.Token);
                        var content = await response.Content.ReadAsStringAsync(cancellationToken);
                        var statusCode = (int)response.StatusCode;

                        if (response.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(content);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                            {
                                var candidate = candidates[0];
                                if (candidate.TryGetProperty("content", out var candContent) &&
                                    candContent.TryGetProperty("parts", out var parts) &&
                                    parts.GetArrayLength() > 0 &&
                                    parts[0].TryGetProperty("text", out var textProp))
                                {
                                    string textVal = textProp.GetString() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(textVal) && textVal != "{}")
                                    {
                                        textVal = SanitizeJsonForLatex(textVal);
                                        try
                                        {
                                            var testParsed = JsonSerializer.Deserialize<ParsedExamDto>(textVal, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                            int totalQ = (testParsed?.SingleQuestions?.Count ?? 0) + (testParsed?.Passages?.Sum(p => p.Questions.Count) ?? 0);
                                            
                                            if (totalQ > maxExtractedCount)
                                            {
                                                maxExtractedCount = totalQ;
                                                bestJsonText = textVal;
                                            }

                                            if (totalQ >= minQuestionThreshold)
                                            {
                                                _logger.LogInformation("Mô hình '{ModelName}' với API Key [{MaskedKey}] trích xuất thành công xuất sắc {Total} câu hỏi (vượt ngưỡng {MinThreshold})!", modelName, maskedKey, totalQ, minQuestionThreshold);
                                                rawJsonText = textVal;
                                                isSuccess = true;
                                                break; // Thành công, thoát vòng lặp model
                                            }
                                            else
                                            {
                                                _logger.LogWarning("Mô hình '{ModelName}' chỉ trích xuất được {Total} câu (chưa đủ 120 câu). Thử lựa chọn kế tiếp...", modelName, totalQ);
                                            }
                                        }
                                        catch (Exception parseEx)
                                        {
                                            _logger.LogWarning("Không thể parse kết quả từ '{ModelName}': {Message}. Thử lựa chọn kế tiếp...", modelName, parseEx.Message);
                                        }
                                    }
                                }
                            }

                            if (!isSuccess)
                            {
                                _logger.LogWarning("Mô hình '{ModelName}' phản hồi 200 nhưng không xuất được parts/text hợp lệ. Thử phương án tiếp theo...", modelName);
                            }
                            continue;
                        }

                        if (statusCode == 401 || statusCode == 403 || statusCode == 429)
                        {
                            _logger.LogWarning("API Key [{MaskedKey}] gặp lỗi xác thực/hạn mức (HTTP {StatusCode}). Chuyển sang API Key dự phòng...", maskedKey, statusCode);
                            keyInvalidOrQuotaExceeded = true;
                            break; // Thoát vòng lặp model, thử key tiếp theo
                        }
                        else if (statusCode == 503 || statusCode == 500 || statusCode == 504 || statusCode == 404 || statusCode == 400)
                        {
                            _logger.LogWarning("Mô hình '{ModelName}' gặp lỗi máy chủ/yêu cầu (HTTP {StatusCode}). Chuyển ngay sang MODEL tiếp theo với cùng API key [{MaskedKey}]...",
                                modelName, statusCode, maskedKey);
                            continue; // Thử ngay model tiếp theo với cùng key
                        }
                        else
                        {
                            _logger.LogWarning("Mô hình '{ModelName}' với Key [{MaskedKey}] trả về HTTP {StatusCode}. Thử lựa chọn kế tiếp...", modelName, maskedKey, statusCode);
                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Mô hình '{ModelName}' với API Key [{MaskedKey}] bị timeout sau 45s. Chuyển ngay sang MODEL tiếp theo.", modelName, maskedKey);
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Lỗi kết nối tới '{ModelName}' bằng Key [{MaskedKey}]. Thử lựa chọn kế tiếp...", modelName, maskedKey);
                        continue;
                    }
                }
            }
        }

        // Nếu không đạt ngưỡng 50 câu nhưng có kết quả tốt nhất > 0, dùng tạm kết quả tốt nhất trước khi gọi Local
        if (!isSuccess && maxExtractedCount > 0)
        {
            _logger.LogInformation("Sử dụng kết quả tốt nhất từ AI ({Attempts}/{MaxAttempts} lần thử): {Count} câu hỏi.", attemptCount, maxAttempts, maxExtractedCount);
            rawJsonText = bestJsonText;
            isSuccess = true;
        }

        if (!isSuccess || string.IsNullOrWhiteSpace(rawJsonText))
        {
            _logger.LogWarning("Đã thử tối đa {Attempts}/{MaxAttempts} lần. Tất cả mô hình và API Key (GPT/Gemini) đều không hoàn thành hoặc quá tải. Tự động kích hoạt Local Fallback Parser...", 
                attemptCount, maxAttempts);
            return await RunLocalFallbackParserAsync(pdfBytes, fileName, localImages, webRootPath, examId, cancellationToken);
        }

        // 6. Deserialize nội dung JSON thành DTO
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        rawJsonText = SanitizeJsonForLatex(rawJsonText);
        var parsedResult = JsonSerializer.Deserialize<ParsedExamDto>(rawJsonText, options)
            ?? new ParsedExamDto();

        CleanExamDto(parsedResult);

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

        _logger.LogInformation("Bóc tách đề thi '{FileName}' thành công qua MultiCloud AI: {Passages} chùm câu hỏi, {Questions} câu hỏi đơn lẻ.",
            fileName, parsedResult.TotalPassages, parsedResult.TotalSingleQuestions);

        // Tự động map ảnh gốc đã trích xuất cục bộ vào các câu hỏi và bài đọc
        if (localImages.Count > 0)
        {
            _imageExtractor.MapImagesToExam(parsedResult, localImages, webRootPath, examId);
        }

        return parsedResult;
    }

    /// <summary>
    /// Bộ bóc tách dự phòng cục bộ (Local Fallback Parser) khi cả OpenAI và Gemini bị lỗi 503 hoặc quá tải
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

            string pythonExe = _configuration["AiSettings:PythonExecutable"] 
                ?? Environment.GetEnvironmentVariable("PYTHON_PATH") 
                ?? (OperatingSystem.IsWindows() ? "python" : "python3");

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

                if (localImages.Count > 0)
                {
                    _imageExtractor.MapImagesToExam(localResult, localImages, webRootPath, examId);
                }

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

    private static string SanitizeJsonForLatex(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        // 1. Khôi phục các ký tự điều khiển ASCII bị nuốt do unescaped backslashes
        var result = json.Replace("\x0crac", "\\\\frac")
                         .Replace("\x09imes", "\\\\times")
                         .Replace("\x09ext", "\\\\text")
                         .Replace("\x08ar", "\\\\bar")
                         .Replace("\x08eta", "\\\\beta");

        // 2. Chuyển các lệnh LaTeX unescaped \frac, \times, \text, \Delta, \bar thành \\frac, \\times...
        result = Regex.Replace(result, @"(?<!\\)\\(frac|times|text|Delta|bar|beta|theta|tau|rho|right|left|le|ge|cdot|mu|omega|alpha|sigma|pi)(?=[^a-zA-Z]|$)", "\\\\$1");

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
             .Replace("\x09imes", "\\times")
             .Replace("\x09ext", "\\text")
             .Replace("\x08ar", "\\bar")
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
        foreach (var p in exam.Passages)
        {
            p.Content = CleanLatexString(p.Content);
            foreach (var q in p.Questions)
            {
                q.Content = CleanLatexString(q.Content);
                if (q.Options != null)
                {
                    var keys = q.Options.Keys.ToList();
                    foreach (var k in keys) q.Options[k] = CleanLatexString(q.Options[k]);
                }
            }
        }
        foreach (var q in exam.SingleQuestions)
        {
            q.Content = CleanLatexString(q.Content);
            if (q.Options != null)
            {
                var keys = q.Options.Keys.ToList();
                foreach (var k in keys) q.Options[k] = CleanLatexString(q.Options[k]);
            }
        }
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
}

