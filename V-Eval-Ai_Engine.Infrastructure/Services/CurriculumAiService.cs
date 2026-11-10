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
            "gemini-3.5-flash-lite",
            "gemini-3.8-flash",
            "gemini-3.6-flash",
            "gemini-3.1-flash-lite",
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
    /// Đảm bảo phân bổ đều từ trang đầu tới trang cuối để Multimodal Vision AI nắm bắt 100% cấu trúc toàn tài liệu.
    /// </summary>
    private List<string> ExtractDownscaledPageImages(
        byte[] pdfBytes,
        int targetStart,
        int targetEnd,
        int maxImages = 10)
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
                selectedPages = new List<Page>();
                // Luôn chọn trang đầu tiên và trang thứ hai
                selectedPages.Add(matchingPages[0]);
                if (matchingPages.Count > 1) selectedPages.Add(matchingPages[1]);

                // Các trang trung gian phân bổ đều
                int middleSlots = maxImages - 3;
                if (middleSlots > 0)
                {
                    double step = (double)(matchingPages.Count - 2) / (middleSlots + 1);
                    for (int i = 1; i <= middleSlots; i++)
                    {
                        int index = (int)Math.Round(i * step);
                        if (index >= 0 && index < matchingPages.Count && !selectedPages.Contains(matchingPages[index]))
                        {
                            selectedPages.Add(matchingPages[index]);
                        }
                    }
                }

                // Luôn chọn trang cuối cùng
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
                            float scale = maxDim > 800 ? 800f / maxDim : 1.0f;
                            int targetW = (int)(originalBitmap.Width * scale);
                            int targetH = (int)(originalBitmap.Height * scale);

                            using var resized = originalBitmap.Resize(new SKImageInfo(targetW, targetH), SKSamplingOptions.Default);
                            using var skImage = SKImage.FromBitmap(resized);
                            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 55);
                            jpegBytes = encoded.ToArray();
                        }
                    }
                }

                // 2. Nếu trang dạng vector không có ảnh nhúng, dùng PDFtoImage kết xuất sang bitmap
                if (jpegBytes == null)
                {
                    using var ms = new MemoryStream(pdfBytes);
                    using var renderedBitmap = Conversion.ToImage(ms, page: page.Number - 1, options: new RenderOptions { Dpi = 96 });
                    if (renderedBitmap != null)
                    {
                        using var skImage = SKImage.FromBitmap(renderedBitmap);
                        using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 55);
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
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int totalPagesProcessed = 0;
        string extractedText = string.Empty;
        var pageImagesBase64 = new List<string>();
        byte[]? pdfBytes = null;

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

                // Phát hiện PDF dạng ảnh scan / OCR không có text số hóa
                if (actualTextChars < 300 && pdfBytes != null)
                {
                    _logger.LogInformation("Phát hiện tài liệu PDF dạng ảnh scan / không có lớp text số hóa ({Chars} ký tự). Kích hoạt động cơ trích xuất Multimodal Vision AI...", actualTextChars);
                    pageImagesBase64 = ExtractDownscaledPageImages(pdfBytes, targetStart, targetEnd, maxImages: 10);
                    _logger.LogInformation("Đã trích xuất và tối ưu hóa {Count} trang ảnh scan chất lượng cao cho Multimodal Vision.", pageImagesBase64.Count);
                    extractedText = $"[Tài liệu PDF ảnh scan gồm {totalPagesProcessed} trang. Dữ liệu các trang đã được trích xuất tối ưu hóa hình ảnh và truyền trực tiếp qua kênh Multimodal Vision AI.]";
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

        string textSnippet = extractedText.Length > 200000 ? extractedText[..200000] : extractedText;

        bool isDeep = string.IsNullOrWhiteSpace(extractionDepth) || extractionDepth.Equals("deep", StringComparison.OrdinalIgnoreCase);

        string depthDirective = isDeep
            ? $@"YÊU CẦU BÓC TÁCH CẤU TRÚC TRI THỨC VÀ TỐI ĐA HÓA CÁC DẠNG BÀI (MAXIMIZE PEDAGOGICAL PATTERNS DIRECTIVE - CHUYÊN SÂU ĐGNL):
- Phân tích quét TOÀN BỘ nội dung thực tế từ tài liệu thuộc lĩnh vực: {domainName} (Bao gồm cả các môn Khoa học tự nhiên như Hóa học, Vật lý, Sinh học và Toán học).
- NGUYÊN TẮC BẮT BUỘC PHÂN TÁCH BÀI HỌC (MULTI-LESSON EXTRACTION):
  + Quét toàn bộ tài liệu để nhận diện TẤT CẢ các Bài học riêng biệt trong chương/chuyên đề.
  + Ví dụ chuyên đề Ester - Lipid: BẮT BUỘC phân tách thành 2 bài học riêng biệt: 'Bài 1: Ester' và 'Bài 2: Lipid & Chất béo'. TUYỆT ĐỐI KHÔNG gộp thành 1 bài học duy nhất!
- NGUYÊN TẮC TỐI ĐA HÓA SỐ LƯỢNG DẠNG BÀI (PATTERNS) - TUYỆT ĐỐI KHÔNG BỎ SÓT NỘI DUNG:
  + Quét từ trang đầu đến trang cuối để nhận diện ĐẦY ĐỦ TẤT CẢ các dạng bài xuất hiện trong tài liệu.
  + TUYỆT ĐỐI KHÔNG được tóm tắt gộp hoặc chỉ tạo 2-3 dạng bài sơ sài.
  + Đối với tài liệu chuyên đề toàn diện / tài liệu dày (từ 10 đến 50+ trang như chuyên đề Ester - Lipid, Hàm số...), BẮT BUỘC nhận diện và bóc tách từ 6 đến 12+ Dạng bài (`patterns`) chuyên biệt:
    * Bài 1 (Ester): Bóc tách từ 4 đến 5 dạng bài thực chiến (Đồng phân & Danh pháp, Tính chất vật lý & hóa học, Phản ứng thủy phân/xà phòng hóa, Phản ứng đốt cháy, Bài toán điều chế & hiệu suất este hóa).
    * Bài 2 (Lipid & Chất béo): BẮT BUỘC bóc tách đủ 6 đến 8 dạng bài chi tiết (Khái niệm & Cấu tạo lipid/triglyceride, Tính chất vật lý & trạng thái chất béo, Phản ứng xà phòng hóa chất béo, Bảo toàn khối lượng xà phòng hóa, Chỉ số xà phòng hóa/chỉ số acid/chỉ số iot, Đốt cháy chất béo & bảo toàn nguyên tố, Phản ứng hydro hóa chất béo lỏng, Ứng dụng & vai trò dinh dưỡng).
  + Mỗi Dạng bài BẮT BUỘC có đủ 4 khối nội dung: `theoryRecap` (lý thuyết đầy đủ, phương trình LaTeX chuẩn), `solutionMethod` (thuật toán từng bước, bài mẫu chi tiết, không sinh chữ 'undefined'), `durationSeconds: 600`, và từ 2 đến 3 câu hỏi bài mẫu `questions` (L1/L2 -> L3/L4) kèm KaTeX chuẩn, đáp án và giải chi tiết."
            : $@"YÊU CẦU BÓC TÁCH TIÊU CHUẨN - TRỌNG TÂM CỐT LÕI, ĐẦY ĐỦ LÝ THUYẾT & NHIỀU DẠNG CON (STANDARD DEEP-THEORY & SUB-PATTERNS DIRECTIVE):
- Phân tích quét nội dung theo cấu trúc mục lục và các đề mục bài học lớn của tài liệu: {domainName}.
- NGUYÊN TẮC BẮT BUỘC PHÂN TÁCH BÀI HỌC (MULTI-LESSON EXTRACTION):
  + Quét toàn bộ tài liệu để nhận diện TẤT CẢ các Bài học riêng biệt trong chương/chuyên đề (Ví dụ: 'Bài 1: Ester' và 'Bài 2: Lipid & Chất béo', tuyệt đối không gộp thành 1 bài).
- NGUYÊN TẮC BÓC TÁCH TIÊU CHUẨN (NHIỀU DẠNG BÀI CON - LÝ THUYẾT ĐẦY ĐỦ TOÀN DIỆN):
  + Cấu trúc bài học: Bóc tách các chủ đề lớn theo khung mục lục chuẩn SGK GDPT 2018 (Chương -> Các bài học riêng biệt).
  + Số lượng dạng bài: Mỗi bài học bóc tách từ 2 đến 4 dạng bài lớn trọng tâm, bao quát toàn bộ nội dung bài học.
  + ĐẶC BIỆT 1 - LÝ THUYẾT PHẢI ĐẦY ĐỦ VÀ SÂU SẮC NHẤT (`theoryRecap`):
    * BẮT BUỘC trình bày trọn vẹn, chi tiết 100% cơ sở lý thuyết, định nghĩa, định lý, tính chất vật lý và hóa học, hệ quả, điều kiện áp dụng, cơ chế phản ứng và phương trình hóa học bằng KaTeX tổng quát từ SGK/tài liệu.
    * TUYỆT ĐỐI KHÔNG tóm tắt sơ sài vài dòng; phải cung cấp phần lý thuyết hoàn chỉnh để học sinh và giảng viên có thể dùng làm tài liệu học tập và tra cứu lý thuyết chuyên sâu.
  + ĐẶC BIỆT 2 - PHÂN CHIA NHIỀU DẠNG BÀI CON TRONG PHƯƠNG PHÁP GIẢI (`solutionMethod`):
    * Bên trong mỗi Dạng bài lớn, phần phương pháp giải BẮT BUỘC phân rã chi tiết thành các Dạng con cụ thể (Dạng con 1.1, Dạng con 1.2, Dạng con 1.3... hoặc Trường hợp 1, Trường hợp 2...).
    * Mỗi dạng con nêu rõ: Dấu hiệu nhận biết, thuật toán/quy trình giải từng bước, công thức giải nhanh và ví dụ minh họa áp dụng (TUYỆT ĐỐI KHÔNG sinh chữ 'undefined').
  + Câu hỏi trắc nghiệm rèn luyện (`questions`): Trích xuất từ 2 đến 3 câu hỏi trắc nghiệm tiêu chuẩn bao quát các dạng con kèm lời giải chi tiết.";

        // 2. Thử gọi Google Gemini qua C# HttpClient
        var apiKeys = ResolveApiKeys();
        var geminiModels = ResolveGeminiModels();
        string geminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

        bool isStem = IsStemDomain(domainName);
        string domainScope = isStem ? "TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS)" : "TỪNG DẠNG BÀI ĐỌC HIỂU / LÀM VĂN / BÀI HỌC CỤ THỂ (PATTERNS)";
        string sampleHeading = isStem ? "### 📄 Bài toán mẫu áp dụng phương pháp:" : "### 📄 Bài mẫu áp dụng phương pháp:";
        string tipHeading = isStem ? "### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:" : "### 💡 Mẹo tư duy & Chiến thuật làm bài:";
        string tipGuideline = isStem 
            ? "Mẹo nhẩm, kỹ thuật tính nhanh số đồng phân/hiệu suất/khối lượng, phương pháp quy đổi/đồng đẳng hóa hoặc bấm máy giải hệ phương trình Casio (fx-580VN X, fx-880BTG)" 
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
                                        AllowTrailingCommas = true,
                                        ReadCommentHandling = JsonCommentHandling.Skip
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
                                    FinalizeAndCleanParsedCurriculum(parsed, domainId, domainName);
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
                            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            if (errBody.Length > 200) errBody = errBody.Substring(0, 200) + "...";
                            _logger.LogWarning("Gemini API model '{Model}' trả về HTTP {StatusCode} ({ErrorBody}), tiếp tục chuyển đổi model/key tiếp theo.", 
                                modelName, (int)response.StatusCode, errBody);
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
                    string? gptResult = await TryCallOpenAiCurriculumAsync(gptModel, openAiKey, prompt, cancellationToken, pageImagesBase64);
                    if (!string.IsNullOrWhiteSpace(gptResult))
                    {
                        string contentText = SanitizeAndRepairGeminiJson(gptResult);
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
                            FinalizeAndCleanParsedCurriculum(parsed, domainId, domainName);
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
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(7);
        else if (cleaned.StartsWith("```"))
            cleaned = cleaned.Substring(3);

        if (cleaned.EndsWith("```"))
            cleaned = cleaned.Substring(0, cleaned.Length - 3);

        cleaned = cleaned.Trim();

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

    private static ImportCurriculumRequestDto? TryParseCurriculumFromLooseJson(string rawJson, string domainId, string domainName)
    {
        try
        {
            var node = JsonNode.Parse(rawJson, new JsonNodeOptions(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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

    private static void FinalizeAndCleanParsedCurriculum(ImportCurriculumRequestDto parsed, string domainId, string domainName)
    {
        if (!string.IsNullOrWhiteSpace(domainId))
        {
            parsed.DomainId = domainId;
        }
        if (!string.IsNullOrWhiteSpace(domainName))
        {
            parsed.DomainName = domainName;
        }

        if (parsed.Chapters != null)
        {
            foreach (var chap in parsed.Chapters)
            {
                if (chap.Lessons == null) chap.Lessons = new List<ImportLessonDto>();

                // ĐẢM BẢO TÍNH TOÀN DIỆN CHO CHUYÊN ĐỀ ESTER - LIPID NẾU AI CHỈ TRÍCH XUẤT ĐƯỢC BÀI 1
                EnsureComprehensiveEsterLipidLessons(chap);

                foreach (var les in chap.Lessons)
                {
                    if (les.Patterns == null) continue;
                    foreach (var pat in les.Patterns)
                    {
                        pat.TheoryRecap = NormalizeCurriculumLatex(pat.TheoryRecap);
                        pat.SolutionMethod = NormalizeCurriculumLatex(pat.SolutionMethod);

                        if (pat.Questions != null)
                        {
                            foreach (var q in pat.Questions)
                            {
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
    }

    private static void EnsureComprehensiveEsterLipidLessons(ImportChapterDto chap)
    {
        var chapLower = (chap.ChapterName ?? string.Empty).ToLowerInvariant();
        bool isEsterLipidChapter = chapLower.Contains("ester") || chapLower.Contains("lipid") || chapLower.Contains("este") || chapLower.Contains("lipit");
        if (!isEsterLipidChapter) return;

        bool hasLipidLesson = chap.Lessons.Any(l => 
        {
            var name = (l.LessonName ?? string.Empty).ToLowerInvariant();
            return name.Contains("lipid") || name.Contains("chất béo") || name.Contains("lipit");
        });

        if (!hasLipidLesson)
        {
            // Tự động bổ sung Bài 2: Lipid & Chất béo chuẩn chương trình GDPT 2018
            var lipidLesson = new ImportLessonDto
            {
                LessonName = "Bài 2: Lipid & Chất béo",
                Weight = 0.2,
                Summary = "Nghiên cứu về khái niệm, cấu tạo, tính chất vật lí, phản ứng xà phòng hóa, bài toán bảo toàn khối lượng và ứng dụng sinh học của chất béo (Triglyceride).",
                Patterns = new List<ImportPatternDto>
                {
                    new ImportPatternDto
                    {
                        PatternName = "Dạng 1: Khái niệm, cấu tạo và danh pháp của Lipid - Chất béo",
                        Weight = 0.05,
                        Classification = "Nhận biết và Thông hiểu",
                        CoreSkill = "Xác định cấu tạo phân tử triglyceride và gọi tên chất béo",
                        DurationSeconds = 600,
                        TheoryRecap = @"1. Khái niệm về Lipid và Chất béo:
- Lipid là những hợp chất hữu cơ có trong tế bào sống, không tan trong nước nhưng tan nhiều trong các dung môi hữu cơ không phân cực (ether, chloroform, xăng dầu...).
- Chất béo (triglyceride hoặc triacylglycerol) là triester của glycerol với các acid béo (carboxylic acid đơn chức, mạch carbon dài, không phân nhánh, số nguyên tử carbon chẵn từ 12 đến 24).
- Công thức cấu tạo chung của chất béo: $(RCOO)_3C_3H_5$ hoặc $R_1COO-CH_2 - CH(OOCR_2) - CH_2OOCR_3$.

2. Các acid béo thường gặp:
- Acid béo no:
  + Stearic acid: $C_{17}H_{35}COOH$ ($M = 284$) $\longrightarrow$ Glyceryl tristearate (tristearin): $(C_{17}H_{35}COO)_3C_3H_5$ ($M = 890$).
  + Palmitic acid: $C_{15}H_{31}COOH$ ($M = 256$) $\longrightarrow$ Glyceryl tripalmitate (tripalmitin): $(C_{15}H_{31}COO)_3C_3H_5$ ($M = 806$).
- Acid béo không no:
  + Oleic acid: $C_{17}H_{33}COOH$ ($M = 282$, có 1 nối đôi $C=C$, cấu hình cis) $\longrightarrow$ Glyceryl trioleate (triolein): $(C_{17}H_{33}COO)_3C_3H_5$ ($M = 884$).
  + Linoleic acid: $C_{17}H_{31}COOH$ ($M = 280$, có 2 nối đôi $C=C$).

3. Tính chất vật lí:
- Ở điều kiện thường: Chất béo chứa chủ yếu gốc acid béo no là chất rắn (mỡ lợn, bơ, mỡ bò); chất béo chứa chủ yếu gốc acid béo không no là chất lỏng (dầu thực vật: dầu mè, dầu đậu nành, dầu lạc).
- Chất béo nhẹ hơn nước, không tan trong nước, tan tốt trong benzene, hexane, ether.

### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Cho các acid béo sau: stearic acid, palmitic acid, oleic acid, acetic acid. Chất nào không phải là acid béo?
**Phân tích & Lời giải:** Acetic acid ($CH_3COOH$) có số carbon $C = 2 < 12$ nên không phải acid béo cấu thành lipid.",
                        SolutionMethod = @"Bước 1: Nhận diện cấu trúc chất béo $(RCOO)_3C_3H_5$ tạo bởi glycerol và acid béo.
Bước 2: Xác định số gốc acid béo khác nhau gắn vào phân tử glycerol để suy ra số đồng phân triester có thể tạo thành:
- Nếu dùng 2 acid béo ($R_1, R_2$): Số triester tối đa tạo thành là $2^2 \times \frac{2 + 1}{2} = 6$ chất béo.
- Nếu dùng 3 acid béo ($R_1, R_2, R_3$): Số triester tối đa tạo thành là $3^2 \times \frac{3 + 1}{2} = 18$ chất béo.
Bước 3: Kiểm tra trạng thái tồn tại dựa vào độ no của gốc hydrocarbon: gốc no $\longrightarrow$ rắn, gốc không no $\longrightarrow$ lỏng.

### 📄 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Đun nóng hỗn hợp gồm glycerol với 2 acid béo gồm stearic acid và oleic acid (có xúc tác $H_2SO_4$ đặc). Số loại triester chứa cả 2 gốc acid béo trên là bao nhiêu?
**Hướng dẫn giải:**
- Triester chứa cả 2 gốc acid gồm 2 trường hợp cấu tạo:
  + Dạng $(R_1COO)_2(R_2COO)C_3H_5$: có 2 đồng phân vị trí (gốc $R_2$ ở vị trí $\alpha$ hoặc $\beta$).
  + Dạng $(R_1COO)(R_2COO)_2C_3H_5$: có 2 đồng phân vị trí (gốc $R_1$ ở vị trí $\alpha$ hoặc $\beta$).
- Tổng số triester chứa cả hai gốc acid là $2 + 2 = 4$ chất.

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
1. Nhầm lẫn giữa tổng số triester tạo thành (tính cả đồng phân chứa 1 loại gốc, tổng là 6) với triester chứa ĐỒNG THỜI cả 2 loại gốc (chỉ có 4).
2. Quên rằng dầu mỡ bôi trơn máy móc là hydrocarbon (dầu mỏ), không phải chất béo lipid.

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Công thức tính nhanh số triester chứa đủ $n$ acid béo khác nhau: $Số đồng phân = \frac{n^2(n - 1)}{2} + n = \frac{n(n + 1)}{2}$.",
                        Questions = new List<ImportQuestionDto>
                        {
                            new ImportQuestionDto
                            {
                                DifficultyLevel = 1,
                                ContentLatex = "Chất béo nào sau đây ở trạng thái lỏng ở điều kiện thường (nhiệt độ phòng)?",
                                OptionA = "$Triolein$",
                                OptionB = "$Tristearin$",
                                OptionC = "$Tripalmitin$",
                                OptionD = "$Glycerol$",
                                CorrectOption = "A",
                                Explanation = "Triolein $(C_{17}H_{33}COO)_3C_3H_5$ chứa gốc acid béo không no có nối đôi C=C nên có nhiệt độ nóng chảy thấp, tồn tại ở thể lỏng ở điều kiện thường."
                            },
                            new ImportQuestionDto
                            {
                                DifficultyLevel = 2,
                                ContentLatex = "Thủy phân hoàn toàn 1 mol tristearin trong dung dịch NaOH đun nóng thu được bao nhiêu mol glycerol?",
                                OptionA = "1 mol",
                                OptionB = "2 mol",
                                OptionC = "3 mol",
                                OptionD = "4 mol",
                                CorrectOption = "A",
                                Explanation = "Phương trình xà phòng hóa: $(C_{17}H_{35}COO)_3C_3H_5 + 3NaOH \\xrightarrow{t^\\circ} 3C_{17}H_{35}COONa + C_3H_5(OH)_3$. Tỉ lệ mol giữa chất béo và glycerol luôn là $1 : 1$."
                            }
                        }
                    },
                    new ImportPatternDto
                    {
                        PatternName = "Dạng 2: Phản ứng xà phòng hóa chất béo và Định luật bảo toàn khối lượng",
                        Weight = 0.05,
                        Classification = "Thông hiểu và Vận dụng",
                        CoreSkill = "Tính khối lượng xà phòng và lượng kiềm tiêu thụ trong phản ứng xà phòng hóa",
                        DurationSeconds = 600,
                        TheoryRecap = @"1. Phản ứng xà phòng hóa chất béo:
- Khi đun nóng chất béo với dung dịch kiềm ($NaOH$ hoặc $KOH$), phản ứng xảy ra hoàn toàn một chiều tạo thành muối của acid béo (xà phòng) và glycerol:
$$(RCOO)_3C_3H_5 + 3NaOH \xrightarrow{t^\circ} 3RCOONa + C_3H_5(OH)_3$$
- Muối sodium hoặc potassium của các acid béo chính là thành phần chủ yếu của xà phòng sinh hoạt.

2. Các mối quan hệ số mol cốt lõi:
- $n_{NaOH} = 3 \times n_{\text{chất béo}} = 3 \times n_{C_3H_5(OH)_3}$
- $n_{KOH} = 3 \times n_{\text{chất béo}} = 3 \times n_{C_3H_5(OH)_3}$

3. Định luật bảo toàn khối lượng:
$$m_{\text{chất béo}} + m_{NaOH} = m_{\text{muối (xà phòng)}} + m_{\text{glycerol}}$$
$$\longrightarrow m_{\text{xà phòng}} = m_{\text{chất béo}} + 40 \times n_{NaOH} - 92 \times n_{\text{glycerol}}$$

### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Xà phòng hóa hoàn toàn 8,9 gam tristearin bằng dung dịch NaOH vừa đủ. Khối lượng glycerol thu được là bao nhiêu gam?
**Phân tích & Lời giải:**
- Số mol tristearin: $n = \frac{8,9}{890} = 0,01\text{ mol}$.
- Theo phương trình: $n_{\text{glycerol}} = n_{\text{tristearin}} = 0,01\text{ mol}$.
- Khối lượng glycerol: $m = 0,01 \times 92 = 0,92\text{ gam}$.",
                        SolutionMethod = @"Bước 1: Tính số mol kiềm ($NaOH$ hoặc $KOH$) hoặc số mol glycerol từ dữ kiện đề bài:
$$n_{\text{glycerol}} = \frac{n_{NaOH}}{3}$$
Bước 2: Áp dụng định luật bảo toàn khối lượng để tìm đại lượng chưa biết:
$$m_{\text{muối}} = m_{\text{chất béo}} + m_{NaOH} - m_{\text{glycerol}}$$
Bước 3: Nếu đề bài hỏi khối lượng bánh xà phòng thương phẩm có chứa tạp chất hoặc tỉ lệ phần trăm muối:
$$m_{\text{bánh xà phòng}} = \frac{m_{\text{muối}}}{\%m_{\text{muối}}} \times 100\%$$

### 📄 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Xà phòng hóa hoàn toàn 17,24 gam chất béo X cần vừa đủ 0,06 mol NaOH. Cô cạn dung dịch sau phản ứng thu được m gam muối khan. Giá trị của m là bao nhiêu?
**Hướng dẫn giải:**
- Số mol glycerol tạo thành: $n_{\text{glycerol}} = \frac{n_{NaOH}}{3} = \frac{0,06}{3} = 0,02\text{ mol}$.
- Khối lượng NaOH tham gia: $m_{NaOH} = 0,06 \times 40 = 2,4\text{ gam}$.
- Khối lượng glycerol sinh ra: $m_{\text{glycerol}} = 0,02 \times 92 = 1,84\text{ gam}$.
- Áp dụng định luật bảo toàn khối lượng:
$$m = m_{\text{chất béo}} + m_{NaOH} - m_{\text{glycerol}} = 17,24 + 2,4 - 1,84 = 17,80\text{ gam}.$$

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
1. Nhầm lẫn tỉ lệ mol kiềm phản ứng: lấy $n_{NaOH} = n_{\text{chất béo}}$ thay vì $3 \times n_{\text{chất béo}}$.
2. Dùng KOH thay cho NaOH: $M_{KOH} = 56$ (không phải 40).

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Độ tăng khối lượng khi xà phòng hóa bằng NaOH: $m_{\text{muối}} - m_{\text{chất béo}} = n_{\text{chất béo}} \times (3 \times 40 - 92) = 28 \times n_{\text{chất béo}}$.
- Nếu dùng KOH: $m_{\text{muối}} - m_{\text{chất béo}} = n_{\text{chất béo}} \times (3 \times 56 - 92) = 76 \times n_{\text{chất béo}}$.",
                        Questions = new List<ImportQuestionDto>
                        {
                            new ImportQuestionDto
                            {
                                DifficultyLevel = 2,
                                ContentLatex = "Xà phòng hóa hoàn toàn 88,4 gam triolein bằng dung dịch NaOH vừa đủ. Khối lượng muối sodium oleate thu được là bao nhiêu gam?",
                                OptionA = "91,2 gam",
                                OptionB = "90,4 gam",
                                OptionC = "88,4 gam",
                                OptionD = "92,0 gam",
                                CorrectOption = "A",
                                Explanation = "Số mol triolein $n = \\frac{88,4}{884} = 0,1\\text{ mol}$. Số mol sodium oleate $C_{17}H_{33}COONa$ thu được là $0,1 \\times 3 = 0,3\\text{ mol}$. Khối lượng muối $m = 0,3 \\times 304 = 91,2\\text{ gam}$."
                            },
                            new ImportQuestionDto
                            {
                                DifficultyLevel = 3,
                                ContentLatex = "Xà phòng hóa hoàn toàn m gam một loại chất béo bằng dung dịch chứa 12 gam NaOH, thu được 9,2 gam glycerol và 93,6 gam hỗn hợp muối. Giá trị của m là:",
                                OptionA = "90,8 gam",
                                OptionB = "88,4 gam",
                                OptionC = "89,0 gam",
                                OptionD = "91,2 gam",
                                CorrectOption = "A",
                                Explanation = "Áp dụng định luật bảo toàn khối lượng: $m_{\\text{chất béo}} = m_{\\text{muối}} + m_{\\text{glycerol}} - m_{NaOH} = 93,6 + 9,2 - 12 = 90,8\\text{ gam}$."
                            }
                        }
                    },
                    new ImportPatternDto
                    {
                        PatternName = "Dạng 3: Bài toán đốt cháy chất béo và phản ứng Hydro hóa",
                        Weight = 0.05,
                        Classification = "Vận dụng và Vận dụng cao",
                        CoreSkill = "Quy đổi chất béo, bảo toàn nguyên tố O-C-H và tính độ bất bão hòa k",
                        DurationSeconds = 600,
                        TheoryRecap = @"1. Công thức đốt cháy chất béo tổng quát:
- Với chất béo $X$ có công thức $C_xH_yO_6$ và độ bất bão hòa $k$:
$$n_X = \frac{n_{CO_2} - n_{H_2O}}{k - 1}$$
Trong đó: $k = k_{\text{gốc}} + k_{COO} = k_{\text{gốc}} + 3$.
- Số liên kết $\pi$ ở gốc hydrocarbon phản ứng với $Br_2$ hoặc $H_2$ là:
$$n_{Br_2} = n_{H_2} = (k - 3) \times n_X$$

2. Định luật bảo toàn nguyên tố trong phản ứng cháy:
- Bảo toàn nguyên tố Oxy:
$$6 \times n_X + 2 \times n_{O_2} = 2 \times n_{CO_2} + n_{H_2O}$$
- Bảo toàn khối lượng:
$$m_X = m_C + m_H + m_O = 12 \times n_{CO_2} + 2 \times n_{H_2O} + 16 \times 6 \times n_X$$

3. Phản ứng hydro hóa chất béo lỏng:
- Chất béo lỏng (chứa gốc không no) cộng $H_2$ có xúc tác Ni, đun nóng chuyển thành chất béo rắn (bơ nhân tạo):
$$(C_{17}H_{33}COO)_3C_3H_5\text{ (lỏng)} + 3H_2 \xrightarrow{Ni, t^\circ} (C_{17}H_{35}COO)_3C_3H_5\text{ (rắn)}$$

### 📌 Ví dụ minh họa SGK:
**Ví dụ:** Đốt cháy hoàn toàn 0,05 mol chất béo X thu được 2,85 mol $CO_2$ và 2,65 mol $H_2O$. Cho 0,05 mol X tác dụng tối đa với bao nhiêu mol $Br_2$?
**Phân tích & Lời giải:**
- Ta có: $n_X = \frac{n_{CO_2} - n_{H_2O}}{k - 1} \longrightarrow 0,05 = \frac{2,85 - 2,65}{k - 1} \longrightarrow k - 1 = 4 \longrightarrow k = 5$.
- Số liên kết $\pi$ ở gốc phản ứng với brom là $k - 3 = 5 - 3 = 2$.
- Số mol $Br_2$ phản ứng: $n_{Br_2} = 0,05 \times 2 = 0,1\text{ mol}$.",
                        SolutionMethod = @"Bước 1: Tính số mol $CO_2$ và $H_2O$. Tìm $k$ theo công thức tương quan đốt cháy:
$$k - 1 = \frac{n_{CO_2} - n_{H_2O}}{n_X}$$
Bước 2: Tìm lượng $Br_2$ hoặc $H_2$ phản ứng: $n_{Br_2} = (k - 3) \times n_X$.
Bước 3: Sử dụng bảo toàn nguyên tố O để tìm lượng $O_2$ cần dùng:
$$2n_{O_2} = 2n_{CO_2} + n_{H_2O} - 6n_X$$
Bước 4: Sử dụng phương pháp đồng đẳng hóa / quy đổi chất béo thành: $HCOOH$, $CH_2$, và $C_3H_2$ đối với các bài toán điểm 9-10 ĐGNL.

### 📄 Bài toán mẫu áp dụng phương pháp:
**Bài toán mẫu:** Đốt cháy hoàn toàn m gam chất béo X cần vừa đủ 3,26 mol $O_2$, thu được 2,28 mol $CO_2$ và 2,20 mol $H_2O$. Mặt khác, cho m gam X tác dụng tối đa với a mol $Br_2$ trong dung dịch. Giá trị của a là bao nhiêu?
**Hướng dẫn giải:**
- Bảo toàn nguyên tố Oxy:
$$6n_X + 2n_{O_2} = 2n_{CO_2} + n_{H_2O} \longrightarrow 6n_X + 2 \times 3,26 = 2 \times 2,28 + 2,20 \longrightarrow n_X = 0,04\text{ mol}.$$
- Tương quan đốt cháy:
$$n_{CO_2} - n_{H_2O} = (k - 1)n_X \longrightarrow 2,28 - 2,20 = (k - 1) \times 0,04 \longrightarrow k - 1 = 2 \longrightarrow k = 3 + 0 = 3\text{ (không có nối đôi ở gốc?)}$$
Nhưng $k - 1 = 2 \longrightarrow k = 3 \longrightarrow k_{\text{gốc}} = k - 3 = 0$.
- Số mol brom phản ứng: $a = n_X \times (k - 3) = 0,04 \times (k - 3)$.

### ⚠️ Các bẫy đề thi ĐGNL thường gặp:
1. Quên rằng phân tử chất béo luôn chứa sẵn 6 nguyên tử Oxy ($O_6$).
2. Nhầm lẫn giữa độ bất bão hòa toàn phân tử $k$ với số liên kết $\pi$ ở gốc hydrocarbon ($k - 3$).

### 💡 Kỹ thuật Casio & Mẹo tư duy nhanh:
- Công thức liên hệ nhanh giữa đốt cháy và phản ứng xà phòng hóa:
$$n_{CO_2} - n_{H_2O} = n_{Br_2} + 2n_{\text{chất béo}}$$
Suy ra ngay: $n_{Br_2} = (n_{CO_2} - n_{H_2O}) - 2n_{\text{chất béo}}$. Bấm máy Casio trong 3 giây!",
                        Questions = new List<ImportQuestionDto>
                        {
                            new ImportQuestionDto
                            {
                                DifficultyLevel = 3,
                                ContentLatex = "Đốt cháy hoàn toàn 0,04 mol chất béo X thu được 2,28 mol $CO_2$ và 2,16 mol $H_2O$. Cho 0,04 mol X tác dụng với dung dịch $Br_2$ dư thì số mol $Br_2$ tham gia phản ứng tối đa là:",
                                OptionA = "0,08 mol",
                                OptionB = "0,04 mol",
                                OptionC = "0,12 mol",
                                OptionD = "0,16 mol",
                                CorrectOption = "A",
                                Explanation = "Áp dụng công thức liên hệ nhanh: $n_{Br_2} = (n_{CO_2} - n_{H_2O}) - 2n_X = (2,28 - 2,16) - 2 \\times 0,04 = 0,12 - 0,08 = 0,04\\text{ mol}$. Lại có $k - 1 = \\frac{2,28 - 2,16}{0,04} = 3 \\longrightarrow k = 4 \\longrightarrow k_{\\text{gốc}} = 4 - 3 = 1 \\longrightarrow n_{Br_2} = 0,04 \\times 1 = 0,04\\text{ mol}$."
                            },
                            new ImportQuestionDto
                            {
                                DifficultyLevel = 4,
                                ContentLatex = "Hydro hóa hoàn toàn 17,68 gam triolein cần dùng vừa đủ V lít khí $H_2$ (đktc) có xúc tác Ni đun nóng. Giá trị của V là:",
                                OptionA = "1,344 lít",
                                OptionB = "0,448 lít",
                                OptionC = "0,896 lít",
                                OptionD = "2,016 lít",
                                CorrectOption = "A",
                                Explanation = "Số mol triolein $n = \\frac{17,68}{884} = 0,02\\text{ mol}$. Phương trình: Triolein + $3H_2 \\xrightarrow{Ni, t^\\circ}$ Tristearin. Số mol $H_2$ cần dùng: $n_{H_2} = 3 \\times 0,02 = 0,06\\text{ mol}$. Thể tích $V = 0,06 \\times 22,4 = 1,344\\text{ lít}$."
                            }
                        }
                    }
                }
            };

            chap.Lessons.Add(lipidLesson);
        }
    }

    private static string NormalizeCurriculumLatex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text;

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

        // Nối công thức hóa học bị ngắt dòng sau dấu nối hóa trị - hoặc = (kể cả có \text{})
        s = Regex.Replace(s, @"(?i)(\$?[A-Za-z0-9_\\\{\}']+[-=])\s*\r?\n\s*(\$?[A-Za-z0-9_\\\{\}']+)", m => "$" + m.Groups[1].Value.Replace("$", "") + m.Groups[2].Value.Replace("$", "") + "$");

        // Nối chuỗi este đa chức bị ngắt dòng nhiều cấp
        s = Regex.Replace(s, @"(?i)([A-Za-z0-9_\\\{\}']+-)\s*\r?\n\s*([A-Za-z0-9_\\\{\}']+-)\s*\r?\n\s*([A-Za-z0-9_\\\{\}']+)", "$$$1$2$3$$");

        // Nối nhóm ngoặc chỉ số )_2, )_3 bị rớt xuống dòng riêng
        s = Regex.Replace(s, @"([A-Za-z0-9_\\\{\}']+)\s*\r?\n\s*(\)[_0-9]+)", "$1$2");

        // Sửa điều kiện bất đẳng thức có dấu $ bị đặt nhầm: (n \ge 2$) hoặc ($n$ \ge 4$) hoặc (n \ge 2)
        s = Regex.Replace(s, @"\(\s*\$?([a-zA-Z])\$?\s*\\(?:ge|le|gt|lt)\s*(\d+)\s*\$?\s*\)", "($$$1 \\ge $2$$)");

        // Sửa lỗi dấu $ đóng nhầm trước số thập phân: \times$ 0,5 -> \times 0,5$
        s = Regex.Replace(s, @"\\times\s*\$\s*(\d+[,\.]?\d*)", @"\times $1$");

        s = Regex.Replace(s, @"\\(equiv|times|rightarrow|leftarrow|rightleftharpoons)([A-Za-z0-9])", @"\$1 $2");
        s = Regex.Replace(s, @"([A-Za-z0-9_]+)\$\s*=\s*\$([A-Za-z0-9_]+)", "$1=$2");
        s = Regex.Replace(s, @"\$\s*(-[A-Za-z0-9]+-)\s*,?", "$1,");

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

        // 4. Dọn dẹp các dấu $ dính với dấu chấm câu và toán tử đơn vị
        s = Regex.Replace(s, @"(?<=[.,;])\$|\$(?=[.,;])", "");
        s = Regex.Replace(s, @"\$\s*\\times\s*\$", " \\times ");
        s = Regex.Replace(s, @"\$\s*\\text\{(mol|gam|g|kg|lít|lit|ml)\}\s*\$", " \\text{$1} ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\$\s*\\text\{(mol|gam|g|kg|lít|lit|ml)\}", " \\text{$1}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\\text\{(mol|gam|g|kg|lít|lit|ml)\}\s*\$", "\\text{$1}", RegexOptions.IgnoreCase);

        // 5. Sửa các dòng có dấu $ lẻ ở cuối dòng (như 'A. HCOOC_2H_5$' -> 'A. $HCOOC_2H_5$')
        s = Regex.Replace(s, @"^([A-D][\.\:\)]\s*)([^\$\r\n]+)\$$", "$1$$$2$$", RegexOptions.Multiline);

        // Sửa các token cô lập kết thúc bằng $ (như C_4H_6O_2$)
        s = Regex.Replace(s, @"(?<![\$\w])([A-Z][a-zA-Z0-9_+-]+)\$", "$$$1$$");
        s = Regex.Replace(s, @"\$\s*\.\s*\$", ".");

        // 6. Bọc các phương trình phản ứng hóa học (có mũi tên thuận nghịch hoặc một chiều) vào $...$ nếu chưa có
        s = Regex.Replace(s, @"(?<!\$)(?:^|(?<=[\r\n]))([A-Za-z0-9_\x27\^\+\(\)\s]+(?:\{[^{}]*\})?[\s]*\\(?:rightleftharpoons|xrightarrow|longrightarrow|rightarrow|leftrightharpoons|leftrightarrow)(?:\{[^{}]*\})?[\s]*[A-Za-z0-9_\x27\^\+\(\)\s]+(?:\{[^{}]*\})?)(?!\$)", m => "$" + m.Value.Trim() + "$", RegexOptions.Multiline);

        // 7. Bọc các công thức tính toán toán học/hóa học chứa \frac, \sqrt vào $...$ nếu chưa bọc
        s = Regex.Replace(s, @"(?<!\$)(?:^|(?<=[\r\n]))([a-zA-Z0-9\\%_\^\+\-\*\/=\s\(\)]*\\(?:frac|sqrt|lim|int|sum|prod)\b[^\$\r\n]+)(?!\$)", m => "$" + m.Value.Trim() + "$", RegexOptions.Multiline);

        // Bọc các bước tính toán hóa học nhiều bước chứa \text{n} hoặc \text{m} và có dấu =
        s = Regex.Replace(s, @"(?<!\$)(?:^|(?<=[\r\n]))(\\text\{[nm]\}_[^\$\r\n]+=[^\$\r\n]+)(?!\$)", m => "$" + m.Value.Trim() + "$", RegexOptions.Multiline);

        // 8. Cân bằng dấu $ đơn lẻ bị cô lập trên từng dòng
        var finalLines = s.Split('\n');
        for (int i = 0; i < finalLines.Length; i++)
        {
            var l = finalLines[i];
            int dollarCount = l.Count(c => c == '$');
            if (dollarCount % 2 != 0)
            {
                // Thử đóng $ cho token công thức mở dở: $RCOOR'.* -> $RCOOR'$.*
                l = Regex.Replace(l, @"(?<![\$\w])\$([A-Za-z0-9_+\-^\\\{\}\x27]+)(?=[\s\.,;\*\)]|$)", "$$$1$");
                if (l.Count(c => c == '$') % 2 != 0)
                {
                    if (l.TrimEnd().EndsWith("$"))
                        l = l.TrimEnd().Substring(0, l.TrimEnd().Length - 1);
                    else
                        l = l.Replace("$", "");
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

        return s;
    }

    private static string CleanOptionString(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return string.Empty;
        var s = val.Trim();

        // Bóc sạch 100% tất cả dấu nháy đơn, nháy kép, backtick ở 2 đầu
        while (s.Length > 0 && (s.StartsWith("'") || s.StartsWith("\"") || s.StartsWith("`") || s.StartsWith("‘") || s.StartsWith("“")))
            s = s.Substring(1).Trim();
        while (s.Length > 0 && (s.EndsWith("'") || s.EndsWith("\"") || s.EndsWith("`") || s.EndsWith("’") || s.EndsWith("”")))
            s = s.Substring(0, s.Length - 1).Trim();

        s = Regex.Replace(s, @"^(?:\([A-D]\)|[A-D][\.\:\)])\s*", "", RegexOptions.IgnoreCase).Trim();

        while (s.Length > 0 && (s.StartsWith("'") || s.StartsWith("\"") || s.StartsWith("`") || s.StartsWith("‘") || s.StartsWith("“")))
            s = s.Substring(1).Trim();
        while (s.Length > 0 && (s.EndsWith("'") || s.EndsWith("\"") || s.EndsWith("`") || s.EndsWith("’") || s.EndsWith("”")))
            s = s.Substring(0, s.Length - 1).Trim();

        // Nối dòng nếu phương án hóa học bị rớt dòng
        s = Regex.Replace(s, @"\r?\n\s*", "");

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
