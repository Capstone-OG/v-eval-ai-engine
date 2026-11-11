using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Trích xuất tri thức SGK: Hỗ trợ nạp ngầm, lưu tiến trình liên tục (Checkpointing) vào Database,
/// cho phép Resume tiếp tục nạp ngay vị trí ngắt, kết hợp tối ưu ảnh thấp điểm ảnh (Low-DPI) để tiết kiệm token Vision AI Free Tier.
/// </summary>
public class PdfPigTextbookParserService : ITextbookParserService
{
    private const int CHUNK_SIZE = 1000;
    private const int CHUNK_OVERLAP = 200;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PdfPigTextbookParserService> _logger;
    private readonly ITextbookRepository _repository;

    public PdfPigTextbookParserService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<PdfPigTextbookParserService> logger,
        ITextbookRepository repository)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _repository = repository;
    }

    public async Task<TextbookIngestResultDto> ParseAndIngestTextbookAsync(
        Stream pdfStream,
        string fileName,
        string domainId,
        string skillId,
        string docType,
        string ocrMode = "TEXT_HUMANITIES",
        bool forceReingest = false,
        Action<string, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        onProgress?.Invoke("Đang đọc luồng dữ liệu PDF và tính mã Hash SHA-256...", 5);

        // 1. Lưu bộ nhớ stream và tính mã SHA-256 Hash
        using var memoryStream = new MemoryStream();
        await pdfStream.CopyToAsync(memoryStream, cancellationToken);
        byte[] pdfBytes = memoryStream.ToArray();

        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(pdfBytes);
        string fileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        // 2. Mở tệp PDF xác định tổng số trang
        using var document = PdfDocument.Open(pdfBytes);
        int totalPages = document.NumberOfPages;

        // 3. KIỂM TRA CHECKPOINT TIẾN TRÌNH TRONG CSDL SUPABASE (v_eval_ai)
        var checkpoint = await _repository.GetCheckpointByFileHashAsync(fileHash, cancellationToken);
        Guid sourceId;
        int startPage = 1;
        int runningCharCount = 0;
        int runningChunkCount = 0;

        if (!forceReingest && checkpoint != null && checkpoint.Value.LastProcessedPage > 0)
        {
            sourceId = checkpoint.Value.SourceId;
            runningCharCount = checkpoint.Value.TotalChars;
            runningChunkCount = checkpoint.Value.TotalChunks;

            // Nếu tệp này đã nạp xong 100% trước đó -> Tái sử dụng ngay lập tức
            if (checkpoint.Value.LastProcessedPage >= totalPages)
            {
                onProgress?.Invoke($"🎉 Tài liệu '{fileName}' đã được nạp hoàn tất từ trước trong CSDL! Đang khôi phục...", 100);
                var existingChunks = await _repository.GetChunksBySourceIdAsync(sourceId, cancellationToken);
                return new TextbookIngestResultDto
                {
                    DocumentId = sourceId.ToString(),
                    FileName = fileName,
                    FileHash = fileHash,
                    DomainId = domainId,
                    SkillId = skillId,
                    DocumentType = docType,
                    TotalPages = totalPages,
                    TotalChars = runningCharCount,
                    TotalChunks = existingChunks.Count,
                    Chunks = existingChunks
                };
            }

            // Nếu đang dở dang -> Tiếp tục ngay trang tiếp theo
            startPage = checkpoint.Value.LastProcessedPage + 1;
            onProgress?.Invoke($"⚡ Phát hiện checkpoint tại trang {checkpoint.Value.LastProcessedPage}/{totalPages}. Tiếp tục nạp tự động từ trang {startPage}...", 
                (int)((float)(startPage - 1) / totalPages * 100));
        }
        else
        {
            sourceId = await _repository.GetOrCreateSourceAsync(
                fileHash, fileName, $"/uploads/textbooks/{fileName}", totalPages, domainId, skillId, docType, cancellationToken);

            if (forceReingest)
            {
                await _repository.ResetSourceChunksAsync(sourceId, totalPages, cancellationToken);
                onProgress?.Invoke($"🔄 Chế độ Nạp lại từ đầu: Đã dọn dẹp các chunk cũ để bóc tách lại chuẩn xác!", 0);
            }
        }

        List<string> geminiKeys = ResolveGeminiKeys();
        List<string> geminiModels = ResolveGeminiModels();

        // 4. TIẾN HÀNH TRÍCH XUẤT VÀ LƯU CHECKPOINT TỪNG TRANG VÀO DATABASE
        for (int i = startPage; i <= totalPages; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int progressPct = (int)((float)i / totalPages * 100);
            string pageText = string.Empty;

            // Ưu tiên 1: Đọc nhanh lớp chữ số nếu có (PDF searchable) trừ khi chế độ yêu cầu bóc tách công thức STEM hoặc chế độ ép Vision AI
            if (ocrMode != "STEM_FORMULAS" && ocrMode != "VISION_AI")
            {
                try
                {
                    var page = document.GetPage(i);
                    string rawText = page.Text ?? string.Empty;
                    rawText = string.Join('\n', rawText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)));
                    
                    if (!string.IsNullOrWhiteSpace(rawText) && rawText.Length >= 30)
                    {
                        // Kiểm tra lỗi mã hóa font InDesign / CID subset (mojibake)
                        if (IsCorruptedFontEncoding(rawText))
                        {
                            _logger.LogWarning("[Trang {Page}/{TotalPages}] Phát hiện lớp chữ số bị lỗi mã hóa font InDesign/CID (mojibake). Bỏ qua lớp chữ số và kích hoạt Vision AI...", i, totalPages);
                            onProgress?.Invoke($"[Trang {i}/{totalPages}] ⚠️ Phát hiện lớp text bị lỗi font InDesign/CID (mojibake). Tự động kích hoạt Vision AI để bóc tách từ ảnh...", progressPct);
                        }
                        else
                        {
                            pageText = rawText;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Trang {Page}/{TotalPages}] Lỗi đọc text PdfPig, chuyển sang Vision OCR", i, totalPages);
                }
            }

            // Ưu tiên 2: Nếu không có chữ số (PDF scan dạng ảnh 100%) hoặc bị lỗi font hoặc chế độ Toán/Lý/Hóa/Vision -> Gọi Vision AI với ảnh nhẹ (DPI 96)
            if (string.IsNullOrWhiteSpace(pageText))
            {
                onProgress?.Invoke($"[Trang {i}/{totalPages}] Đang bóc tách bằng Vision AI (Ảnh tối ưu DPI 96 tiết kiệm token)...", progressPct);

                pageText = await OcrPageWithVisionModelsAsync(pdfBytes, i, geminiKeys, geminiModels, cancellationToken);

                // Giãn cách nhẹ (3.5s) cho Free Tier Google 15 RPM nếu chỉ có 1 API Key
                if (geminiKeys.Count <= 1)
                {
                    await Task.Delay(3500, cancellationToken);
                }
            }

            // Nếu vẫn không đọc được chữ nào, đặt placeholder tối thiểu để không bỏ lửng trang
            if (string.IsNullOrWhiteSpace(pageText))
            {
                pageText = $"[Nội dung hình ảnh/minh họa không chứa ký tự tại Trang {i}]";
            }

            // LƯU NGAY TIẾN TRÌNH VÀO CSDL SUPABASE (CHECKPOINT PER PAGE)
            runningChunkCount++;
            runningCharCount += pageText.Length;

            await _repository.SavePageChunkAsync(
                sourceId,
                i,
                runningChunkCount,
                pageText,
                domainId,
                skillId,
                docType,
                cancellationToken
            );

            await _repository.UpdateSourceProgressAsync(
                sourceId,
                totalPages,
                runningCharCount,
                runningChunkCount,
                "PROCESSING",
                cancellationToken
            );

            onProgress?.Invoke($"[Trang {i}/{totalPages}] Đã lưu CSDL thành công ({pageText.Length} ký tự). Tiến trình: {progressPct}%", progressPct);
        }

        // 5. HOÀN TẤT TOÀN BỘ CÁC TRANG -> CẬP NHẬT TRẠNG THÁI 'COMPLETED'
        await _repository.UpdateSourceProgressAsync(
            sourceId,
            totalPages,
            runningCharCount,
            runningChunkCount,
            "COMPLETED",
            cancellationToken
        );

        onProgress?.Invoke($"🎉 Đã hoàn tất 100% nạp và lưu trữ {totalPages} trang SGK vào Supabase Database!", 100);

        var finalChunks = await _repository.GetChunksBySourceIdAsync(sourceId, cancellationToken);

        return new TextbookIngestResultDto
        {
            DocumentId = sourceId.ToString(),
            FileName = fileName,
            FileHash = fileHash,
            DomainId = domainId,
            SkillId = skillId,
            DocumentType = docType,
            TotalPages = totalPages,
            TotalChars = runningCharCount,
            TotalChunks = finalChunks.Count,
            Chunks = finalChunks
        };
    }

    private static int _keyCounter = 0;

    /// <summary>
    /// Kết xuất trang PDF scan thành hình ảnh JPEG độ phân giải vừa phải (96 DPI) và gọi Vision API
    /// Giúp tiết kiệm tối đa token, tối thiểu chi phí và tăng tốc độ xử lý trên Free Tier
    /// </summary>
    private async Task<string> OcrPageWithVisionModelsAsync(
        byte[] pdfBytes,
        int pageNumber,
        List<string> geminiKeys,
        List<string> geminiModels,
        CancellationToken cancellationToken)
    {
        try
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            
            // TỐI ƯU HÓA: Render ở 96 DPI (vừa vặn nét cho OCR chữ/công thức mà kích thước chỉ ~800x1100 px, <80KB)
            using var skBitmap = Conversion.ToImage(pdfStream, page: pageNumber - 1, options: new RenderOptions { Dpi = 96 });
            if (skBitmap == null) return string.Empty;

            using var imageStream = new MemoryStream();
            skBitmap.Encode(imageStream, SKEncodedImageFormat.Jpeg, quality: 70);
            byte[] imageBytes = imageStream.ToArray();
            string base64Image = Convert.ToBase64String(imageBytes);

            string prompt = "BẠN LÀ MÁY SCAN OCR ĐỘ CHÍNH XÁC CAO. Đọc và trích xuất NGUYÊN VĂN 100% nội dung chữ, bảng biểu (dạng Markdown table), thơ và công thức trong hình ảnh này. Mọi công thức Toán/Lý/Hóa bao bọc bằng cặp dấu đô-la $...$ theo cú pháp LaTeX.";

            // 1. Chuẩn bị payload JSON (không kèm thinkingConfig để tương thích 100% với các dòng Lite & Flash mới)
            var payloadObj = new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = prompt },
                            new JsonObject
                            {
                                ["inline_data"] = new JsonObject
                                {
                                    ["mime_type"] = "image/jpeg",
                                    ["data"] = base64Image
                                }
                            }
                        }
                    }
                },
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0.0
                }
            };
            string jsonString = payloadObj.ToJsonString();

            // 2. Sắp xếp xoay vòng API Key luân phiên (Round-Robin) tăng gấp đôi thông lượng
            var orderedKeys = new List<string>(geminiKeys);
            if (orderedKeys.Count > 1)
            {
                int shift = (Interlocked.Increment(ref _keyCounter) & 0x7FFFFFFF) % orderedKeys.Count;
                orderedKeys = orderedKeys.Skip(shift).Concat(orderedKeys.Take(shift)).ToList();
            }

            foreach (var model in geminiModels)
            {
                foreach (var apiKey in orderedKeys)
                {
                    try
                    {
                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                        using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                        var response = await _httpClient.PostAsync(url, content, cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            var jsonNode = JsonNode.Parse(responseBody);
                            var partsNode = jsonNode?["candidates"]?[0]?["content"]?["parts"]?.AsArray();

                            var sb = new StringBuilder();
                            if (partsNode != null)
                            {
                                foreach (var part in partsNode)
                                {
                                    if (part?["thought"]?.GetValue<bool>() == true) continue;
                                    var txt = part?["text"]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(txt))
                                    {
                                        sb.AppendLine(txt);
                                    }
                                }
                            }

                            string extractedText = sb.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(extractedText))
                            {
                                return extractedText;
                            }
                        }
                        else
                        {
                            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            _logger.LogWarning("[Trang {PageNumber}] Gọi Gemini Model '{Model}' thất bại (HTTP {Status}): {Error}", 
                                pageNumber, model, (int)response.StatusCode, errBody.Length > 250 ? errBody[..250] : errBody);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi gọi Gemini Model '{Model}' cho trang {PageNumber}", model, pageNumber);
                    }
                }
            }

            // 2. Fallback sang OpenAI Vision gpt-4o-mini nếu cấu hình
            string? openAiKey = ResolveOpenAiKey();
            if (!string.IsNullOrWhiteSpace(openAiKey))
            {
                try
                {
                    string ocrResult = await OcrPageWithOpenAiAsync(openAiKey, base64Image, prompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(ocrResult))
                    {
                        return ocrResult;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OpenAI Vision fallback thất bại cho trang {PageNumber}", pageNumber);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết xuất ảnh OCR cho trang {PageNumber}", pageNumber);
        }

        return string.Empty;
    }

    private async Task<string> OcrPageWithOpenAiAsync(string openAiKey, string base64Image, string prompt, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:image/jpeg;base64,{base64Image}"
                            }
                        }
                    }
                }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAiKey);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var textProp))
                {
                    return textProp.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private List<string> ResolveGeminiKeys()
    {
        var keys = new List<string>();
        var keyArr = _configuration.GetSection("AiSettings:GeminiApiKeys").Get<string[]>();
        if (keyArr != null) keys.AddRange(keyArr);

        var singleKey = _configuration["AiSettings:GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(singleKey)) keys.Add(singleKey);

        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) keys.Add(envKey);

        return keys
            .Select(k => k.Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_"))
            .Distinct()
            .ToList();
    }

    private List<string> ResolveGeminiModels()
    {
        var models = _configuration.GetSection("AiSettings:GeminiModels").Get<string[]>();
        if (models != null && models.Length > 0)
        {
            return models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();
        }

        return new List<string> { "gemini-flash-lite-latest", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.8-flash", "gemini-3.6-flash" };
    }

    private string? ResolveOpenAiKey()
    {
        var key = _configuration["AiSettings:ApiKey"] 
                  ?? _configuration["AiSettings:OpenAiApiKey"]
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("YOUR_")) return null;
        return key.Trim();
    }

    private static readonly HashSet<char> VietnameseVowels = new HashSet<char>
    {
        'a', 'ă', 'â', 'e', 'ê', 'i', 'o', 'ô', 'ơ', 'u', 'ư', 'y',
        'á', 'à', 'ả', 'ã', 'ạ',
        'ắ', 'ằ', 'ẳ', 'ẵ', 'ặ',
        'ấ', 'ầ', 'ẩ', 'ẫ', 'ậ',
        'é', 'è', 'ẻ', 'ẽ', 'ẹ',
        'ế', 'ề', 'ể', 'ễ', 'ệ',
        'í', 'ì', 'ỉ', 'ĩ', 'ị',
        'ó', 'ò', 'ỏ', 'õ', 'ọ',
        'ố', 'ồ', 'ổ', 'ỗ', 'ộ',
        'ớ', 'ờ', 'ở', 'ỡ', 'ợ',
        'ú', 'ù', 'ủ', 'ũ', 'ụ',
        'ứ', 'ừ', 'ử', 'ữ', 'ự',
        'ý', 'ỳ', 'ỷ', 'ỹ', 'ỵ',
        'A', 'Ă', 'Â', 'E', 'Ê', 'I', 'O', 'Ô', 'Ơ', 'U', 'Ư', 'Y',
        'Á', 'À', 'Ả', 'Ã', 'Ạ',
        'Ắ', 'Ằ', 'Ẳ', 'Ẵ', 'Ặ',
        'Ấ', 'Ầ', 'Ẩ', 'Ẫ', 'Ậ',
        'É', 'È', 'Ẻ', 'Ẽ', 'Ẹ',
        'Ế', 'Ề', 'Ể', 'Ễ', 'Ệ',
        'Í', 'Ì', 'Ỉ', 'Ĩ', 'Ị',
        'Ó', 'Ò', 'Ỏ', 'Õ', 'Ọ',
        'Ố', 'Ồ', 'Ổ', 'Ỗ', 'Ộ',
        'Ớ', 'Ờ', 'Ở', 'Ỡ', 'Ợ',
        'Ú', 'Ù', 'Ủ', 'Ũ', 'Ụ',
        'Ứ', 'Ừ', 'Ử', 'Ữ', 'Ự',
        'Ý', 'Ỳ', 'Ỷ', 'Ỹ', 'Ỵ'
    };

    private static readonly HashSet<char> CorruptedGlyphs = new HashSet<char>
    {
        '{', '}', '\\', '^', '~', '|', '¶', '§', '©', '®', '½', '¼', '¾', '¿', '±', '`', '¤', '°',
        'ł', 'Ċ', 'ī', 'Ť', 'Š', 'ś', 'ř', 'œ', 'ż', 'ź', 'č', 'ď', 'ń', 'ň', 'ħ', 'Ĉ', 'Ў', 'Č', 'Г', 'ə',
        'Ä', 'Å', 'Ö', 'Ā', 'Ś', 'Ĩ', 'Ũ', 'Ð', 'þ', 'Ñ', 'ť', 'Ğ', 'ï'
    };

    /// <summary>
    /// Phát hiện lớp text trích xuất từ PDF có bị lỗi mã hóa font InDesign/CID subset (mojibake) hay không.
    /// Nếu bị lỗi font, văn bản đọc từ PdfPig sẽ là các chuỗi vô nghĩa (&IFHPkFVLQK, QKQKL, v.v.).
    /// </summary>
    public static bool IsCorruptedFontEncoding(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 30) return false;

        int corruptedCount = 0;
        int totalLetters = 0;
        int vowelCount = 0;

        foreach (char c in text)
        {
            if (CorruptedGlyphs.Contains(c)) corruptedCount++;
            if (char.IsLetter(c))
            {
                totalLetters++;
                if (VietnameseVowels.Contains(c)) vowelCount++;
            }
        }

        // 1. Nếu mật độ ký tự rác / glyph bất thường >= 1.0% (hoặc có từ 4 ký tự lạ trở lên và chiếm >= 0.8%) -> Lỗi font
        if (corruptedCount >= 4 && ((double)corruptedCount / text.Length) >= 0.008)
        {
            return true;
        }

        // 2. Kiểm tra tỷ lệ nguyên âm: Văn bản Tiếng Việt / Tiếng Anh thông thường có 32% - 50% nguyên âm.
        // Khi bị lỗi font CID InDesign, tỷ lệ nguyên âm sụt giảm mạnh (< 24%) do mã glyph bị dịch sang phụ âm.
        if (totalLetters >= 30)
        {
            double vowelRatio = (double)vowelCount / totalLetters;
            if (vowelRatio < 0.23)
            {
                return true;
            }
        }

        // 3. Phân tích các từ có độ dài >= 4:
        var words = text.Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '"', '\'', '`' }, 
            StringSplitOptions.RemoveEmptyEntries);

        int vowellessWordCount = 0;
        int totalSignificantWords = 0;
        int consecutiveConsonantBombCount = 0;

        foreach (var word in words)
        {
            string cleanWord = new string(word.Where(char.IsLetter).ToArray());
            if (cleanWord.Length < 4) continue;

            totalSignificantWords++;

            bool hasVowel = cleanWord.Any(c => VietnameseVowels.Contains(c));
            if (!hasVowel)
            {
                vowellessWordCount++;
            }

            int currentConsonants = 0;
            foreach (char c in cleanWord)
            {
                if (!VietnameseVowels.Contains(c))
                {
                    currentConsonants++;
                    if (currentConsonants >= 5)
                    {
                        consecutiveConsonantBombCount++;
                        break;
                    }
                }
                else
                {
                    currentConsonants = 0;
                }
            }
        }

        // Nếu có từ 2 từ dài không chứa nguyên âm nào trở lên VÀ chiếm >= 15% số từ quan trọng -> Lỗi font
        if (totalSignificantWords >= 5 && vowellessWordCount >= 2 && ((double)vowellessWordCount / totalSignificantWords) >= 0.15)
        {
            return true;
        }

        // Nếu có từ 3 từ chứa cụm phụ âm liên tiếp >= 5 trở lên (VD: WKKQPQC, tFKWKtFKWt)
        if (consecutiveConsonantBombCount >= 3)
        {
            return true;
        }

        return false;
    }
}
