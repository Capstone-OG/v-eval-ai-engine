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
   - `questions`: MỖI DẠNG BÀI CÓ TỪ 2 ĐẾN 3 CÂU HỎI TRẮC NGHIỆM ĐẠI DIỆN CÁC CẤP ĐỘ BLOOM (L1/L2 -> L3/L4):
     * Câu 1: [L1 - Nhận biết / L2 - Thông hiểu] Câu hỏi lý thuyết, khái niệm hoặc ví dụ áp dụng trực tiếp.
     * Câu 2: [L3 - Vận dụng / L4 - Vận dụng cao] Bài toán vận dụng tổng hợp, thực tế hoặc phân loại học sinh (kèm hình 3D/2D nếu là hình học/hàm số).
     Mỗi câu gồm: difficultyLevel (1-4), contentLatex, optionA, optionB, optionC, optionD, correctOption ('A'|'B'|'C'|'D'), explanation (giải súc tích).
3. BẮT BUỘC DỰNG MÔ HÌNH HÌNH HỌC 3D, BẢNG BIẾN THIÊN & ĐỒ THỊ 2D TƯƠNG TÁC (TEXT-TO-MATHLAB RECONSTRUCTION):
   - ĐẶC BIỆT QUAN TRỌNG: KỂ CẢ KHI TÀI LIỆU/ĐỀ BÀI CHỈ MÔ TẢ HÌNH HỌC, HÀM SỐ HOẶC BẢNG BIẾN THIÊN BẰNG CHỮ THUẦN TÚY (hoàn toàn không có hình ảnh sẵn trong file gốc):
     BẠN BẮT BUỘC ĐÓNG VAI TRÒ MÁY DỰNG TOÁN HỌC, tự động tính toán và nhúng các khối thẻ cấu trúc [MATH_GRAPH_3D], [MATH_GRAPH] hoặc [VARIATION_TABLE] vào CẢ 2 NƠI:
     + Ở PHẦN BÀI GIẢNG: Nhúng trực tiếp bên dưới Ví dụ minh họa SGK (Khối 1 `theoryRecap`) và Bài toán mẫu áp dụng (Khối 2 `solutionMethod`).
     + Ở PHẦN CÂU HỎI: Nhúng trực tiếp ở cuối nội dung câu hỏi `contentLatex` (để học sinh quan sát xoay 360 độ hoặc đọc đồ thị giải bài) và trong Lời giải chi tiết `explanation`.
   
   - [A] ĐỐI VỚI HÌNH HỌC KHÔNG GIAN 3D [MATH_GRAPH_3D]:
     [MATH_GRAPH_3D]
     title: Cho tứ diện ABCD
     shapeType: tetrahedron
     dimensions: h=4,w=4,d=3.5,r=3,planeD=1.8
     vertices: A:A(1.3,1.2,4);B:B(0,0,0);C:C(4,0,0);D:D(2,3.5,0)
     extraPoints: M:name=M:edge=A,B:ratio=0.5:color=#ec4899;N:name=N:edge=A,C:ratio=0.5:color=#ec4899
     auxLines: M-N:dash=0:color=#ef4444;B-C:dash=0:color=#0f172a
     [/MATH_GRAPH_3D]
     (Các shapeType: tetrahedron [tứ diện ABCD], pyramid_rect_sa [chóp SA vuông góc đáy], pyramid_tri_sa [chóp đáy tam giác SA vuông góc], pyramid_regular_quad [chóp tứ giác đều], pyramid_regular_tri [chóp tam giác đều], prism_tri [lăng trụ tam giác], box_cube [khối hộp/lập phương]).

   - [B] ĐỐI VỚI BẢNG BIẾN THIÊN [VARIATION_TABLE] (Chuẩn SGK GDPT 2018):
     Khi đề bài hoặc bài giảng nói về Bảng biến thiên (tính đơn điệu, cực trị, GTLN/GTNN, tiệm cận):
     (LƯU Ý: Nếu đã dùng [VARIATION_TABLE] thì TUYỆT ĐỐI KHÔNG sinh thêm [MATH_GRAPH]! Mỗi thuộc tính BẮT BUỘC xuống dòng rõ ràng):
     * Hàm đa thức liên tục (bậc ba, trùng phương bậc bốn, bậc hai):
     [VARIATION_TABLE]
     title: Bảng biến thiên hàm số y = f(x)
     x: -\infty | x_1 | x_2 | +\infty
     y': + | 0 | - | 0 | +
     y: -\infty | y_max | y_min | +\infty
     dir: up | down | up
     [/VARIATION_TABLE]
     * Hàm phân thức hữu tỉ có tiệm cận đứng / vạch kép (||):
     [VARIATION_TABLE]
     title: Bảng biến thiên hàm số phân thức y = \\frac{{ax+b}}{{cx+d}}
     x: -\infty | x_0 | +\infty
     y': - | || | -
     y: y_asymp | -\infty || +\infty | y_asymp
     dir: down || down
     [/VARIATION_TABLE]

   - [C] ĐỐI VỚI ĐỒ THỊ HÀM SỐ 2D [MATH_GRAPH]:
     (Chỉ dùng khi đề bài yêu cầu vẽ hoặc nhận dạng ĐỒ THỊ hình vẽ cụ thể, KHÔNG dùng chung với [VARIATION_TABLE]):
     [MATH_GRAPH]
     title: Đồ thị hàm số y = f(x)
     formula: y = x^3 - 3*x + 1
     type: cubic
     params: a=1,b=0,c=-3,d=1
     [/MATH_GRAPH]
     (Các type hợp lệ: cubic [bậc ba y=ax^3+bx^2+cx+d, params: a,b,c,d], quartic [bậc bốn trùng phương y=ax^4+bx^2+c, params: a,b,c], rational [phân thức bậc 1/1 y=(ax+b)/(cx+d), params: a,b,c,d], rational_21 [phân thức bậc 2/1 y=(ax^2+bx+c)/(dx+e), params: a,b,c,d,e], quadratic [parabol y=ax^2+bx+c, params: a,b,c], custom [biểu thức tùy ý expr=...]).
4. ĐẦU RA JSON BẮT BUỘC:
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
              ""theoryRecap"": ""[Định lý & Công thức SGK]\n\n### 📌 Ví dụ minh họa SGK:\n**Ví dụ:** Cho hình chóp S.ABCD có đáy là hình vuông, SA vuông góc đáy...\n**Phân tích & Lời giải:** ...\n\n[MATH_GRAPH_3D]\ntitle: Cho hình chóp S.ABCD\nshapeType: pyramid_rect_sa\ndimensions: h=4,w=4,d=3.5,r=3,planeD=1.8\nvertices: S:S(0,0,4);A:A(0,0,0);B:B(4,0,0);C:C(4,3.5,0);D:D(0,3.5,0)\nextraPoints: M:name=M:edge=S,C:ratio=0.5:color=#ec4899\nauxLines: M-A:dash=2:color=#ef4444\n[/MATH_GRAPH_3D]"",
              ""solutionMethod"": ""Bước 1: ...\nBước 2: ...\n\n{sampleHeading}\n**{sampleKey}** Cho tứ diện ABCD...\n**Hướng dẫn giải:** ...\n\n[MATH_GRAPH_3D]\ntitle: Cho tứ diện ABCD\nshapeType: tetrahedron\ndimensions: h=4,w=4,d=3.5,r=3,planeD=1.8\nvertices: A:A(1.3,1.2,4);B:B(0,0,0);C:C(4,0,0);D:D(2,3.5,0)\nextraPoints: M:name=M:edge=A,B:ratio=0.5:color=#ec4899\nauxLines: M-C:dash=0:color=#ef4444\n[/MATH_GRAPH_3D]\n\n### ⚠️ Các bẫy đề thi:\n...\n\n{tipHeading}\n..."",
              ""durationSeconds"": 600,
              ""questions"": [
                {{
                  ""difficultyLevel"": 1,
                  ""contentLatex"": ""[Câu 1 - Nhận biết] Cho hình chóp S.ABCD..."",
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
                  ""contentLatex"": ""[Câu 3 - Vận dụng] Cho hình chóp S.ABCD có đáy là hình thang...\n\n[MATH_GRAPH_3D]\ntitle: Cho hình chóp S.ABCD\nshapeType: pyramid_rect_sa\ndimensions: h=4,w=4,d=3.5,r=3,planeD=1.8\nvertices: S:S(0,0,4);A:A(0,0,0);B:B(4,0,0);C:C(4,3.5,0);D:D(0,3.5,0)\nextraPoints: M:name=M:edge=C,D:ratio=0.5:color=#ec4899\nauxLines: B-M:dash=2:color=#ef4444;A-C:dash=2:color=#0f172a\n[/MATH_GRAPH_3D]"",
                  ""optionA"": ""..."",
                  ""optionB"": ""..."",
                  ""optionC"": ""..."",
                  ""optionD"": ""..."",
                  ""correctOption"": ""C"",
                  ""explanation"": ""Lời giải chi tiết từng bước: Giao tuyến là SI.\n\n[MATH_GRAPH_3D]\ntitle: Thiết diện S.ABCD\nshapeType: pyramid_rect_sa\ndimensions: h=4,w=4,d=3.5,r=3,planeD=1.8\nvertices: S:S(0,0,4);A:A(0,0,0);B:B(4,0,0);C:C(4,3.5,0);D:D(0,3.5,0)\nextraPoints: M:name=M:edge=C,D:ratio=0.5:color=#ec4899;I:name=I:edge=A,C:ratio=0.5:color=#ef4444\nauxLines: S-I:dash=0:color=#ef4444;B-M:dash=2:color=#0f172a\n[/MATH_GRAPH_3D]""
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
1. TRÍCH XUẤT 100% DỮ LIỆU THẬT & TỰ ĐỘNG DỰNG HÌNH 3D/2D/BẢNG BIẾN THIÊN TỪ CHỮ: Toàn bộ Chương, Bài học, Dạng bài, Ví dụ và Câu hỏi trắc nghiệm phải được trích xuất THỰC TẾ từ tài liệu được cung cấp ở trên (TUYỆT ĐỐI KHÔNG HARDCODE MẪU CỐ ĐỊNH). ĐẶC BIỆT: Đối với các bài toán hình học không gian (chóp, tứ diện, lăng trụ, hộp), đồ thị hàm số hoặc bảng biến thiên được mô tả bằng chữ trong tài liệu (kể cả khi file PDF gốc không có hình vẽ in sẵn), BẮT BUỘC đóng vai trò máy dựng hình, tự động tính toán tọa độ và nhúng thẻ [MATH_GRAPH_3D], [MATH_GRAPH] hoặc [VARIATION_TABLE] vào ví dụ SGK (Khối 1), bài toán mẫu (Khối 2) và câu hỏi trắc nghiệm (Khối 4)!
2. ESCAPE LATEX TRONG JSON: Mọi ký tự gạch chéo ngược LaTeX BẮT BUỘC phải dùng hai gạch chéo ngược (double backslash): \\frac, \\sqrt, \\Delta, \\{{, \\}}, \\%, \\$, \\alpha, \\beta, \\times, \\text... Tuyệt đối không dùng 1 gạch chéo ngược đơn độc.
3. QUY TẮC DẤU NGOẶC KÉP TRÍCH DẪN: TUYỆT ĐỐI KHÔNG dùng dấu ngoặc kép thẳng "" bên trong nội dung văn bản (đề bài, lý thuyết, giải thích, phương án A-D). Nếu cần trích dẫn thuật ngữ, tên chất hay công thức, BẮT BUỘC dùng dấu nháy đơn '...' (ví dụ: 'chất khử', 'Fe').
4. ĐẦU RA HOÀN CHỈNH: Đảm bảo đóng đầy đủ các dấu ngoặc nhọn }} và ngoặc vuông ]] để JSON hợp lệ 100%. Không sinh bất kỳ đoạn chat giải thích nào ngoài khối JSON.";

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
        var geminiModels = ResolveGeminiModels();
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
8. `sample_question`: 1 câu hỏi trắc nghiệm minh họa 4 phương án kèm lời giải chi tiết. ĐẶC BIỆT: KỂ CẢ KHI BÀI TOÁN CHỈ MÔ TẢ HÌNH/HÀM SỐ BẰNG CHỮ (như tứ diện ABCD, chóp S.ABCD, lăng trụ, hộp, lập phương, hàm số, bảng biến thiên...), BẮT BUỘC tự động tính toán tọa độ và nhúng thẻ [MATH_GRAPH_3D], [MATH_GRAPH] hoặc [VARIATION_TABLE] vào CẢ 3 NƠI: trong ví dụ của `theory_recap`, trong bài toán mẫu của `solution_method`, và trong `sample_question` (ở cả `content_latex` và `explanation`)! (Cú pháp Bảng biến thiên: [VARIATION_TABLE]\ntitle: Bảng biến thiên\nx: -\\infty | 1 | +\\infty\ny': + | 0 | -\ny: -\\infty | 2 | -\\infty\ndir: up | down\n[/VARIATION_TABLE]).

LƯU Ý CỰC KỲ QUAN TRỌNG VỀ ĐỊNH DẠNG JSON VÀ LATEX:
- Mọi ký tự gạch chéo ngược LaTeX BẮT BUỘC dùng 2 gạch chéo ngược: \\frac, \\sqrt, \\Delta, \\{{, \\}}, \\%, \\$, \\alpha... Tuyệt đối không dùng 1 gạch chéo ngược đơn độc.
- TUYỆT ĐỐI KHÔNG dùng dấu ngoặc kép thẳng "" bên trong nội dung văn bản (đề bài, lý thuyết, giải thích, phương án A-D). Nếu cần trích dẫn thuật ngữ, điểm hay công thức, BẮT BUỘC dùng dấu nháy đơn '...' (ví dụ: 'chất khử', 'Fe', 'S.ABCD', 'M', 'N').
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
        var geminiModels = ResolveGeminiModels();
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
   - Nếu là dạng bài Hình học không gian, Đồ thị hàm số hoặc Bảng biến thiên, BẮT BUỘC nhúng thẻ tương tác [MATH_GRAPH_3D], [MATH_GRAPH] hoặc [VARIATION_TABLE] vào `contentLatex` hoặc `explanation` để học sinh và giáo viên quan sát trực quan.

LƯU Ý CỰC KỲ QUAN TRỌNG VỀ ĐỊNH DẠNG JSON VÀ LATEX:
- Mọi ký tự gạch chéo ngược LaTeX BẮT BUỘC dùng 2 gạch chéo ngược: \\frac, \\sqrt, \\Delta, \\{{, \\}}, \\%, \\$, \\alpha...
- TUYỆT ĐỐI KHÔNG dùng dấu ngoặc kép thẳng "" bên trong nội dung văn bản (đề bài, lý thuyết, giải thích, phương án A-D). Nếu cần trích dẫn thuật ngữ, tên điểm hay công thức, BẮT BUỘC dùng dấu nháy đơn '...' (ví dụ: 'chất khử', 'Fe', 'S.ABCD', 'M', 'N').
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
