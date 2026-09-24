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
/// Trích xuất tri thức SGK: Dùng Python PyMuPDF Local OCR siêu tốc cho Ngữ Văn/Anh Văn (1-2s), và tự động fallback sang Vision AI cho PDF Scan Ảnh hoặc Toán/Lý/Hóa
/// </summary>
public class PdfPigTextbookParserService : ITextbookParserService
{
    private const int CHUNK_SIZE = 1000;
    private const int CHUNK_OVERLAP = 200;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PdfPigTextbookParserService> _logger;

    public PdfPigTextbookParserService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<PdfPigTextbookParserService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TextbookIngestResultDto> ParseAndIngestTextbookAsync(
        Stream pdfStream,
        string fileName,
        string domainId,
        string skillId,
        string docType,
        string ocrMode = "TEXT_HUMANITIES",
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

        var pageTextMap = new List<(int PageNumber, string Text)>();
        int totalChars = 0;

        // 3. LUỒNG 1: DÀNH CHO VĂN BẢN THUẦN (Ngữ Văn, Tiếng Anh, Sử, Địa) -> Chạy Python PyMuPDF Local OCR siêu tốc (1-2s)
        if (ocrMode == "TEXT_HUMANITIES" || ocrMode == "AUTO_LOCAL")
        {
            onProgress?.Invoke($"Phát hiện {totalPages} trang PDF (Chế độ: Văn Bản Thuần Ngữ Văn/Anh Văn). Đang kích hoạt Python PyMuPDF Local OCR siêu tốc...", 15);

            pageTextMap = await RunPythonLocalParserAsync(pdfBytes, onProgress);
            
            if (pageTextMap.Count > 0)
            {
                totalChars = pageTextMap.Sum(p => p.Text.Length);
            }

            // Fallback nếu C# PdfPig đọc local khi Python trả rỗng
            if (totalChars == 0)
            {
                onProgress?.Invoke("Thử trích xuất bằng C# PdfPig Local Engine...", 25);
                for (int i = 1; i <= totalPages; i++)
                {
                    var page = document.GetPage(i);
                    string pageText = page.Text ?? string.Empty;
                    pageText = string.Join('\n', pageText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)));

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        pageTextMap.Add((i, pageText));
                        totalChars += pageText.Length;
                    }
                }
            }

            // Nếu VẪN RỖNG (File PDF Scan dạng HÌNH ẢNH 100%) -> Tự động chuyển Vision OCR bóc chữ từ ảnh scan
            if (totalChars == 0)
            {
                onProgress?.Invoke($"⚠️ Phát hiện {totalPages} trang PDF Scan dạng HÌNH ẢNH (không chứa lớp chữ text). Đang kích hoạt Vision OCR bóc chữ từ ảnh...", 30);

                List<string> geminiKeys = ResolveGeminiKeys();
                List<string> geminiModels = ResolveGeminiModels();

                for (int i = 1; i <= totalPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onProgress?.Invoke($"[Trang {i}/{totalPages}] Đang Vision OCR chữ từ ảnh scan...", 30 + (int)((float)i / totalPages * 50));

                    string ocrText = await OcrPageWithVisionModelsAsync(pdfBytes, i, geminiKeys, geminiModels, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        pageTextMap.Add((i, ocrText));
                        totalChars += ocrText.Length;
                    }
                }
            }
            else
            {
                onProgress?.Invoke($"Đã trích xuất thành công {totalChars} ký tự từ {pageTextMap.Count}/{totalPages} trang bằng Local Fast Engine!", 75);
            }
        }
        else
        {
            // LUỒNG 2: DÀNH CHO TỰ NHIÊN & CÔNG THỨC (Toán, Lý, Hóa) -> Giữ nguyên luồng Vision AI cho LaTeX
            onProgress?.Invoke($"Phát hiện {totalPages} trang PDF (Chế độ: Tự Nhiên & Công Thức Toán/Lý/Hóa). Đang chạy luồng Vision OCR...", 15);

            List<string> geminiKeys = ResolveGeminiKeys();
            List<string> geminiModels = ResolveGeminiModels();

            for (int i = 1; i <= totalPages; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = document.GetPage(i);
                string pageText = page.Text ?? string.Empty;
                pageText = string.Join('\n', pageText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)));

                if (string.IsNullOrWhiteSpace(pageText) || pageText.Length < 10)
                {
                    onProgress?.Invoke($"[Trang {i}/{totalPages}] Đang OCR công thức Toán/Lý/Hóa bằng Vision AI...", 15 + (int)((float)i / totalPages * 60));
                    string ocrText = await OcrPageWithVisionModelsAsync(pdfBytes, i, geminiKeys, geminiModels, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        pageText = ocrText;
                    }
                }

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    pageTextMap.Add((i, pageText));
                    totalChars += pageText.Length;
                }
            }
        }

        onProgress?.Invoke("Đang tiến hành cắt Chunk tri thức theo ranh giới Semantic...", 85);

        // 4. Cắt Chunking (Chunk Size ~1000, Overlap ~200)
        var chunks = BuildSemanticChunks(pageTextMap);

        onProgress?.Invoke($"Đã hoàn tất cắt {chunks.Count} Chunks tri thức. Đang chuẩn bị hoàn tất...", 95);

        var result = new TextbookIngestResultDto
        {
            DocumentId = Guid.NewGuid().ToString(),
            FileName = fileName,
            FileHash = fileHash,
            DomainId = domainId,
            SkillId = skillId,
            DocumentType = string.IsNullOrWhiteSpace(docType) ? "TEXTBOOK" : docType,
            TotalPages = totalPages,
            TotalChars = totalChars,
            TotalChunks = chunks.Count,
            Chunks = chunks
        };

        onProgress?.Invoke($"Nạp hoàn tất {totalPages} trang, {totalChars} ký tự, {chunks.Count} Chunks!", 100);

        return result;
    }

    /// <summary>
    /// Chạy Python script local (textbook_local_parser.py) trích xuất chữ thuần 100% offline trong 1-2 giây
    /// </summary>
    private async Task<List<(int PageNumber, string Text)>> RunPythonLocalParserAsync(byte[] pdfBytes, Action<string, int>? onProgress)
    {
        var pageTextMap = new List<(int PageNumber, string Text)>();
        string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");

        try
        {
            await File.WriteAllBytesAsync(tempPath, pdfBytes);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptPath = Path.Combine(baseDir, "Parsers", "textbook_local_parser.py");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Parsers", "textbook_local_parser.py");
            }
            if (!File.Exists(scriptPath))
            {
                scriptPath = @"e:\CapStone\All Services\V-Eval-Ai_Engine\V-Eval-Ai_Engine.Infrastructure\Parsers\textbook_local_parser.py";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    if (e.Data.StartsWith("[PROGRESS]"))
                    {
                        string logMsg = e.Data.Replace("[PROGRESS]", "").Trim();
                        var match = System.Text.RegularExpressions.Regex.Match(logMsg, @"\[Trang (\d+)/(\d+)\]");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int curP) && int.TryParse(match.Groups[2].Value, out int totP) && totP > 0)
                        {
                            int pct = 15 + (int)((float)curP / totP * 65);
                            onProgress?.Invoke(logMsg, pct);
                        }
                        else
                        {
                            onProgress?.Invoke(logMsg, 20);
                        }
                    }
                    _logger.LogInformation("[Python Local Parser] {Log}", e.Data);
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            string jsonOutput = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(jsonOutput))
            {
                using var doc = JsonDocument.Parse(jsonOutput);
                if (doc.RootElement.TryGetProperty("pages", out var pagesElement))
                {
                    foreach (var item in pagesElement.EnumerateArray())
                    {
                        int pageNum = item.GetProperty("page").GetInt32();
                        string text = item.GetProperty("text").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            pageTextMap.Add((pageNum, text));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi chạy Python Local Parser, chuyển sang luồng fallback...");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return pageTextMap;
    }

    /// <summary>
    /// Kết xuất trang PDF scan thành hình ảnh JPEG và gọi Vision API (Gemini/OpenAI) cho bài toán Toán/Lý/Hóa
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
            using var skBitmap = PDFtoImage.Conversion.ToImage(pdfStream, page: pageNumber - 1);
            if (skBitmap == null) return string.Empty;

            using var imageStream = new MemoryStream();
            skBitmap.Encode(imageStream, SKEncodedImageFormat.Jpeg, quality: 80);
            byte[] imageBytes = imageStream.ToArray();
            string base64Image = Convert.ToBase64String(imageBytes);

            string prompt = "BẠN LÀ BỘ ĐỌC OCR CHÍNH XÁC CAO CHO ĐỀ THI VÀ TÀI LIỆU TOÁN, VẬT LÝ, HÓA HỌC.\nNhiệm vụ: Hãy đọc và trích xuất NGUYÊN VĂN 100% nội dung chữ và công thức trong hình ảnh này. Mọi công thức toán/lý/hóa phải được bao bọc bởi cặp dấu đô-la $...$ theo đúng cú pháp LaTeX.";

            // 1. Thử các API Keys & các Mô Hình Gemini hợp lệ (gemini-1.5-flash, gemini-2.0-flash...)
            foreach (var apiKey in geminiKeys)
            {
                foreach (var model in geminiModels)
                {
                    try
                    {
                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                        
                        var payload = new
                        {
                            contents = new[]
                            {
                                new
                                {
                                    parts = new object[]
                                    {
                                        new { text = prompt },
                                        new
                                        {
                                            inline_data = new
                                            {
                                                mime_type = "image/jpeg",
                                                data = base64Image
                                            }
                                        }
                                    }
                                }
                            }
                        };

                        string jsonString = JsonSerializer.Serialize(payload);
                        using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                        var response = await _httpClient.PostAsync(url, content, cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            var jsonNode = JsonNode.Parse(responseBody);
                            string extractedText = jsonNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(extractedText))
                            {
                                return extractedText.Trim();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi gọi Gemini Model '{Model}' cho trang {PageNumber}", model, pageNumber);
                    }
                }
            }

            // 2. Fallback sang OpenAI Vision gpt-4o-mini
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
        var list = new List<string>();

        if (models != null && models.Length > 0)
        {
            list.AddRange(models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()));
        }

        var defaultModels = new[] { "gemini-1.5-flash", "gemini-2.0-flash", "gemini-1.5-pro", "gemini-2.0-flash-exp" };
        foreach (var def in defaultModels)
        {
            if (!list.Contains(def)) list.Add(def);
        }

        return list.Distinct().ToList();
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

    private static List<TextbookChunkDto> BuildSemanticChunks(List<(int PageNumber, string Text)> pageTextMap)
    {
        var chunks = new List<TextbookChunkDto>();
        int chunkIndex = 1;
        var currentChunkBuilder = new StringBuilder();
        int currentStartPage = 1;

        foreach (var (pageNumber, text) in pageTextMap)
        {
            if (currentChunkBuilder.Length == 0)
            {
                currentStartPage = pageNumber;
            }

            currentChunkBuilder.AppendLine(text);

            while (currentChunkBuilder.Length >= CHUNK_SIZE)
            {
                string chunkContent = currentChunkBuilder.ToString(0, CHUNK_SIZE);
                chunks.Add(new TextbookChunkDto
                {
                    ChunkIndex = chunkIndex++,
                    PageNumber = currentStartPage,
                    Text = chunkContent,
                    CharCount = chunkContent.Length
                });

                int keepLength = Math.Min(CHUNK_OVERLAP, currentChunkBuilder.Length);
                string overlapText = currentChunkBuilder.ToString(currentChunkBuilder.Length - keepLength, keepLength);
                currentChunkBuilder.Clear();
                currentChunkBuilder.Append(overlapText);
            }
        }

        if (currentChunkBuilder.Length > 0)
        {
            chunks.Add(new TextbookChunkDto
            {
                ChunkIndex = chunkIndex++,
                PageNumber = currentStartPage,
                Text = currentChunkBuilder.ToString(),
                CharCount = currentChunkBuilder.Length
            });
        }

        return chunks;
    }
}
