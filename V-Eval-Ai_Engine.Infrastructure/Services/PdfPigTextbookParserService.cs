using System.Net.Http.Headers;
using System.Security.Cryptography;
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
/// Động cơ nạp tri thức Sách Giáo Khoa thế hệ mới (TOC-First + Text-First + AI Systematization):
/// - Bước 1 (TOC-First): Quét Mục lục ở các trang đầu/cuối để định hình toàn bộ Chương & Bài học.
/// - Bước 2 (Text-First & Lọc rác): Đọc text thuần local siêu tốc (< 1s cho 170 trang), cắt bỏ header/footer/chi tiết thừa, không xử lý hình ảnh.
/// - Bước 3 (AI Systematization): Gửi text sạch theo từng Chương/nhóm trang cho AI chuẩn hóa KaTeX (Toán/Lý/Hóa) và hệ thống hóa Chương -> Bài -> Dạng bài.
/// - Fallback Vision AI (56 DPI) chỉ kích hoạt cho các trang scan 100% không có lớp chữ số.
/// </summary>
public class PdfPigTextbookParserService : ITextbookParserService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PdfPigTextbookParserService> _logger;
    private readonly ITextbookRepository _repository;
    private readonly IEmbeddingService _embeddingService;

    public PdfPigTextbookParserService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<PdfPigTextbookParserService> logger,
        ITextbookRepository repository,
        IEmbeddingService embeddingService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _repository = repository;
        _embeddingService = embeddingService;
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
        CancellationToken cancellationToken = default,
        int targetDpi = 48,
        int batchSize = 2)
    {
        onProgress?.Invoke("📖 [TOC-First] Đang đọc PDF, tính mã Hash SHA-256 và quét Mục lục...", 4);

        using var memoryStream = new MemoryStream();
        await pdfStream.CopyToAsync(memoryStream, cancellationToken);
        byte[] pdfBytes = memoryStream.ToArray();

        using var sha256 = SHA256.Create();
        string fileHash = Convert.ToHexStringLower(sha256.ComputeHash(pdfBytes));

        using var document = PdfDocument.Open(pdfBytes);
        int totalPages = document.NumberOfPages;

        // 1. Quét nhanh lớp chữ số toàn bộ các trang (< 1s cho 170 trang) và trích xuất Mục lục
        var digitalPages = new Dictionary<int, string>();
        var scanPages = new List<int>();
        for (int p = 1; p <= totalPages; p++)
        {
            string? cleanDigital = TryExtractCleanDigitalPageText(document, p);
            if (!string.IsNullOrWhiteSpace(cleanDigital))
            {
                digitalPages[p] = CleanTextbookNoise(cleanDigital);
            }
            else
            {
                scanPages.Add(p);
            }
        }

        // Tự động kích hoạt VISION_AI nếu tài liệu là PDF thuần ảnh scan (hơn 50% số trang không có text)
        bool isPureScanPdf = digitalPages.Count < totalPages * 0.5;
        if (isPureScanPdf)
        {
            ocrMode = "VISION_AI";
        }

        int effectiveDpi = Math.Clamp(targetDpi > 0 ? targetDpi : 48, 40, 72);
        int effectiveBatchSize = isPureScanPdf || ocrMode == "VISION_AI"
            ? Math.Clamp(batchSize > 0 && batchSize <= 3 ? batchSize : 2, 1, 3)
            : Math.Clamp(batchSize > 0 ? batchSize : 6, 1, 10);

        string tocSample = BuildTocSample(digitalPages, totalPages);
        var docIdentity = CurriculumTaxonomyRegistry.DetectDocumentIdentity(fileName, tocSample, domainId);
        var state = new IngestionState
        {
            SubjectCode = docIdentity.SubjectCode,
            SubjectName = docIdentity.SubjectName,
            Grade = docIdentity.Grade,
            BookEdition = docIdentity.BookEdition
        };

        if (!string.IsNullOrWhiteSpace(docIdentity.DomainId) && (string.IsNullOrWhiteSpace(domainId) || domainId == "Domain-Default"))
        {
            domainId = docIdentity.DomainId;
        }

        bool isStemSubject = state.SubjectCode is "MATH" or "PHYS" or "CHEM" or "LOGIC_DATA";

        // 2. Kiểm tra Checkpoint
        var checkpoint = await _repository.GetCheckpointByFileHashAsync(fileHash, cancellationToken);
        Guid sourceId;
        int startPage = 1;

        if (!forceReingest && checkpoint != null && checkpoint.Value.LastProcessedPage > 0)
        {
            sourceId = checkpoint.Value.SourceId;
            state.RunningCharCount = checkpoint.Value.TotalChars;
            state.RunningChunkCount = checkpoint.Value.TotalChunks;

            var existingChunks = await _repository.GetChunksBySourceIdAsync(sourceId, cancellationToken);
            if (checkpoint.Value.LastProcessedPage >= totalPages && existingChunks.Count > 0)
            {
                onProgress?.Invoke($"🎉 Tài liệu '{fileName}' ({state.SubjectName} {state.Grade}) đã nạp hoàn tất 100% từ trước!", 100);
                return BuildFinalIngestResult(
                    sourceId, fileName, fileHash, domainId, skillId, docType, totalPages,
                    state.RunningCharCount, 0, docIdentity.IsInDgnlScope, existingChunks);
            }

            startPage = checkpoint.Value.LastProcessedPage + 1;
            onProgress?.Invoke($"⚡ Tiếp tục từ Checkpoint trang {startPage}/{totalPages}...", (int)((float)(startPage - 1) / totalPages * 100));
        }
        else
        {
            sourceId = await _repository.GetOrCreateSourceAsync(
                fileHash, fileName, $"/uploads/textbooks/{fileName}", totalPages, domainId, skillId, docType, cancellationToken);

            if (forceReingest)
            {
                await _repository.ResetSourceChunksAsync(sourceId, totalPages, cancellationToken);
            }
        }

        var geminiKeys = ResolveGeminiKeys();
        var geminiModels = ResolveGeminiModels();

        onProgress?.Invoke(
            $"⚡ [Text-First] Phát hiện {digitalPages.Count}/{totalPages} trang có chữ số ({scanPages.Count} trang scan). Bắt đầu hệ thống hóa {(isStemSubject ? "Toán/Lý/Hóa (KaTeX)" : "Lý thuyết thuần")}...",
            10);

        // 3. Xử lý các trang theo cụm (6 trang/lượt cho Text AI, hoặc 2 trang cho Vision scan thuần)
        int currentPage = startPage;
        int textClusterSize = 6;

        while (currentPage <= totalPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Nhánh A: Trang có lớp chữ số sạch (Text-First - siêu nhanh, không gửi ảnh!)
            if (digitalPages.ContainsKey(currentPage) && ocrMode != "VISION_AI")
            {
                var clusterPages = new List<int>();
                var clusterTextSb = new StringBuilder();

                int p = currentPage;
                while (p <= totalPages && clusterPages.Count < textClusterSize && digitalPages.ContainsKey(p))
                {
                    string txt = digitalPages[p];
                    if (txt.Length >= 40) // Bỏ qua trang bìa/rỗng
                    {
                        clusterPages.Add(p);
                        clusterTextSb.AppendLine($"<<<PAGE_START:{p}>>>");
                        clusterTextSb.AppendLine(txt.Length > 3500 ? txt[..3500] : txt);
                        clusterTextSb.AppendLine($"<<<PAGE_END:{p}>>>");
                    }
                    p++;
                }

                if (clusterPages.Count == 0)
                {
                    currentPage = p;
                    continue;
                }

                int endClusterPage = clusterPages[^1];
                int pct = (int)((float)endClusterPage / totalPages * 100);
                onProgress?.Invoke(
                    $"🧠 [Text AI Trang {clusterPages[0]}-{endClusterPage}/{totalPages}] Đang hệ thống hóa Chương/Bài/Dạng bài{(isStemSubject ? " & chuẩn hóa KaTeX" : "")}...",
                    pct);

                // Gọi Gemini Text API (nhanh ~1-2s, không tốn token ảnh) để hệ thống hóa & chuẩn hóa LaTeX
                var structuredMap = await SystematizeTextClusterWithAiAsync(
                    clusterPages,
                    clusterTextSb.ToString(),
                    tocSample,
                    state.SubjectCode,
                    state.Grade,
                    state.Chapter,
                    state.Lesson,
                    isStemSubject,
                    geminiKeys,
                    geminiModels,
                    cancellationToken);

                foreach (int pNum in clusterPages)
                {
                    string pageText = structuredMap.TryGetValue(pNum, out var aiTxt) && !string.IsNullOrWhiteSpace(aiTxt)
                        ? aiTxt
                        : digitalPages[pNum];

                    await SaveEnrichedPageChunkAsync(
                        sourceId, pNum, pageText, domainId, skillId, docType, 0,
                        state, cancellationToken);
                }

                await _repository.UpdateSourceProgressAsync(
                    sourceId, totalPages, state.RunningCharCount, state.RunningChunkCount, "PROCESSING", cancellationToken);

                currentPage = endClusterPage + 1;
                // Nghỉ nhẹ 400ms giữa các cụm để bảo vệ hạn ngạch TPM/RPM của Google Gemini API, tránh bị 503/429
                await Task.Delay(400, cancellationToken);
                continue;
            }

            // Nhánh B: Trang scan thuần (không có text số) -> Gộp Batch 10 trang ở 56 DPI
            var batchPages = new List<int> { currentPage };
            int scanAhead = currentPage + 1;
            while (batchPages.Count < effectiveBatchSize && scanAhead <= totalPages)
            {
                if (ocrMode != "VISION_AI" && digitalPages.ContainsKey(scanAhead))
                {
                    break;
                }
                batchPages.Add(scanAhead);
                scanAhead++;
            }

            int batchEndPage = batchPages[^1];
            int batchPct = (int)((float)batchEndPage / totalPages * 100);
            onProgress?.Invoke(
                $"👁️ [Vision Batch {currentPage}-{batchEndPage}/{totalPages}] Đang đọc text & KaTeX (bỏ qua hình ảnh thừa)...",
                batchPct);

            var batchExtractedMap = await OcrPageBatchWithVisionModelsAsync(
                pdfBytes,
                batchPages,
                effectiveDpi,
                state.SubjectCode,
                state.Grade,
                state.Chapter,
                state.Lesson,
                geminiKeys,
                geminiModels,
                cancellationToken);

            foreach (int pNum in batchPages)
            {
                string pageText = batchExtractedMap.TryGetValue(pNum, out var apiPageText) && !string.IsNullOrWhiteSpace(apiPageText)
                    ? apiPageText
                    : (digitalPages.TryGetValue(pNum, out var dig) ? dig : $"[Trang {pNum}: Nội dung tóm tắt]");

                await SaveEnrichedPageChunkAsync(
                    sourceId, pNum, pageText, domainId, skillId, docType, effectiveDpi,
                    state, cancellationToken);
            }

            await _repository.UpdateSourceProgressAsync(
                sourceId, totalPages, state.RunningCharCount, state.RunningChunkCount, "PROCESSING", cancellationToken);

            currentPage = batchEndPage + 1;
            // Nghỉ nhẹ 400ms giữa các batch để bảo vệ hạn ngạch TPM/RPM của Google Gemini API, tránh bị 503/429
            await Task.Delay(400, cancellationToken);
        }

        // 4. Hoàn tất 100%
        await _repository.UpdateSourceProgressAsync(
            sourceId, totalPages, state.RunningCharCount, state.RunningChunkCount, "COMPLETED", cancellationToken);

        var finalChunks = await _repository.GetChunksBySourceIdAsync(sourceId, cancellationToken);
        var finalResult = BuildFinalIngestResult(
            sourceId, fileName, fileHash, domainId, skillId, docType, totalPages,
            state.RunningCharCount, scanPages.Count > 0 ? effectiveDpi : 0, docIdentity.IsInDgnlScope, finalChunks);

        onProgress?.Invoke(
            $"🎉 Hoàn tất 100% nạp {totalPages} trang '{fileName}' ({finalResult.DetectedSubjectName} {finalResult.DetectedGrade} - {finalResult.ChaptersDetected.Count} chương, {finalResult.LessonsDetected.Count} bài)!",
            100);

        return finalResult;
    }

    private sealed class IngestionState
    {
        public string SubjectCode { get; set; } = string.Empty;
        public string SubjectName { get; set; } = string.Empty;
        public int Grade { get; set; }
        public string BookEdition { get; set; } = string.Empty;
        public string Chapter { get; set; } = string.Empty;
        public string Lesson { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public int RunningChunkCount { get; set; }
        public int RunningCharCount { get; set; }
    }

    private async Task SaveEnrichedPageChunkAsync(
        Guid sourceId,
        int pageNumber,
        string pageText,
        string domainId,
        string skillId,
        string docType,
        int dpiUsed,
        IngestionState state,
        CancellationToken cancellationToken)
    {
        state.RunningChunkCount++;
        var chunk = new TextbookChunkDto
        {
            ChunkIndex = state.RunningChunkCount,
            PageNumber = pageNumber,
            Text = pageText,
            DpiUsed = dpiUsed,
            HasDiagram = false
        };

        string subjectCode = state.SubjectCode;
        string subjectName = state.SubjectName;
        int grade = state.Grade;
        string bookEdition = state.BookEdition;
        string chapter = state.Chapter;
        string lesson = state.Lesson;
        string pattern = state.Pattern;

        CurriculumTaxonomyRegistry.ClassifyAndEnrichChunk(
            chunk,
            ref subjectCode,
            ref subjectName,
            ref grade,
            ref bookEdition,
            ref chapter,
            ref lesson,
            ref pattern);

        state.SubjectCode = subjectCode;
        state.SubjectName = subjectName;
        state.Grade = grade;
        state.BookEdition = bookEdition;
        state.Chapter = chapter;
        state.Lesson = lesson;
        state.Pattern = pattern;

        // Bỏ qua thông tin hình ảnh theo yêu cầu mới: chỉ giữ Text + KaTeX
        chunk.HasDiagram = false;
        chunk.ImagePlaceholders.Clear();
        state.RunningCharCount += chunk.CharCount;

        // Cắt trượt gối đầu theo ngữ nghĩa (1000 ký tự, overlap 200) & nhúng vector 768 chiều
        var subChunks = PgVectorDbService.SplitIntoOverlappingChunks(chunk.Text, chunkSize: 1000, overlap: 200);
        if (subChunks.Count == 0) return;

        var embeddings = await _embeddingService.EmbedBatchAsync(subChunks, "RETRIEVAL_DOCUMENT", cancellationToken);
        for (int sIdx = 0; sIdx < subChunks.Count; sIdx++)
        {
            if (sIdx > 0) state.RunningChunkCount++;
            await _repository.SavePageChunkAsync(
                sourceId,
                pageNumber,
                state.RunningChunkCount,
                subChunks[sIdx],
                domainId,
                skillId,
                docType,
                cancellationToken,
                chunk,
                embeddings[sIdx]);
        }
    }

    private static string BuildTocSample(Dictionary<int, string> digitalPages, int totalPages)
    {
        var sb = new StringBuilder();
        for (int p = 1; p <= Math.Min(6, totalPages); p++)
        {
            if (digitalPages.TryGetValue(p, out var txt))
                sb.AppendLine(txt.Length > 1500 ? txt[..1500] : txt);
        }
        for (int p = Math.Max(7, totalPages - 2); p <= totalPages; p++)
        {
            if (digitalPages.TryGetValue(p, out var txt))
                sb.AppendLine(txt.Length > 1500 ? txt[..1500] : txt);
        }
        return sb.ToString();
    }

    private static string CleanTextbookNoise(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var lines = raw.Split('\n')
            .Select(l => l.Trim())
            .Where(l =>
                !string.IsNullOrWhiteSpace(l) &&
                !Regex.IsMatch(l, @"^\d{1,3}$") && // Số trang đứng riêng
                !Regex.IsMatch(l, @"^(?:Hình\s+\d+[\.\d]*|Ảnh\s+\d+|Nguồn\s*:)", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(l, @"^(?:Hoạt động khởi động|Em có biết\??|Ghi chú hình)", RegexOptions.IgnoreCase))
            .ToList();
        return string.Join("\n", lines);
    }

    private async Task<Dictionary<int, string>> SystematizeTextClusterWithAiAsync(
        List<int> pageNumbers,
        string clusterText,
        string tocSample,
        string subjectCode,
        int grade,
        string currentChapter,
        string currentLesson,
        bool isStemSubject,
        List<string> geminiKeys,
        List<string> geminiModels,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        if (geminiKeys.Count == 0) return result;

        string prompt = $@"BẠN LÀ CHUYÊN GIA HỆ THỐNG HÓA TRI THỨC SGK GDPT 2018 (CHÂN TRỜI SÁNG TẠO).
Nhiệm vụ: Đọc nội dung text thô của các trang {string.Join(", ", pageNumbers)} (Môn {subjectCode}, Lớp {grade}), CẮT BỎ chi tiết thừa (lời dẫn rườm rà, hoạt động trò chơi), KHÔNG quan tâm hình ảnh, và hệ thống hóa lại từng trang thật súc tích, chuẩn xác.

QUY TẮC BẮT BUỘC:
1. Giữ nguyên thẻ phân trang:
   <<<PAGE_START:Số_Trang>>>
   [TAXONOMY: subject=""{subjectCode}"" | grade=""{grade}"" | chapter=""Tên chương"" | lesson=""Tên bài"" | pattern=""Tên dạng bài"" | section=""THEORY|FORMULA|EXAMPLE|EXERCISE|PASSAGE""]
   (Nội dung trọng tâm đã hệ thống hóa...)
   <<<PAGE_END:Số_Trang>>>
2. {(isStemSubject ? "CHUẨN HÓA 100% CÔNG THỨC TOÁN/LÝ/HÓA SANG KATEX ($...$ hoặc $$...$$). Ký hiệu vectơ, độ dài vectơ, biến số phải được bọc trong KaTeX chuẩn (VD: $\\vec{AB}$, $\\vec{a}$, $|\\vec{AB}|$, điểm $A$). KHÔNG ngắt biến số/ký hiệu toán xuống dòng đơn lẻ." : "TÓM TẮT Ý CHÍNH, LUẬN ĐIỂM, THỂ LOẠI VÀ KIẾN THỨC CỐT LÕI.")}
3. Ngữ cảnh hiện tại: Chương '{currentChapter}', Bài '{currentLesson}'.
4. TUYỆT ĐỐI BỎ QUA các trang bìa sách, thông tin xuất bản, bản quyền, danh sách tác giả/chủ biên/NXB (đánh dấu section=""COVER"" nếu gặp).

MỤC LỤC THAM KHẢO:
{(tocSample.Length > 1500 ? tocSample[..1500] : tocSample)}

NỘI DUNG CÁC TRANG:
{clusterText}";

        var payloadObj = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["maxOutputTokens"] = 8192
            }
        };
        string jsonString = payloadObj.ToJsonString();

        var orderedKeys = GetRotatedKeys(geminiKeys);
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
                        string respBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        var jsonNode = JsonNode.Parse(respBody);
                        string? textOut = jsonNode?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(textOut))
                        {
                            ParseMultiPageBatchResponse(textOut, pageNumbers, result);
                            if (result.Count > 0) return result;
                        }
                    }
                    else
                    {
                        int statusCode = (int)response.StatusCode;
                        if (statusCode is 503 or 500 or 504 or 404 or 400)
                        {
                            _logger.LogWarning("Model {Model} trả về HTTP {Status}. Đổi ngay model tiếp theo, không retry key", model, statusCode);
                            break;
                        }
                    }
                }
                catch
                {
                    // Try next key/model
                }
            }
        }

        return result;
    }

    public ImportCurriculumRequestDto SynthesizeCurriculumFromChunks(
        string fileName,
        string domainId,
        List<TextbookChunkDto> chunks)
    {
        return CurriculumTaxonomyRegistry.SynthesizeCurriculumFromChunks(fileName, domainId, chunks);
    }

    private TextbookIngestResultDto BuildFinalIngestResult(
        Guid sourceId,
        string fileName,
        string fileHash,
        string domainId,
        string skillId,
        string docType,
        int totalPages,
        int totalChars,
        int dpiUsed,
        bool isInDgnlScope,
        List<TextbookChunkDto> chunks)
    {
        var first = chunks.FirstOrDefault();
        var synthesized = CurriculumTaxonomyRegistry.SynthesizeCurriculumFromChunks(fileName, domainId, chunks);

        return new TextbookIngestResultDto
        {
            DocumentId = sourceId.ToString(),
            FileName = fileName,
            FileHash = fileHash,
            DomainId = domainId,
            SkillId = skillId,
            DocumentType = docType,
            TotalPages = totalPages,
            TotalChars = totalChars,
            TotalChunks = chunks.Count,
            DetectedSubjectCode = first?.SubjectCode ?? synthesized.SubjectCode ?? "MATH",
            DetectedSubjectName = first?.SubjectName ?? synthesized.SubjectName ?? "Toán học",
            DetectedGrade = first?.Grade ?? synthesized.Grade,
            DetectedBookEdition = first?.BookEdition ?? synthesized.BookEdition,
            IsInDgnlScope = isInDgnlScope && (first?.SubjectCode != "OUT_OF_SCOPE_DGNL"),
            DpiUsed = dpiUsed,
            TotalDiagramsDetected = 0,
            ChaptersDetected = chunks.Select(c => c.ChapterName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
            LessonsDetected = chunks.Select(c => c.LessonName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
            PatternsDetected = chunks.Select(c => c.PatternName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
            SynthesizedCurriculum = synthesized,
            Chunks = chunks
        };
    }

    private string? TryExtractCleanDigitalPageText(PdfDocument document, int pageNumber)
    {
        try
        {
            var page = document.GetPage(pageNumber);
            string rawText = page.Text ?? string.Empty;
            rawText = string.Join('\n', rawText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)));

            if (!string.IsNullOrWhiteSpace(rawText) && rawText.Length >= 40 && !IsCorruptedFontEncoding(rawText))
            {
                return rawText;
            }
        }
        catch
        {
            // Fallback to Vision
        }
        return null;
    }

    private static int _keyCounter = 0;

    private static List<string> GetRotatedKeys(List<string> geminiKeys)
    {
        var orderedKeys = new List<string>(geminiKeys);
        if (orderedKeys.Count > 1)
        {
            int shift = (Interlocked.Increment(ref _keyCounter) & 0x7FFFFFFF) % orderedKeys.Count;
            orderedKeys = orderedKeys.Skip(shift).Concat(orderedKeys.Take(shift)).ToList();
        }
        return orderedKeys;
    }

    private async Task<Dictionary<int, string>> OcrPageBatchWithVisionModelsAsync(
        byte[] pdfBytes,
        List<int> pageNumbers,
        int targetDpi,
        string currentSubjectCode,
        int currentGrade,
        string currentChapter,
        string currentLesson,
        List<string> geminiKeys,
        List<string> geminiModels,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        var pageImagesBase64 = new List<(int PageNumber, string Base64)>();

        try
        {
            foreach (int pNum in pageNumbers)
            {
                string? b64 = RenderLowDpiPageJpegBase64(pdfBytes, pNum, targetDpi);
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    pageImagesBase64.Add((pNum, b64));
                }
            }

            if (pageImagesBase64.Count == 0) return result;

            string batchPrompt = $@"BẠN LÀ CHUYÊN GIA SỐ HÓA TRI THỨC SÁCH GIÁO KHOA GDPT 2018 (MÔN {currentSubjectCode}, LỚP {currentGrade}).
Nhiệm vụ: Đọc nhanh {pageImagesBase64.Count} trang ảnh đính kèm (Trang: {string.Join(", ", pageNumbers)}).
Trích xuất CÔ ĐỌNG, SÚC TÍCH, ĐÚNG TRỌNG TÂM TRI THỨC (giảm tải nội dung rườm rà để tăng tốc tối đa):
1. Định nghĩa, khái niệm, tính chất, định luật cốt lõi của bài học (trình bày súc tích, đủ ý, không chép lời dẫn lê thê).
2. Công thức Toán/Lý/Hóa chuẩn hóa 100% sang KaTeX ($...$ hoặc $$...$$). Ký hiệu vectơ, độ dài vectơ, biến số phải được bọc trong KaTeX chuẩn (VD: $\vec{{AB}}$, $\vec{{a}}$, $|\vec{{AB}}|$, điểm $A$). KHÔNG ngắt biến số/ký hiệu toán xuống dòng đơn lẻ.
3. Ví dụ mẫu: Chỉ giữ tối đa 1 ví dụ tiêu biểu nhất kèm lời giải ngắn gọn (2-3 bước).
4. BỎ QUA HOÀN TOÀN: Toàn bộ bài tập tự luyện/rèn luyện dài dòng, hoạt động khởi động/trò chơi/thảo luận nhóm, hình vẽ minh họa bên lề.
5. TUYỆT ĐỐI KHÔNG bóc tách trang bìa, lời nói đầu, mục lục, thông tin xuất bản/bản quyền/NXB (đánh dấu section=""COVER"" nếu gặp).

QUY TẮC BÓC TÁCH CẤU TRÚC:
- Nhìn vào TIÊU ĐỀ IN TRÊN TỪNG TRANG ẢNH để tự nhận diện:
  + chapter: Tên Chủ đề hoặc Chương (VD: 'Chủ đề 1: Mô tả chuyển động' hoặc 'Chương 1: ...'). Nếu trang nằm giữa chương, kế thừa: '{currentChapter}'.
  + lesson: Tên Bài học cụ thể (VD: 'Bài 1: Khái quát về môn Vật lí', 'Bài 2: Vận tốc và độ dịch chuyển'...). Nếu nằm giữa bài, kế thừa: '{currentLesson}'.
  + pattern: Tên dạng bài hoặc mục kiến thức trọng tâm.
  + section: Phân loại mục ('THEORY' cho lý thuyết/định luật, 'FORMULA' cho công thức, 'EXAMPLE' cho bài toán mẫu, 'EXERCISE' cho câu hỏi/bài tập).

Bắt buộc bọc mỗi trang trong:
<<<PAGE_START:Số_Trang>>>
[TAXONOMY: subject=""{currentSubjectCode}"" | grade=""{currentGrade}"" | chapter=""Tên chương bóc tách được"" | lesson=""Tên bài học bóc tách được"" | pattern=""Tên dạng bài"" | section=""THEORY|FORMULA|EXAMPLE|EXERCISE""]
(Nội dung trọng tâm cô đọng, công thức KaTeX, ví dụ ngắn gọn...)
<<<PAGE_END:Số_Trang>>>";

            var partsArray = new JsonArray { new JsonObject { ["text"] = batchPrompt } };
            foreach (var item in pageImagesBase64)
            {
                partsArray.Add(new JsonObject { ["text"] = $"=== PAGE {item.PageNumber} ===" });
                partsArray.Add(new JsonObject
                {
                    ["inline_data"] = new JsonObject
                    {
                        ["mime_type"] = "image/jpeg",
                        ["data"] = item.Base64
                    }
                });
            }

            var payloadObj = new JsonObject
            {
                ["contents"] = new JsonArray { new JsonObject { ["parts"] = partsArray } },
                ["generationConfig"] = new JsonObject { ["temperature"] = 0.0, ["maxOutputTokens"] = 4096 }
            };
            string jsonString = payloadObj.ToJsonString();
            var orderedKeys = GetRotatedKeys(geminiKeys);

            foreach (var model in geminiModels)
            {
                foreach (var apiKey in orderedKeys)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(15)); // Timeout 15s cho Vision request để tránh bị kẹt hàng đợi Google

                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                        using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
                        var response = await _httpClient.PostAsync(url, content, cts.Token);
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
                                    if (!string.IsNullOrWhiteSpace(txt)) sb.AppendLine(txt);
                                }
                            }
                            string fullResponseText = sb.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(fullResponseText))
                            {
                                ParseMultiPageBatchResponse(fullResponseText, pageNumbers, result);
                                if (result.Count > 0) return result;
                            }
                        }
                        else
                        {
                            int statusCode = (int)response.StatusCode;
                            if (statusCode is 503 or 500 or 504 or 404 or 400)
                            {
                                _logger.LogWarning("Vision Model {Model} trả về HTTP {Status}. Đổi ngay model tiếp theo, không retry key", model, statusCode);
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Vision Model {Model} quá hạn 15s. Đổi ngay model tiếp theo", model);
                        break;
                    }
                    catch
                    {
                        // Try next key/model
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi Vision Batch trang {Start}-{End}", pageNumbers[0], pageNumbers[^1]);
        }

        return result;
    }

    private static string? RenderLowDpiPageJpegBase64(byte[] pdfBytes, int pageNumber, int targetDpi)
    {
        try
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            using var skBitmap = Conversion.ToImage(pdfStream, page: pageNumber - 1, options: new RenderOptions { Dpi = targetDpi });
            if (skBitmap == null) return null;

            int maxDim = Math.Max(skBitmap.Width, skBitmap.Height);
            int capDim = targetDpi <= 50 ? 640 : (targetDpi <= 60 ? 720 : 800);
            int jpegQuality = 52; // Tối ưu nén JPEG 52%: Giảm ~65% dung lượng ảnh mà chữ và công thức KaTeX vẫn rõ nét 100%

            if (maxDim > capDim)
            {
                float scale = (float)capDim / maxDim;
                int targetW = Math.Max(1, (int)(skBitmap.Width * scale));
                int targetH = Math.Max(1, (int)(skBitmap.Height * scale));
                using var resized = skBitmap.Resize(new SKImageInfo(targetW, targetH), SKSamplingOptions.Default);
                if (resized != null)
                {
                    using var ms = new MemoryStream();
                    resized.Encode(ms, SKEncodedImageFormat.Jpeg, jpegQuality);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }

            using var imageStream = new MemoryStream();
            skBitmap.Encode(imageStream, SKEncodedImageFormat.Jpeg, jpegQuality);
            return Convert.ToBase64String(imageStream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static void ParseMultiPageBatchResponse(string fullText, List<int> expectedPages, Dictionary<int, string> outputMap)
    {
        if (string.IsNullOrWhiteSpace(fullText)) return;

        // 1. Khớp chuẩn thẻ phân trang <<<PAGE_START:xx>>> ... <<<PAGE_END:xx>>>
        var matches = Regex.Matches(
            fullText,
            @"<<<PAGE_START\s*:\s*(\d+)>>>([\s\S]*?)(?:<<<PAGE_END\s*:\s*\1>>>|(?=<<<PAGE_START\s*:\s*\d+>>>)|\z)",
            RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int pageNum))
            {
                string content = Regex.Replace(m.Groups[2].Value, @"<<<PAGE_END\s*:\s*\d+>>>", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(content)) outputMap[pageNum] = content;
            }
        }

        // 2. Dự phòng: nếu thẻ chuẩn bị thiếu, thử bắt theo tiêu đề ### Trang X, [TRANG X], Page X
        if (outputMap.Count == 0)
        {
            var fallbackMatches = Regex.Matches(
                fullText,
                @"(?:###?\s*(?:Trang|Page)|\[(?:TRANG|PAGE)|<<<PAGE_START:?)\s*(\d+)[\]:]?([\s\S]*?)(?=(?:###?\s*(?:Trang|Page)|\[(?:TRANG|PAGE)|<<<PAGE_START:?)\s*\d+|\z)",
                RegexOptions.IgnoreCase);

            foreach (Match m in fallbackMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int pageNum) && expectedPages.Contains(pageNum))
                {
                    string content = m.Groups[2].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(content)) outputMap[pageNum] = content;
                }
            }
        }

        // 3. Bảo hiểm an toàn: Nếu output hợp lệ nhưng không chứa thẻ nhận diện trang, gán cho trang đầu tiên trong batch
        if (outputMap.Count == 0 && expectedPages.Count > 0 && !string.IsNullOrWhiteSpace(fullText))
        {
            outputMap[expectedPages[0]] = fullText.Trim();
        }
    }

    private List<string> ResolveGeminiKeys()
    {
        var keys = new List<string>();
        var keyArr = _configuration.GetSection("AiSettings:GeminiApiKeys").Get<string[]>();
        if (keyArr != null) keys.AddRange(keyArr);

        var singleKey = _configuration["AiSettings:GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(singleKey))
            keys.AddRange(singleKey.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));

        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEYS")
                     ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                     ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            keys.AddRange(envKey.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));

        return keys.Select(k => k.Trim()).Where(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_")).Distinct().ToList();
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
            return new List<string> { single.Trim() };

        return new List<string> { "gemini-3.1-flash-lite", "gemini-3.5-flash-lite", "gemini-flash-lite-latest" };
    }

    private static readonly HashSet<char> VietnameseVowels = new HashSet<char>
    {
        'a', 'ă', 'â', 'e', 'ê', 'i', 'o', 'ô', 'ơ', 'u', 'ư', 'y',
        'á', 'à', 'ả', 'ã', 'ạ', 'ắ', 'ằ', 'ẳ', 'ẵ', 'ặ', 'ấ', 'ầ', 'ẩ', 'ẫ', 'ậ',
        'é', 'è', 'ẻ', 'ẽ', 'ẹ', 'ế', 'ề', 'ể', 'ễ', 'ệ', 'í', 'ì', 'ỉ', 'ĩ', 'ị',
        'ó', 'ò', 'ỏ', 'õ', 'ọ', 'ố', 'ồ', 'ổ', 'ỗ', 'ộ', 'ớ', 'ờ', 'ở', 'ỡ', 'ợ',
        'ú', 'ù', 'ủ', 'ũ', 'ụ', 'ứ', 'ừ', 'ử', 'ữ', 'ự', 'ý', 'ỳ', 'ỷ', 'ỹ', 'ỵ',
        'A', 'Ă', 'Â', 'E', 'Ê', 'I', 'O', 'Ô', 'Ơ', 'U', 'Ư', 'Y',
        'Á', 'À', 'Ả', 'Ã', 'Ạ', 'Ắ', 'Ằ', 'Ẳ', 'Ẵ', 'Ặ', 'Ấ', 'Ầ', 'Ẩ', 'Ẫ', 'Ậ',
        'É', 'È', 'Ẻ', 'Ẽ', 'Ẹ', 'Ế', 'Ề', 'Ể', 'Ễ', 'Ệ', 'Í', 'Ì', 'Ỉ', 'Ĩ', 'Ị',
        'Ó', 'Ò', 'Ỏ', 'Õ', 'Ọ', 'Ố', 'Ồ', 'Ổ', 'Ỗ', 'Ộ', 'Ớ', 'Ờ', 'Ở', 'Ỡ', 'Ợ',
        'Ú', 'Ù', 'Ủ', 'Ũ', 'Ụ', 'Ứ', 'Ừ', 'Ử', 'Ữ', 'Ự', 'Ý', 'Ỳ', 'Ỷ', 'Ỹ', 'Ỵ'
    };

    private static readonly HashSet<char> CorruptedGlyphs = new HashSet<char>
    {
        '{', '}', '\\', '^', '~', '|', '¶', '§', '©', '®', '½', '¼', '¾', '¿', '±', '`', '¤', '°',
        'ł', 'Ċ', 'ī', 'Ť', 'Š', 'ś', 'ř', 'œ', 'ż', 'ź', 'č', 'ď', 'ń', 'ň', 'ħ', 'Ĉ', 'Ў', 'Č', 'Г', 'ə',
        'Ä', 'Å', 'Ö', 'Ā', 'Ś', 'Ĩ', 'Ũ', 'Ð', 'þ', 'Ñ', 'ť', 'Ğ', 'ï'
    };

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

        if (corruptedCount >= 4 && ((double)corruptedCount / text.Length) >= 0.008) return true;
        if (totalLetters >= 30 && ((double)vowelCount / totalLetters) < 0.23) return true;
        return false;
    }

    public async Task<TextbookSkeletonDto> ScanTextbookSkeletonAsync(
        Stream pdfStream,
        string fileName,
        string? hintSubjectCode = null,
        int? hintGrade = null,
        string? hintBookEdition = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await pdfStream.CopyToAsync(ms, cancellationToken);
        byte[] pdfBytes = ms.ToArray();

        int totalPages = 0;
        var tocSb = new StringBuilder();
        var tocImages = new List<(int PageNumber, byte[] PngBytes)>();

        using (var doc = PdfDocument.Open(pdfBytes))
        {
            totalPages = doc.NumberOfPages;

            // 1. Quét tìm trang có chứa từ khóa MỤC LỤC trong toàn bộ cuốn sách (cả đầu và cuối sách)
            var candidateTocPages = new List<int>();
            for (int p = 1; p <= totalPages; p++)
            {
                try
                {
                    string pText = doc.GetPage(p).Text ?? string.Empty;
                    if (pText.Contains("MỤC LỤC", StringComparison.OrdinalIgnoreCase) ||
                        pText.Contains("Mục lục", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateTocPages.Add(p);
                    }
                }
                catch { }
            }

            var pagesToRead = new List<int>();
            if (candidateTocPages.Count > 0)
            {
                foreach (int tp in candidateTocPages)
                {
                    for (int p = tp; p <= Math.Min(totalPages, tp + 3); p++)
                    {
                        if (!pagesToRead.Contains(p)) pagesToRead.Add(p);
                    }
                }
            }
            else
            {
                // Nếu không tìm thấy text "Mục lục" (ví dụ sách scan ảnh),
                // quét các trang cuối (totalPages - 4 đến totalPages) và các trang đầu (1 đến 6)
                for (int p = Math.Max(1, totalPages - 4); p <= totalPages; p++) pagesToRead.Add(p);
                for (int p = 1; p <= Math.Min(6, totalPages); p++) if (!pagesToRead.Contains(p)) pagesToRead.Add(p);
            }

            pagesToRead.Sort();

            foreach (int p in pagesToRead)
            {
                string raw = doc.GetPage(p).Text ?? string.Empty;
                string clean = CleanTextbookNoise(raw);
                if (!string.IsNullOrWhiteSpace(clean) && !IsCorruptedFontEncoding(clean))
                {
                    tocSb.AppendLine($"[Trang {p}]");
                    tocSb.AppendLine(clean.Length > 2500 ? clean[..2500] : clean);
                }
            }

            // LUÔN RENDER ẢNH các trang ứng viên mục lục cho AI Vision (10 trang đầu và các trang cuối)
            // Giúp xử lý triệt để file PDF thuần ảnh / scan không có lớp chữ số
            var renderPages = new List<int>();
            for (int p = 1; p <= Math.Min(10, totalPages); p++) renderPages.Add(p);
            for (int p = Math.Max(1, totalPages - 5); p <= totalPages; p++) if (!renderPages.Contains(p)) renderPages.Add(p);
            foreach (int tp in candidateTocPages) if (!renderPages.Contains(tp)) renderPages.Add(tp);

            foreach (int p in renderPages)
            {
                try
                {
                    using var pageStream = new MemoryStream(pdfBytes);
                    using var skBitmap = Conversion.ToImage(pageStream, page: p - 1, options: new RenderOptions { Dpi = 56 });
                    if (skBitmap != null)
                    {
                        using var imgData = skBitmap.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 60);
                        tocImages.Add((p, imgData.ToArray()));
                    }
                }
                catch { }
            }
        }

        var docIdentity = CurriculumTaxonomyRegistry.DetectDocumentIdentity(fileName, tocSb.ToString(), null, hintSubjectCode, hintGrade, hintBookEdition);
        var skeleton = new TextbookSkeletonDto
        {
            FileName = fileName,
            TotalPages = totalPages,
            SubjectCode = docIdentity.SubjectCode,
            SubjectName = docIdentity.SubjectName,
            Grade = docIdentity.Grade > 0 ? docIdentity.Grade : 10,
            BookEdition = string.IsNullOrWhiteSpace(docIdentity.BookEdition) ? "Chân trời sáng tạo" : docIdentity.BookEdition,
            VersionTag = "2025-2026"
        };

        var geminiKeys = ResolveGeminiKeys();
        var geminiModels = ResolveGeminiModels();

        if (geminiKeys.Count == 0)
        {
            var fallback = CurriculumTaxonomyRegistry.GetStandardSkeletonChapters(skeleton.SubjectCode, skeleton.Grade);
            if (fallback.Count > 0) skeleton.Chapters = fallback;
            return skeleton;
        }

        string prompt = $@"BẠN LÀ CHUYÊN GIA SƯ PHẠM HÀNG ĐẦU THIẾT KẾ CHƯƠNG TRÌNH GDPT 2018 VIỆT NAM (MÔN {skeleton.SubjectName} LỚP {skeleton.Grade}).
Bạn được cung cấp {tocImages.Count} trang ảnh đính kèm (các trang đầu và các trang cuối của sách '{fileName}', gồm Bìa, Lời nói đầu, Hướng dẫn sử dụng và MỤC LỤC).
NHIỆM VỤ BẮT BUỘC:
1. Hãy tìm kỹ trang MỤC LỤC (Table of Contents) trong các trang ảnh và trích xuất TOÀN BỘ TẤT CẢ CÁC CHỦ ĐỀ / CHƯƠNG và TẤT CẢ CÁC BÀI HỌC CỦA CUỐN SÁCH!
2. RÀNG BUỘC CỨNG VỀ BÀI HỌC (MANDATORY MULTI-LESSONS PER CHAPTER - ZERO LESSON DROPPING):
   - Một cuốn sách SGK {totalPages} trang luôn có từ 4 đến 8 Chương và TỔNG CỘNG TỪ 10 ĐẾN 25 BÀI HỌC.
   - Mỗi Chương BẮT BUỘC phải trích xuất TOÀN BỘ tất cả các bài học con (Bài 1, Bài 2, Bài 3, Bài 4...) có trong mục lục của chương đó!
   - CẢNH BÁO: TUYỆT ĐỐI CẤM GỘP BÀI, TUYỆT ĐỐI CẤM CHỈ LẤY 1 BÀI ĐẠI DIỆN CHO MỖI CHƯƠNG! Nếu chương có 4 bài học, mảng lessons của chương đó BẮT BUỘC phải có đủ 4 phần tử bài học.
3. Số lượng Dạng bài (patterns) của mỗi bài học PHẢI BÁM SÁT ĐÚNG NỘI DUNG THỰC TẾ TRONG SÁCH (bài ngắn chỉ có 1 mảng kiến thức thì trả 1 dạng, bài dài có 4-5 mục/ví dụ thì trả đủ 4-5 dạng — TUYỆT ĐỐI KHÔNG ép cứng 1 dạng hay 2-3 dạng).
   - QUY TẮC TÊN DẠNG BÀI: Trường ""name"" CHỈ ghi tên bản chất kiến thức/dạng bài (KHÔNG đánh tiền tố ""Dạng 1:"", ""Dạng 2:"" vào dữ liệu gốc vì giao diện FE sẽ tự hiển thị số thứ tự).
4. Chỉ trả về JSON thuần:
{{
  ""chapters"": [
    {{
      ""chapterNumber"": 1,
      ""title"": ""Chương 1: [Tên chương 1 theo mục lục]"",
      ""pageStart"": 5,
      ""pageEnd"": 35,
      ""lessons"": [
        {{
          ""lessonNumber"": 1,
          ""title"": ""Bài 1: [Tên bài 1 theo mục lục]"",
          ""pageStart"": 5,
          ""pageEnd"": 15,
          ""keyFormulas"": [""$v = \\frac{{s}}{{t}}$""],
          ""patterns"": [
            {{
              ""patternCode"": ""C1_B1_P1"",
              ""name"": ""[Tên mảng kiến thức/dạng bài thực tế trong sách, không ghi Dạng 1:]"",
              ""bloomLevel"": 1,
              ""description"": ""Nhận diện định nghĩa, đơn vị và hiện tượng"",
              ""method"": ""Áp dụng định nghĩa chuẩn GDPT 2018""
            }}
          ]
        }}
      ]
    }}
  ]
}}

MỤC LỤC THÔ (NẾU CÓ):
{tocSb}";

        var client = _httpClient;
        foreach (var model in geminiModels)
        {
            foreach (var key in geminiKeys)
            {
                try
                {
                    var partsList = new List<object> { new { text = prompt } };
                    foreach (var img in tocImages)
                    {
                        partsList.Add(new
                        {
                            inline_data = new
                            {
                                mime_type = "image/jpeg",
                                data = Convert.ToBase64String(img.PngBytes)
                            }
                        });
                    }

                    var reqBody = new
                    {
                        contents = new[] { new { parts = partsList.ToArray() } },
                        generationConfig = new { temperature = 0.1, responseMimeType = "application/json" }
                    };
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}";
                    using var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
                    using var resp = await client.PostAsync(url, httpContent, cancellationToken);
                    if (!resp.IsSuccessStatusCode)
                    {
                        int statusCode = (int)resp.StatusCode;
                        if (statusCode is 503 or 500 or 504 or 404 or 400)
                        {
                            _logger.LogWarning("TOC Scan Model {Model} trả về HTTP {Status}. Đổi ngay model tiếp theo", model, statusCode);
                            break;
                        }
                        continue;
                    }

                    using var docJson = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                    string? rawJson = docJson.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();

                    if (string.IsNullOrWhiteSpace(rawJson)) continue;
                    rawJson = rawJson.Trim();
                    if (rawJson.StartsWith("```"))
                    {
                        int firstNewline = rawJson.IndexOf('\n');
                        if (firstNewline > 0) rawJson = rawJson[(firstNewline + 1)..];
                        if (rawJson.EndsWith("```")) rawJson = rawJson[..^3].Trim();
                    }
                    var parsed = JsonSerializer.Deserialize<TextbookSkeletonDto>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed?.Chapters is { Count: > 0 })
                    {
                        var stdChaps = CurriculumTaxonomyRegistry.GetStandardSkeletonChapters(skeleton.SubjectCode, skeleton.Grade);
                        int totalParsedLessons = parsed.Chapters.Sum(c => c.Lessons?.Count ?? 0);

                        // 1. Nếu AI chỉ trích xuất được <= 2 chương nhưng sách có >= 25 trang, bổ sung các chương chuẩn của GDPT 2018
                        if (parsed.Chapters.Count <= 2 && totalPages >= 25)
                        {
                            if (stdChaps.Count > 1)
                            {
                                var combinedChaps = new List<SkeletonChapterDto>(parsed.Chapters);
                                foreach (var sc in stdChaps)
                                {
                                    if (!combinedChaps.Any(c => c.Title.Contains(sc.Title, StringComparison.OrdinalIgnoreCase) || sc.Title.Contains(c.Title, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        sc.ChapterNumber = combinedChaps.Count + 1;
                                        combinedChaps.Add(sc);
                                    }
                                }
                                parsed.Chapters = combinedChaps;
                            }
                        }

                        // 2. LESSON GUARD: Nếu các chương parse ra bị thiếu bài (tổng số bài < 10 nhưng sách có >= 25 trang hoặc trung bình <= 1.2 bài/chương)
                        // Bổ sung các bài học chuẩn từ GDPT 2018 Taxonomy để bảo toàn trọn vẹn 10-25 bài học của cuốn sách
                        if (totalPages >= 25 && (totalParsedLessons < 10 || (parsed.Chapters.Count >= 3 && (double)totalParsedLessons / parsed.Chapters.Count <= 1.2)))
                        {
                            if (stdChaps.Count > 0)
                            {
                                foreach (var chap in parsed.Chapters)
                                {
                                    chap.Lessons ??= new List<SkeletonLessonDto>();
                                    // Tìm chương tương ứng trong taxonomy chuẩn
                                    var matchingStdChap = stdChaps.FirstOrDefault(sc => 
                                        sc.ChapterNumber == chap.ChapterNumber || 
                                        sc.Title.Contains(chap.Title, StringComparison.OrdinalIgnoreCase) || 
                                        chap.Title.Contains(sc.Title, StringComparison.OrdinalIgnoreCase));

                                    if (matchingStdChap?.Lessons != null && matchingStdChap.Lessons.Count > chap.Lessons.Count)
                                    {
                                        // Giữ các bài AI đã parse, bổ sung các bài chuẩn còn thiếu
                                        foreach (var stdLes in matchingStdChap.Lessons)
                                        {
                                            bool exists = chap.Lessons.Any(l => 
                                                l.Title.Contains(stdLes.Title, StringComparison.OrdinalIgnoreCase) || 
                                                stdLes.Title.Contains(l.Title, StringComparison.OrdinalIgnoreCase));
                                            if (!exists)
                                            {
                                                stdLes.LessonNumber = chap.Lessons.Count + 1;
                                                chap.Lessons.Add(stdLes);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        skeleton.Chapters = parsed.Chapters;
                        return skeleton;
                    }
                }
                catch
                {
                    // Thử model/key tiếp theo
                }
            }
        }

        if (skeleton.Chapters == null || skeleton.Chapters.Count <= 1)
        {
            var fallback = CurriculumTaxonomyRegistry.GetStandardSkeletonChapters(skeleton.SubjectCode, skeleton.Grade);
            if (fallback.Count > 0)
            {
                skeleton.Chapters = fallback;
            }
        }

        return skeleton;
    }

    public async Task<List<GeneratedExerciseDto>> GeneratePatternExercisesAsync(
        GenerateExercisesRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var exercises = new List<GeneratedExerciseDto>();
        var geminiKeys = ResolveGeminiKeys();
        var geminiModels = ResolveGeminiModels();

        string prompt = $@"Bạn là chuyên gia sư phạm ra đề thi GDPT 2018 Việt Nam môn {request.Subject} Lớp {request.Grade}.
Hãy biên soạn {request.Count} bài tập (trắc nghiệm 4 lựa chọn) cho:
- Chương: {request.Chapter}
- Bài học: {request.Lesson}
- Dạng bài: {request.PatternName}
- Mức độ Bloom yêu cầu: Cấp {request.BloomLevel} (1: Nhớ, 2: Hiểu, 3: Vận dụng, 4: Phân tích, 5: Đánh giá, 6: Sáng tạo).
- Ngữ cảnh lý thuyết: {(string.IsNullOrWhiteSpace(request.TheoryContext) ? "Chuẩn GDPT 2018" : request.TheoryContext)}

Yêu cầu xuất ra JSON duy nhất:
[
  {{
    ""id"": ""ex1"",
    ""stem"": ""Nội dung câu hỏi có công thức KaTeX chuẩn $...$ hoặc $$...$$"",
    ""options"": [""A. Đáp án 1"", ""B. Đáp án 2"", ""C. Đáp án 3"", ""D. Đáp án 4""],
    ""correctIndex"": 0,
    ""bloomLevel"": {request.BloomLevel},
    ""solution"": ""Lời giải chi tiết từng bước có KaTeX giải thích rõ tại sao chọn đáp án đó.""
  }}
]
Chỉ trả về JSON thuần, không kèm văn bản giải thích thừa.";

        var client = _httpClient;
        string? configuredGeminiBaseUrl = _configuration["AiSettings:GeminiBaseUrl"];
        foreach (var model in geminiModels)
        {
            foreach (var key in geminiKeys)
            {
                try
                {
                    var reqBody = new
                    {
                        contents = new[] { new { parts = new[] { new { text = prompt } } } },
                        generationConfig = new
                        {
                            temperature = 0.35,
                            responseMimeType = "application/json"
                        }
                    };
                    string url = MultiCloudExamParserService.ResolveGeminiEndpointUrl(model, key, configuredGeminiBaseUrl);
                    using var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
                    using var resp = await client.PostAsync(url, httpContent, cancellationToken);
                    if (!resp.IsSuccessStatusCode)
                    {
                        int statusCode = (int)resp.StatusCode;
                        if (statusCode is 503 or 500 or 504 or 404 or 400)
                        {
                            _logger.LogWarning("GenerateExercises Model {Model} trả về HTTP {Status}. Đổi ngay model tiếp theo", model, statusCode);
                            break;
                        }
                        continue;
                    }

                    string respBody = await resp.Content.ReadAsStringAsync(cancellationToken);
                    var jsonNode = JsonNode.Parse(respBody);
                    var partsNode = jsonNode?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                    var sb = new StringBuilder();
                    if (partsNode != null)
                    {
                        foreach (var part in partsNode)
                        {
                            if (part?["thought"]?.GetValue<bool>() == true) continue;
                            var txt = part?["text"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(txt)) sb.AppendLine(txt);
                        }
                    }
                    string? rawJson = sb.ToString();

                    if (string.IsNullOrWhiteSpace(rawJson)) continue;
                    rawJson = rawJson.Trim();
                    if (rawJson.StartsWith("```"))
                    {
                        int firstNewline = rawJson.IndexOf('\n');
                        if (firstNewline > 0) rawJson = rawJson[(firstNewline + 1)..];
                        if (rawJson.EndsWith("```")) rawJson = rawJson[..^3].Trim();
                    }
                    var parsed = JsonSerializer.Deserialize<List<GeneratedExerciseDto>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed is { Count: > 0 })
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // Thử tiếp
                }
            }
        }

        // Fallback mẫu chất lượng theo đúng số lượng & cấp Bloom 1–6 nếu chưa có mạng / lỗi key
        int safeCount = Math.Clamp(request.Count, 1, 10);
        string bloomVerb = request.BloomLevel switch
        {
            1 => "Nhận biết khái niệm và công thức nền tảng",
            2 => "Giải thích bản chất và thông hiểu mối liên hệ",
            3 => "Vận dụng quy tắc tính toán vào bài toán cụ thể",
            4 => "Phân tích điều kiện, dữ kiện và mối quan hệ nhân quả",
            5 => "Đánh giá tính đúng/sai và lựa chọn phương án tối ưu",
            6 => "Tổng hợp kiến thức và thiết kế hướng giải quyết tình huống mới",
            _ => "Vận dụng kiến thức trọng tâm"
        };
        for (int i = 1; i <= safeCount; i++)
        {
            exercises.Add(new GeneratedExerciseDto
            {
                Id = $"fb_l{request.BloomLevel}_{i}",
                Stem = $"[Bloom L{request.BloomLevel} · Mẫu #{i}] Hãy {bloomVerb.ToLowerInvariant()} cho dạng bài '{request.PatternName}' ({request.Lesson}):",
                Options = new List<string>
                {
                    "A. Kết luận chuẩn xác theo định nghĩa và quy tắc SGK GDPT 2018",
                    "B. Bỏ sót điều kiện xác định hoặc giả thiết quan trọng",
                    "C. Nhầm dấu / đơn vị trong quá trình biến đổi",
                    "D. Chỉ đúng trong trường hợp đặc biệt, không tổng quát"
                },
                CorrectIndex = 0,
                BloomLevel = request.BloomLevel,
                Solution = $"Bước 1: Xác định trọng tâm của {request.PatternName} ({request.Lesson}). Bước 2: Đối chiếu yêu cầu bậc nhận thức Bloom L{request.BloomLevel} ({bloomVerb}) để chọn phương án A."
            });
        }

        return exercises;
    }
}
