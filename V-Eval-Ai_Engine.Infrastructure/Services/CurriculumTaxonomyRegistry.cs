using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Bộ nạp & đối chiếu Ma trận Nội dung Chương trình GDPT 2018 (Lớp 10 - 11 - 12, Chân trời sáng tạo & ĐGNL ĐHQG-HCM)
/// Đọc động từ file cấu hình JSON (Zero Hardcode trong logic), hỗ trợ phân loại Môn -> Lớp -> Chương -> Bài -> Dạng bài
/// và phát hiện vị trí Sơ đồ / Hình ảnh [IMAGE_PLACEHOLDER] để FE cắt bổ sung.
/// </summary>
public static class CurriculumTaxonomyRegistry
{
    private static JsonNode? _cachedTaxonomy;
    private static DateTime _lastLoadedUtc = DateTime.MinValue;
    private static readonly object _lock = new();

    public static JsonNode? GetTaxonomyRoot()
    {
        lock (_lock)
        {
            if (_cachedTaxonomy != null && (DateTime.UtcNow - _lastLoadedUtc).TotalMinutes < 10)
            {
                return _cachedTaxonomy;
            }

            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Templates", "Taxonomy", "gdpt2018_dgnl_taxonomy.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Templates", "Taxonomy", "gdpt2018_dgnl_taxonomy.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "V-Eval-Ai_Engine.Infrastructure", "Templates", "Taxonomy", "gdpt2018_dgnl_taxonomy.json")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string rawJson = File.ReadAllText(path, Encoding.UTF8);
                        _cachedTaxonomy = JsonNode.Parse(rawJson);
                        _lastLoadedUtc = DateTime.UtcNow;
                        return _cachedTaxonomy;
                    }
                    catch
                    {
                        // Continue to next path
                    }
                }
            }

            return _cachedTaxonomy;
        }
    }

    public static List<SkeletonChapterDto> GetStandardSkeletonChapters(string subjectCode, int grade)
    {
        var result = new List<SkeletonChapterDto>();
        var root = GetTaxonomyRoot();
        var domains = root?["domains"]?.AsArray();
        
        if (domains != null)
        {
            foreach (var dom in domains)
            {
                var subjects = dom?["subjects"]?.AsArray();
                if (subjects == null) continue;

                foreach (var sub in subjects)
                {
                    string? code = sub?["subjectCode"]?.GetValue<string>();
                    if (!string.Equals(code, subjectCode, StringComparison.OrdinalIgnoreCase)) continue;

                    var grades = sub?["grades"]?.AsArray();
                    if (grades == null || grades.Count == 0) continue;

                    // Ưu tiên khớp chính xác khối lớp được yêu cầu trước; chỉ fallback sang lớp 10/12 hoặc phần tử đầu tiên khi không tìm thấy đúng lớp
                    JsonNode? targetGradeNode = grade > 0
                        ? grades.FirstOrDefault(g => (g?["grade"]?.GetValue<int>() ?? 0) == grade)
                        : null;
                    targetGradeNode ??= grades.FirstOrDefault(g => (g?["grade"]?.GetValue<int>() ?? 0) == 10)
                        ?? grades.FirstOrDefault(g => (g?["grade"]?.GetValue<int>() ?? 0) == 12)
                        ?? grades.FirstOrDefault();

                    var chaps = targetGradeNode?["chapters"]?.AsArray();
                    if (chaps == null) continue;

                    int chIdx = 1;
                    foreach (var cNode in chaps)
                    {
                        string cName = cNode?["name"]?.GetValue<string>() ?? $"Chương {chIdx}";
                        var chapDto = new SkeletonChapterDto
                        {
                            ChapterNumber = chIdx,
                            Title = cName,
                            PageStart = chIdx * 20 - 15,
                            PageEnd = chIdx * 20 + 10
                        };

                        var lessons = cNode?["lessons"]?.AsArray();
                        if (lessons != null)
                        {
                            int lesIdx = 1;
                            foreach (var lNode in lessons)
                            {
                                string lName = lNode?["name"]?.GetValue<string>() ?? $"Bài {lesIdx}";
                                var lesDto = new SkeletonLessonDto
                                {
                                    LessonNumber = lesIdx,
                                    Title = lName,
                                    PageStart = chapDto.PageStart + (lesIdx - 1) * 8,
                                    PageEnd = chapDto.PageStart + lesIdx * 8
                                };

                                var patterns = lNode?["patterns"]?.AsArray();
                                if (patterns != null)
                                {
                                    int pIdx = 1;
                                    foreach (var pNode in patterns)
                                    {
                                        string pName = pNode?.GetValue<string>() ?? $"Dạng {pIdx}";
                                        lesDto.Patterns.Add(new SkeletonPatternDto
                                        {
                                            PatternCode = $"C{chIdx}_B{lesIdx}_D{pIdx}",
                                            Name = pName,
                                            BloomLevel = (pIdx % 3) + 1,
                                            Description = string.Empty,
                                            Method = string.Empty
                                        });
                                        pIdx++;
                                    }
                                }
                                chapDto.Lessons.Add(lesDto);
                                lesIdx++;
                            }
                        }

                        result.Add(chapDto);
                        chIdx++;
                    }
                    if (result.Count > 0) return result;
                }
            }
        }

        // Fallback mở rộng chương trình SGK GDPT 2018 chi tiết cho các môn cốt lõi
        if (subjectCode == "PHYS" && (grade == 10 || grade == 0))
        {
            return new List<SkeletonChapterDto>
            {
                new()
                {
                    ChapterNumber = 1,
                    Title = "Chủ đề 1: Mô tả chuyển động",
                    PageStart = 5,
                    PageEnd = 38,
                    Lessons = new List<SkeletonLessonDto>
                    {
                        new() { LessonNumber = 1, Title = "Bài 1: Khái quát về môn Vật lí và An toàn trong phòng thực hành", PageStart = 5, PageEnd = 12, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C1_B1_D1", Name = "Đối tượng nghiên cứu của Vật lí và các quy tắc an toàn", BloomLevel = 1 } } },
                        new() { LessonNumber = 2, Title = "Bài 2: Độ dịch chuyển và Vận tốc", PageStart = 13, PageEnd = 24, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C1_B2_D1", Name = "Phân biệt quãng đường và độ dịch chuyển", BloomLevel = 2 }, new() { PatternCode = "P10_C1_B2_D2", Name = "Xác định vận tốc trung bình và đồ thị độ dịch chuyển", BloomLevel = 3 } } },
                        new() { LessonNumber = 3, Title = "Bài 3: Chuyển động biến đổi và Gia tốc", PageStart = 25, PageEnd = 38, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C1_B3_D1", Name = "Tính gia tốc và vận tốc trong chuyển động biến đổi đều", BloomLevel = 2 }, new() { PatternCode = "P10_C1_B3_D2", Name = "Sự rơi tự do và chuyển động ném ngang", BloomLevel = 3 } } }
                    }
                },
                new()
                {
                    ChapterNumber = 2,
                    Title = "Chủ đề 2: Lực và Chuyển động",
                    PageStart = 39,
                    PageEnd = 68,
                    Lessons = new List<SkeletonLessonDto>
                    {
                        new() { LessonNumber = 1, Title = "Bài 1: Tổng hợp và phân tích lực - Cân bằng lực", PageStart = 39, PageEnd = 48, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C2_B1_D1", Name = "Quy tắc hình bình hành tổng hợp lực", BloomLevel = 2 } } },
                        new() { LessonNumber = 2, Title = "Bài 2: Ba định luật Newton về chuyển động", PageStart = 49, PageEnd = 58, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C2_B2_D1", Name = "Áp dụng định luật II Newton F = ma", BloomLevel = 3 }, new() { PatternCode = "P10_C2_B2_D2", Name = "Định luật quán tính và lực tương tác trực đối", BloomLevel = 2 } } },
                        new() { LessonNumber = 3, Title = "Bài 3: Một số lực trong thực tiễn (Trọng lực, Ma sát, Căng dây)", PageStart = 59, PageEnd = 68, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C2_B3_D1", Name = "Tính lực ma sát trượt và hệ số ma sát", BloomLevel = 2 }, new() { PatternCode = "P10_C2_B3_D2", Name = "Lực đẩy Archimedes và điều kiện vật nổi", BloomLevel = 3 } } }
                    }
                },
                new()
                {
                    ChapterNumber = 3,
                    Title = "Chủ đề 3: Năng lượng, Công và Công suất",
                    PageStart = 69,
                    PageEnd = 92,
                    Lessons = new List<SkeletonLessonDto>
                    {
                        new() { LessonNumber = 1, Title = "Bài 1: Năng lượng và Công cơ học", PageStart = 69, PageEnd = 80, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C3_B1_D1", Name = "Tính công cơ học A = F.s.cos(alpha)", BloomLevel = 2 }, new() { PatternCode = "P10_C3_B1_D2", Name = "Công suất và hiệu suất chuyển hóa năng lượng", BloomLevel = 3 } } },
                        new() { LessonNumber = 2, Title = "Bài 2: Động năng, Thế năng và Bảo toàn cơ năng", PageStart = 81, PageEnd = 92, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C3_B2_D1", Name = "Tính động năng và thế năng trọng trường", BloomLevel = 2 }, new() { PatternCode = "P10_C3_B2_D2", Name = "Áp dụng định luật bảo toàn cơ năng trong trường trọng lực", BloomLevel = 3 } } }
                    }
                },
                new()
                {
                    ChapterNumber = 4,
                    Title = "Chủ đề 4: Động lượng và Va chạm",
                    PageStart = 93,
                    PageEnd = 108,
                    Lessons = new List<SkeletonLessonDto>
                    {
                        new() { LessonNumber = 1, Title = "Bài 1: Động lượng và Xung lượng của lực", PageStart = 93, PageEnd = 100, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C4_B1_D1", Name = "Khái niệm động lượng p = mv và xung lượng", BloomLevel = 2 } } },
                        new() { LessonNumber = 2, Title = "Bài 2: Định luật bảo toàn động lượng và Va chạm", PageStart = 101, PageEnd = 108, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C4_B2_D1", Name = "Bài toán va chạm đàn hồi và va chạm mềm", BloomLevel = 3 } } }
                    }
                },
                new()
                {
                    ChapterNumber = 5,
                    Title = "Chủ đề 5: Chuyển động tròn và Biến dạng của vật rắn",
                    PageStart = 109,
                    PageEnd = 122,
                    Lessons = new List<SkeletonLessonDto>
                    {
                        new() { LessonNumber = 1, Title = "Bài 1: Chuyển động tròn đều và Lực hướng tâm", PageStart = 109, PageEnd = 115, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C5_B1_D1", Name = "Tốc độ góc, gia tốc hướng tâm và lực hướng tâm", BloomLevel = 3 } } },
                        new() { LessonNumber = 2, Title = "Bài 2: Biến dạng đàn hồi và Định luật Hooke", PageStart = 116, PageEnd = 122, Patterns = new List<SkeletonPatternDto> { new() { PatternCode = "P10_C5_B2_D1", Name = "Độ biến dạng, độ cứng lò xo và định luật Hooke", BloomLevel = 2 } } }
                    }
                }
            };
        }

        return result;
    }

    public static string DetectBookEdition(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Chân trời sáng tạo";
        string lower = text.ToLowerInvariant();
        if (lower.Contains("kết nối tri thức") || lower.Contains("ket noi tri thuc") || lower.Contains("kntt"))
            return "Kết nối tri thức với cuộc sống";
        if (lower.Contains("cánh diều") || lower.Contains("canh dieu") || Regex.IsMatch(lower, @"\bcd\b|[_\-\s]cd[_\-\s\.]"))
            return "Cánh diều";
        return "Chân trời sáng tạo";
    }

    public static (string SubjectCode, string SubjectName, string DomainId, int Grade, string BookEdition, bool IsInDgnlScope) DetectDocumentIdentity(
        string? fileName,
        string? sampleText,
        string? hintDomainId = null,
        string? hintSubjectCode = null,
        int? hintGrade = null,
        string? hintBookEdition = null)
    {
        string combined = $"{fileName ?? string.Empty}\n{sampleText ?? string.Empty}".ToLowerInvariant();
        var root = GetTaxonomyRoot();

        // 1. Kiểm tra các môn ngoài phạm vi ĐGNL (Âm nhạc, Mỹ thuật, Thể dục, GDQP...)
        var outOfScopeArr = root?["outOfScopeKeywords"]?.AsArray();
        if (outOfScopeArr != null)
        {
            foreach (var kwNode in outOfScopeArr)
            {
                string? kw = kwNode?.GetValue<string>()?.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(kw) && combined.Contains(kw))
                {
                    return ("OUT_OF_SCOPE_DGNL", $"Kiến thức ngoài ĐGNL ({kw})", "OUT_OF_SCOPE", DetectGradeNumber(combined), "GDPT 2018", false);
                }
            }
        }

        int grade = hintGrade.HasValue && hintGrade.Value > 0 ? hintGrade.Value : DetectGradeNumber(combined);
        string bookEdition = !string.IsNullOrWhiteSpace(hintBookEdition) ? hintBookEdition : DetectBookEdition(combined);

        // Nếu client đã cung cấp hintSubjectCode rõ ràng (người dùng chọn trên Dropdown UI)
        if (!string.IsNullOrWhiteSpace(hintSubjectCode) && hintSubjectCode != "AUTO")
        {
            string normCode = hintSubjectCode.Trim().ToUpperInvariant();
            string sName = GetSubjectDisplayName(normCode);
            string domId = normCode switch
            {
                "VIET" or "ENGL" => "b581ee4c-7277-4be8-a156-c5b20a0a59f6",
                "PHYS" or "CHEM" or "BIO" => "77777777-7777-7777-7777-000000000001",
                "HIST" or "GEO" => "77777777-7777-7777-7777-000000000002",
                _ => "6f3765db-943e-4810-bf74-d6a8bdc215da"
            };
            return (normCode, sName, domId, grade, bookEdition, true);
        }

        // 2. Nhận diện môn học theo tín hiệu tên file + nội dung mẫu + hintDomainId
        string fnLower = (fileName ?? string.Empty).ToLowerInvariant();
        string domLower = (hintDomainId ?? string.Empty).ToLowerInvariant();

        if (fnLower.Contains("ngữ văn") || fnLower.Contains("ngu van") || fnLower.Contains("ngu-van") || fnLower.Contains("nguvan") ||
            fnLower.Contains("tiếng việt") || fnLower.Contains("văn ") || Regex.IsMatch(fnLower, @"\b(nv|van)[_\-\s\.]") ||
            Regex.IsMatch(combined, @"\b(ngữ văn|thơ trữ tình|truyện ngắn|nghị luận xã hội|nghị luận văn học|thần thoại|sử thi|hài kịch|bi kịch|tùy bút|tản văn|phóng sự|văn tế|tri thức ngữ văn)\b"))
        {
            return ("VIET", "Ngữ văn / Tiếng Việt", "b581ee4c-7277-4be8-a156-c5b20a0a59f6", grade, bookEdition, true);
        }

        if (fnLower.Contains("tiếng anh") || fnLower.Contains("english") || fnLower.Contains("friends global") || fnLower.Contains("global success") || Regex.IsMatch(fnLower, @"\b(eng|ta)[_\-\s\.]") ||
            Regex.IsMatch(combined, @"\b(reading comprehension|grammar|vocabulary|phrasal verbs|relative clauses|passive voice)\b"))
        {
            return ("ENGL", "Tiếng Anh", "b581ee4c-7277-4be8-a156-c5b20a0a59f6", grade, bookEdition, true);
        }

        if (fnLower.Contains("vật lí") || fnLower.Contains("vật lý") || fnLower.Contains("vat li") || fnLower.Contains("vat ly") || fnLower.Contains("physics") ||
            Regex.IsMatch(fnLower, @"\bvl\b|[_\-\s]vl[_\-\s\.]|\bsgk\s*vl\b") ||
            Regex.IsMatch(combined, @"\b(vật lí|vật lý|dao động điều hòa|sóng cơ|điện trường|từ trường|cảm ứng điện từ|khí lí tưởng|vật lí nhiệt|vật lí hạt nhân|định luật newton|động lượng)\b"))
        {
            return ("PHYS", "Vật lí", "77777777-7777-7777-7777-000000000001", grade, bookEdition, true);
        }

        if (fnLower.Contains("hóa học") || fnLower.Contains("hoá học") || fnLower.Contains("hoa hoc") || fnLower.Contains("chemistry") || fnLower.Contains("ester") ||
            Regex.IsMatch(fnLower, @"\b(hh|hoa|hóa|hoá)[_\-\s\.]|\bsgk\s*hh\b") ||
            Regex.IsMatch(combined, @"\b(hóa học|hoá học|ester|lipid|carbohydrate|glucose|amine|amino acid|peptide|polymer|điện phân|pin galvani|enthalpy|cân bằng hóa học|cân bằng hoá học|hydrocarbon)\b"))
        {
            return ("CHEM", "Hóa học", "77777777-7777-7777-7777-000000000001", grade, bookEdition, true);
        }

        if (fnLower.Contains("sinh học") || fnLower.Contains("sinh hoc") || fnLower.Contains("biology") ||
            Regex.IsMatch(fnLower, @"\b(sh|sinh)[_\-\s\.]|\bsgk\s*sh\b") ||
            Regex.IsMatch(combined, @"\b(sinh học|phiên mã|dịch mã|nhiễm sắc thể|di truyền|quần thể|quần xã|hệ sinh thái|tiến hóa|nguyên phân|giảm phân)\b"))
        {
            return ("BIO", "Sinh học", "77777777-7777-7777-7777-000000000001", grade, bookEdition, true);
        }

        if (fnLower.Contains("lịch sử") || fnLower.Contains("lich su") || fnLower.Contains("history") ||
            Regex.IsMatch(fnLower, @"\b(ls|su)[_\-\s\.]|\bsgk\s*ls\b") ||
            Regex.IsMatch(combined, @"\b(lịch sử|chiến tranh lạnh|cách mạng tháng tám|kháng chiến chống pháp|kháng chiến chống mỹ|công cuộc đổi mới)\b"))
        {
            return ("HIST", "Lịch sử", "77777777-7777-7777-7777-000000000002", grade, bookEdition, true);
        }

        if (fnLower.Contains("địa lí") || fnLower.Contains("địa lý") || fnLower.Contains("dia li") || fnLower.Contains("geography") ||
            Regex.IsMatch(fnLower, @"\b(dl|dia)[_\-\s\.]|\bsgk\s*dl\b") ||
            Regex.IsMatch(combined, @"\b(địa lí|địa lý|nhiệt đới ẩm gió mùa|đô thị hóa|vùng kinh tế|đồng bằng sông cửu long|đông nam bộ)\b"))
        {
            return ("GEO", "Địa lí", "77777777-7777-7777-7777-000000000002", grade, bookEdition, true);
        }

        if (fnLower.Contains("logic") || fnLower.Contains("số liệu") ||
            Regex.IsMatch(combined, @"\b(tư duy logic|phân tích số liệu|xếp vị trí bàn tròn)\b"))
        {
            return ("LOGIC_DATA", "Tư duy Logic & Phân tích số liệu", "6f3765db-943e-4810-bf74-d6a8bdc215da", grade, bookEdition, true);
        }

        if (domLower.Contains("b581ee4c") || domLower.Contains("lang") || domLower.Contains("ngôn ngữ") || domLower.Contains("văn"))
        {
            return ("VIET", "Ngữ văn / Tiếng Việt", "b581ee4c-7277-4be8-a156-c5b20a0a59f6", grade, bookEdition, true);
        }
        if (domLower.Contains("77777777-7777-7777-7777-000000000001") || domLower.Contains("khtn") || domLower.Contains("natural"))
        {
            return ("PHYS", "Khoa học tự nhiên", "77777777-7777-7777-7777-000000000001", grade, bookEdition, true);
        }
        if (domLower.Contains("77777777-7777-7777-7777-000000000002") || domLower.Contains("khxh") || domLower.Contains("social"))
        {
            return ("HIST", "Khoa học xã hội", "77777777-7777-7777-7777-000000000002", grade, bookEdition, true);
        }

        return ("MATH", "Toán học", "6f3765db-943e-4810-bf74-d6a8bdc215da", grade, bookEdition, true);
    }

    public static int DetectGradeNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 12;
        if (Regex.IsMatch(text, @"\b(lớp\s*10|toán\s*10|ngữ\s*văn\s*10|vật\s*l[íý]\s*10|h[óo][aá]\s*học\s*10|h[óo][aá]\s*10|sinh\s*học\s*10|lịch\s*sử\s*10|địa\s*l[íý]\s*10|khối\s*10|grade\s*10|[_\-\s]10[_\-\s\.])\b", RegexOptions.IgnoreCase))
            return 10;
        if (Regex.IsMatch(text, @"\b(lớp\s*11|toán\s*11|ngữ\s*văn\s*11|vật\s*l[íý]\s*11|h[óo][aá]\s*học\s*11|h[óo][aá]\s*11|sinh\s*học\s*11|lịch\s*sử\s*11|địa\s*l[íý]\s*11|khối\s*11|grade\s*11|[_\-\s]11[_\-\s\.])\b", RegexOptions.IgnoreCase))
            return 11;
        if (Regex.IsMatch(text, @"\b(lớp\s*12|toán\s*12|ngữ\s*văn\s*12|vật\s*l[íý]\s*12|h[óo][aá]\s*học\s*12|h[óo][aá]\s*12|sinh\s*học\s*12|lịch\s*sử\s*12|địa\s*l[íý]\s*12|khối\s*12|grade\s*12|[_\-\s]12[_\-\s\.])\b", RegexOptions.IgnoreCase))
            return 12;

        // Suy luận khối lớp từ các chương đặc trưng của Toán/Văn/Lý/Hóa nếu tên file không ghi số lớp
        if (Regex.IsMatch(text, @"\b(nguyên hàm|tích phân|tiệm cận|mặt cầu|xác suất có điều kiện|công thức bayes|ester|carbohydrate|vật lí nhiệt|khí lí tưởng|vật lí hạt nhân)\b", RegexOptions.IgnoreCase))
            return 12;
        if (Regex.IsMatch(text, @"\b(cấp số cộng|cấp số nhân|hàm số liên tục|lôgarit|quan hệ song song|quan hệ vuông góc|dao động điều hòa|sóng dừng|cân bằng hóa học|cân bằng hoá học|hydrocarbon)\b", RegexOptions.IgnoreCase))
            return 11;
        if (Regex.IsMatch(text, @"\b(mệnh đề và tập hợp|tam thức bậc hai|nhị thức newton|ba đường conic|thần thoại|sử thi|cấu tạo nguyên tử|phản ứng oxi hóa|phản ứng oxi hoá|tốc độ phản ứng)\b", RegexOptions.IgnoreCase))
            return 10;

        return 12;
    }



    /// <summary>
    /// Bóc tách thẻ [TAXONOMY: ...] nếu AI Vision gắn ở đầu trang, đồng thời đối chiếu với Ma trận GDPT 2018
    /// để gán chính xác Chương -> Bài -> Dạng bài -> Loại mục (THEORY / FORMULA / EXAMPLE / EXERCISE / PASSAGE)
    /// </summary>
    public static void ClassifyAndEnrichChunk(
        TextbookChunkDto chunk,
        ref string runningSubjectCode,
        ref string runningSubjectName,
        ref int runningGrade,
        ref string runningBookEdition,
        ref string runningChapter,
        ref string runningLesson,
        ref string runningPattern)
    {
        string text = chunk.Text ?? string.Empty;

        // 1. Kiểm tra tất cả thẻ [TAXONOMY: subject="..." | grade="..." | chapter="..." | lesson="..." | pattern="..." | section="..."]
        var taxMatches = Regex.Matches(text, @"\[TAXONOMY\s*:\s*([^\]]+)\]", RegexOptions.IgnoreCase);
        string? explicitSection = null;
        foreach (Match taxMatch in taxMatches)
        {
            string attrBlock = taxMatch.Groups[1].Value;

            string? sub = ExtractAttr(attrBlock, "subject");
            string? gr = ExtractAttr(attrBlock, "grade");
            string? chap = ExtractAttr(attrBlock, "chapter");
            string? les = ExtractAttr(attrBlock, "lesson");
            string? pat = ExtractAttr(attrBlock, "pattern");
            var sec = ExtractAttr(attrBlock, "section");
            if (!string.IsNullOrWhiteSpace(sec)) explicitSection = sec;

            if (!string.IsNullOrWhiteSpace(sub))
            {
                runningSubjectCode = NormalizeSubjectCode(sub, runningSubjectCode);
                runningSubjectName = GetSubjectDisplayName(runningSubjectCode);
            }
            if (!string.IsNullOrWhiteSpace(gr) && int.TryParse(Regex.Match(gr, @"1[0-2]").Value, out int parsedGr))
            {
                runningGrade = parsedGr;
            }
            if (!string.IsNullOrWhiteSpace(chap) && !chap.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                runningChapter = chap.Trim();
            }
            if (!string.IsNullOrWhiteSpace(les) && !les.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                runningLesson = les.Trim();
            }
            if (!string.IsNullOrWhiteSpace(pat) && !pat.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                runningPattern = pat.Trim();
            }
        }

        // Xóa sạch 100% tất cả các thẻ [TAXONOMY: ...] khỏi nội dung văn bản
        text = Regex.Replace(text, @"\[TAXONOMY\s*:[^\]]+\]", string.Empty, RegexOptions.IgnoreCase).Trim();

        // 1.1. Loại bỏ các dòng rác thông tin xuất bản, bản quyền, số trang kết thúc bài học
        text = Regex.Replace(text, @"(?m)^[ \t]*(?:Tên sách|Bộ sách|Tổng chủ biên|Chủ biên|Các tác giả|Nhà xuất bản|Chịu trách nhiệm xuất bản|Mã số sách|Giấy phép xuất bản|In tại)\s*:.*$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\(Trang\s+\d+\s+là\s+trang\s+kết\s+thúc\s+bài\s+học[^\)]*\)", string.Empty, RegexOptions.IgnoreCase);

        // Nhận diện nếu toàn bộ trang chỉ là trang bìa hoặc thông tin xuất bản
        bool isPublishingPage = Regex.IsMatch(text, @"\b(tổng chủ biên|chủ biên|nhà xuất bản|chịu trách nhiệm xuất bản|trình bày bìa|mã số sách)\b", RegexOptions.IgnoreCase) &&
                                !text.Contains("định nghĩa", StringComparison.OrdinalIgnoreCase) &&
                                !text.Contains("công thức", StringComparison.OrdinalIgnoreCase);
        if (isPublishingPage)
        {
            explicitSection = "COVER";
            runningChapter = "Thông tin xuất bản";
            runningLesson = "Bìa & Bản quyền";
            runningPattern = "Thông tin xuất bản";
        }

        // 2. Quét tiêu đề Chương / Bài / Dạng trực tiếp trong văn bản trang sách
        var chapHeaderMatch = Regex.Match(text, @"(?m)^(?:#+\s*)?(Chương\s+(?:[IVXLC]+|\d+)\s*[:\.\-–]\s*[^\r\n]{3,90}|Chuyên\s+đề\s+\d+\s*[:\.\-–]\s*[^\r\n]{3,90})", RegexOptions.IgnoreCase);
        if (chapHeaderMatch.Success)
        {
            runningChapter = chapHeaderMatch.Groups[1].Value.Trim().Trim('*');
        }

        var lessonHeaderMatch = Regex.Match(text, @"(?m)^(?:#+\s*)?(Bài\s+\d+\s*[:\.\-–]\s*[^\r\n]{3,100})", RegexOptions.IgnoreCase);
        if (lessonHeaderMatch.Success)
        {
            runningLesson = lessonHeaderMatch.Groups[1].Value.Trim().Trim('*');
        }

        var patternHeaderMatch = Regex.Match(text, @"(?m)^(?:#+\s*|\*\*)?(Dạng\s+\d+\s*[:\.\-–]\s*[^\r\n\*]{3,110})", RegexOptions.IgnoreCase);
        if (patternHeaderMatch.Success)
        {
            runningPattern = patternHeaderMatch.Groups[1].Value.Trim().Trim('*');
        }

        // 3. Đối chiếu với cây chuẩn GDPT 2018 (gdpt2018_dgnl_taxonomy.json) nếu trang giữa bài chưa có tiêu đề hoặc cần khớp chuẩn
        MatchBestNodeFromTaxonomy(text, runningSubjectCode, ref runningGrade, ref runningChapter, ref runningLesson, ref runningPattern);

        // 4. Trích xuất và chuẩn hóa các điểm đánh dấu Sơ đồ / Hình vẽ [IMAGE_PLACEHOLDER: ...]
        var placeholders = ExtractAndNormalizeImagePlaceholders(ref text, chunk.PageNumber, runningSubjectCode);

        // 5. Chuẩn hóa toàn bộ công thức Toán / Lý / Hóa sang KaTeX chuẩn ($$...$$ và $...$)
        text = CurriculumAiService.NormalizeCurriculumLatex(text);

        chunk.Text = text;
        chunk.CharCount = text.Length;
        chunk.SubjectCode = runningSubjectCode;
        chunk.SubjectName = runningSubjectName;
        chunk.Grade = runningGrade;
        chunk.BookEdition = runningBookEdition;
        chunk.ChapterName = !string.IsNullOrWhiteSpace(runningChapter) ? runningChapter : $"Chương trọng tâm {runningSubjectName} {runningGrade}";
        chunk.LessonName = !string.IsNullOrWhiteSpace(runningLesson) ? runningLesson : "Bài 1: Nội dung cốt lõi SGK";
        chunk.PatternName = !string.IsNullOrWhiteSpace(runningPattern) ? runningPattern : "Dạng 1: Kiến thức & Kỹ năng trọng tâm";
        chunk.SectionType = DetermineSectionType(text, explicitSection, runningSubjectCode);
        chunk.HasDiagram = placeholders.Count > 0 ||
                           text.Contains("[MATH_GRAPH", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("[VARIATION_TABLE]", StringComparison.OrdinalIgnoreCase);
        chunk.ImagePlaceholders = placeholders;
    }

    public static List<DiagramPlaceholderDto> ExtractAndNormalizeImagePlaceholders(
        ref string text,
        int pageNumber,
        string subjectCode)
    {
        var list = new List<DiagramPlaceholderDto>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        // 1. Bắt các thẻ [IMAGE_PLACEHOLDER: ...] do Vision AI sinh ra
        int counter = 0;
        text = Regex.Replace(text, @"\[IMAGE_PLACEHOLDER\s*:\s*([^\]]+)\]", m =>
        {
            counter++;
            string rawAttrs = m.Groups[1].Value;
            string id = ExtractAttr(rawAttrs, "id") ?? $"img_p{pageNumber}_{counter}";
            string type = (ExtractAttr(rawAttrs, "type") ?? InferDiagramType(rawAttrs, subjectCode)).ToUpperInvariant();
            string desc = ExtractAttr(rawAttrs, "desc") ?? ExtractAttr(rawAttrs, "description") ?? rawAttrs.Trim();
            desc = desc.Replace("\"", "'").Trim();

            list.Add(new DiagramPlaceholderDto
            {
                Id = id,
                PageNumber = pageNumber,
                DiagramType = type,
                Description = desc,
                SuggestedAction = "FE_CROP_OR_SUPPLEMENT"
            });

            return $"[IMAGE_PLACEHOLDER: id=\"{id}\" page=\"{pageNumber}\" type=\"{type}\" desc=\"{desc}\"]";
        }, RegexOptions.IgnoreCase);

        // 2. Nếu văn bản có chú thích hình SGK rõ ràng (VD: "Hình 1.2. Đồ thị hàm số...", "Hình 3.4: Sơ đồ...") mà chưa có thẻ IMAGE_PLACEHOLDER
        if (list.Count == 0)
        {
            var figMatches = Regex.Matches(text, @"(?m)^[ \t]*(?:\*\*)?(Hình\s+\d+[\.\d]*\s*[:\.\-–]\s*[^\r\n]{4,120})(?:\*\*)?$", RegexOptions.IgnoreCase);
            foreach (Match fm in figMatches)
            {
                counter++;
                string caption = fm.Groups[1].Value.Trim().Replace("\"", "'");
                string id = $"img_p{pageNumber}_{counter}";
                string type = InferDiagramType(caption, subjectCode);

                list.Add(new DiagramPlaceholderDto
                {
                    Id = id,
                    PageNumber = pageNumber,
                    DiagramType = type,
                    Description = caption,
                    SuggestedAction = "FE_CROP_OR_SUPPLEMENT"
                });

                string tag = $"\n[IMAGE_PLACEHOLDER: id=\"{id}\" page=\"{pageNumber}\" type=\"{type}\" desc=\"{caption}\"]\n";
                int pos = text.IndexOf(fm.Value, StringComparison.Ordinal);
                if (pos >= 0)
                {
                    text = text.Insert(pos + fm.Value.Length, tag);
                }
            }
        }

        return list;
    }

    private static string InferDiagramType(string desc, string subjectCode)
    {
        string lower = desc.ToLowerInvariant();
        if (lower.Contains("bảng biến thiên") || lower.Contains("variation")) return "VARIATION_TABLE";
        if (lower.Contains("đồ thị") || lower.Contains("parabol") || lower.Contains("tiệm cận") || lower.Contains("graph_2d")) return "GRAPH_2D";
        if (lower.Contains("hình chóp") || lower.Contains("tứ diện") || lower.Contains("lăng trụ") || lower.Contains("mặt cầu") || lower.Contains("oxyz") || lower.Contains("3d")) return "GEOMETRY_3D";
        if (lower.Contains("thí nghiệm") || lower.Contains("mạch điện") || lower.Contains("sơ đồ") || subjectCode == "CHEM" || subjectCode == "PHYS" || subjectCode == "BIO") return "LAB_DIAGRAM";
        if (lower.Contains("biểu đồ") || lower.Contains("lược đồ") || lower.Contains("bản đồ")) return "CHART";
        return "FIGURE";
    }

    private static string DetermineSectionType(string text, string? explicitSection, string subjectCode)
    {
        if (!string.IsNullOrWhiteSpace(explicitSection))
        {
            string upper = explicitSection.Trim().ToUpperInvariant();
            if (upper is "THEORY" or "FORMULA" or "EXAMPLE" or "EXERCISE" or "PASSAGE" or "COVER")
                return upper;
        }

        string lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(tổng chủ biên|chủ biên|nhà xuất bản|chịu trách nhiệm xuất bản|trình bày bìa|mã số sách|giấy phép xuất bản|lời nói đầu|hướng dẫn sử dụng sách|bìa 1|bìa 4|mục lục|table of contents)\b")
            || (lower.Contains("tên sách:") && lower.Contains("bộ sách:")))
        {
            return "COVER";
        }
        if (Regex.IsMatch(lower, @"\b(bài tập cuối|luyện tập\s+\d+|câu\s+1[\.\:]\s+.*câu\s+2[\.\:])", RegexOptions.Singleline))
            return "EXERCISE";
        if (Regex.IsMatch(lower, @"\b(ví dụ\s+\d+|bài toán mẫu|lời giải|hướng dẫn giải)\b"))
            return "EXAMPLE";
        if (subjectCode == "VIET" && (lower.Contains("văn bản") || lower.Contains("đọc văn bản") || text.Length > 1200))
            return "PASSAGE";
        if (text.Count(c => c == '$') >= 8 && Regex.IsMatch(lower, @"\b(định lí|công thức|tính chất|hệ quả)\b"))
            return "FORMULA";
        return "THEORY";
    }

    private static void MatchBestNodeFromTaxonomy(
        string pageText,
        string subjectCode,
        ref int runningGrade,
        ref string runningChapter,
        ref string runningLesson,
        ref string runningPattern)
    {
        var root = GetTaxonomyRoot();
        var domains = root?["domains"]?.AsArray();
        if (domains == null) return;

        string lowerText = pageText.ToLowerInvariant();
        int bestScore = 0;
        int matchedGrade = runningGrade;
        string? matchedChap = null;
        string? matchedLes = null;
        string? matchedPat = null;

        foreach (var dom in domains)
        {
            var subjects = dom?["subjects"]?.AsArray();
            if (subjects == null) continue;

            foreach (var sub in subjects)
            {
                string? sCode = sub?["subjectCode"]?.GetValue<string>();
                if (!string.Equals(sCode, subjectCode, StringComparison.OrdinalIgnoreCase)) continue;

                var grades = sub?["grades"]?.AsArray();
                if (grades == null) continue;

                foreach (var grNode in grades)
                {
                    int gr = grNode?["grade"]?.GetValue<int>() ?? 12;
                    // Ưu tiên khối lớp hiện tại, nhưng vẫn cho phép nhảy sang khối lớp khác nếu khớp rất mạnh
                    int gradeBonus = (gr == runningGrade) ? 2 : 0;

                    var chapters = grNode?["chapters"]?.AsArray();
                    if (chapters == null) continue;

                    foreach (var chapNode in chapters)
                    {
                        string chapName = chapNode?["name"]?.GetValue<string>() ?? string.Empty;
                        var lessons = chapNode?["lessons"]?.AsArray();
                        if (lessons == null) continue;

                        foreach (var lesNode in lessons)
                        {
                            string lesName = lesNode?["name"]?.GetValue<string>() ?? string.Empty;
                            var patterns = lesNode?["patterns"]?.AsArray();
                            if (patterns == null) continue;

                            foreach (var patNode in patterns)
                            {
                                string patName = patNode?.GetValue<string>() ?? string.Empty;
                                int score = ScoreTextMatch(lowerText, chapName, lesName, patName) + gradeBonus;
                                if (score > bestScore && score >= 5)
                                {
                                    bestScore = score;
                                    matchedGrade = gr;
                                    matchedChap = chapName;
                                    matchedLes = lesName;
                                    matchedPat = patName;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (bestScore >= 5)
        {
            runningGrade = matchedGrade;
            if (!string.IsNullOrWhiteSpace(matchedChap)) runningChapter = matchedChap;
            if (!string.IsNullOrWhiteSpace(matchedLes)) runningLesson = matchedLes;
            if (!string.IsNullOrWhiteSpace(matchedPat)) runningPattern = matchedPat;
        }
        else if (string.IsNullOrWhiteSpace(runningChapter) || string.IsNullOrWhiteSpace(runningLesson))
        {
            // Lấy chương/bài đầu tiên của môn & khối lớp tương ứng làm mặc định khởi đầu
            foreach (var dom in domains)
            {
                var subjects = dom?["subjects"]?.AsArray();
                if (subjects == null) continue;
                foreach (var sub in subjects)
                {
                    if (!string.Equals(sub?["subjectCode"]?.GetValue<string>(), subjectCode, StringComparison.OrdinalIgnoreCase)) continue;
                    var grades = sub?["grades"]?.AsArray();
                    int targetGradeVal = runningGrade;
                    var targetGr = grades?.FirstOrDefault(g => (g?["grade"]?.GetValue<int>() ?? 12) == targetGradeVal) ?? grades?.FirstOrDefault();
                    var firstChap = targetGr?["chapters"]?.AsArray()?.FirstOrDefault();
                    var firstLes = firstChap?["lessons"]?.AsArray()?.FirstOrDefault();
                    var firstPat = firstLes?["patterns"]?.AsArray()?.FirstOrDefault();

                    if (string.IsNullOrWhiteSpace(runningChapter))
                        runningChapter = firstChap?["name"]?.GetValue<string>() ?? $"Chương 1: Trọng tâm {subjectCode} {runningGrade}";
                    if (string.IsNullOrWhiteSpace(runningLesson))
                        runningLesson = firstLes?["name"]?.GetValue<string>() ?? "Bài 1: Kiến thức nền tảng";
                    if (string.IsNullOrWhiteSpace(runningPattern))
                        runningPattern = firstPat?.GetValue<string>() ?? "Dạng 1: Lý thuyết & Vận dụng cốt lõi";
                    return;
                }
            }
        }
    }

    private static int ScoreTextMatch(string lowerText, string chapName, string lesName, string patName)
    {
        int score = 0;
        string cleanChap = Regex.Replace(chapName.ToLowerInvariant(), @"^chương\s*\d+\s*:\s*", "");
        string cleanLes = Regex.Replace(lesName.ToLowerInvariant(), @"^bài\s*[\d,\s&]+\s*:\s*", "");
        string cleanPat = Regex.Replace(patName.ToLowerInvariant(), @"^dạng\s*\d+\s*:\s*", "");

        if (cleanLes.Length >= 5 && lowerText.Contains(cleanLes)) score += 8;
        if (cleanChap.Length >= 5 && lowerText.Contains(cleanChap)) score += 5;

        var keywords = $"{cleanLes} {cleanPat}"
            .Split(new[] { ' ', ',', '.', ';', ':', '(', ')', '-', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && w is not ("dạng" or "trong" or "hoặc" or "bằng" or "biết" or "tính" or "hàm" or "phần"))
            .Distinct()
            .ToList();

        foreach (var kw in keywords)
        {
            if (lowerText.Contains(kw)) score += 2;
        }

        return score;
    }

    public static string GetTaxonomyPromptReference(string subjectCode, int grade)
    {
        var root = GetTaxonomyRoot();
        var domains = root?["domains"]?.AsArray();
        if (domains == null) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"MA TRẬN CẤU TRÚC CHUẨN GDPT 2018 (BỘ CHÂN TRỜI SÁNG TẠO & ĐGNL V-ACT) CHO MÔN {subjectCode} (LỚP 10 - 11 - 12, TRỌNG TÂM LỚP {grade}):");

        foreach (var dom in domains)
        {
            var subjects = dom?["subjects"]?.AsArray();
            if (subjects == null) continue;

            foreach (var sub in subjects)
            {
                if (!string.Equals(sub?["subjectCode"]?.GetValue<string>(), subjectCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                var grades = sub?["grades"]?.AsArray();
                if (grades == null) continue;

                foreach (var grNode in grades)
                {
                    int gr = grNode?["grade"]?.GetValue<int>() ?? 12;
                    string edition = grNode?["bookEdition"]?.GetValue<string>() ?? "Chân trời sáng tạo";
                    sb.AppendLine($"- [KHỐI LỚP {gr} - {edition}]:");

                    var chapters = grNode?["chapters"]?.AsArray();
                    if (chapters == null) continue;

                    foreach (var chap in chapters)
                    {
                        string chapName = chap?["name"]?.GetValue<string>() ?? string.Empty;
                        sb.AppendLine($"  + {chapName}");
                        var lessons = chap?["lessons"]?.AsArray();
                        if (lessons == null) continue;

                        foreach (var les in lessons)
                        {
                            string lesName = les?["name"]?.GetValue<string>() ?? string.Empty;
                            var pats = les?["patterns"]?.AsArray()?.Select(p => p?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string?>();
                            sb.AppendLine($"    * {lesName} -> Các dạng gốc: {string.Join("; ", pats)}");
                        }
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tổng hợp và liên kết danh sách Chunks SGK đã bóc tách thành Cây Chuyên đề (Chương -> Bài -> Dạng bài)
    /// tương thích 100% với POST /api/content/curriculum/import của Content Service (0% thay đổi DB).
    /// </summary>
    public static ImportCurriculumRequestDto SynthesizeCurriculumFromChunks(
        string fileName,
        string domainId,
        List<TextbookChunkDto> chunks)
    {
        var first = chunks.FirstOrDefault();
        var identity = DetectDocumentIdentity(fileName, first?.Text, domainId);

        string resolvedDomainId = Guid.TryParse(domainId, out _) ? domainId : identity.DomainId;
        string subjectCode = first?.SubjectCode ?? identity.SubjectCode;
        string subjectName = first?.SubjectName ?? identity.SubjectName;
        int grade = first?.Grade ?? identity.Grade;
        string bookEdition = first?.BookEdition ?? identity.BookEdition;

        var dto = new ImportCurriculumRequestDto
        {
            DomainId = resolvedDomainId,
            DomainName = subjectName,
            SubjectCode = subjectCode,
            SubjectName = subjectName,
            Grade = grade,
            BookEdition = bookEdition,
            IsInDgnlScope = identity.IsInDgnlScope,
            SourceFileName = fileName,
            TotalPagesProcessed = chunks.Select(c => c.PageNumber).Distinct().Count(),
            TotalCharactersExtracted = chunks.Sum(c => c.CharCount),
            DpiUsed = first?.DpiUsed ?? 56,
            TotalDiagramsDetected = chunks.Sum(c => c.ImagePlaceholders?.Count ?? 0)
        };

        var chapterGroups = chunks
            .GroupBy(c => !string.IsNullOrWhiteSpace(c.ChapterName) ? c.ChapterName : $"Chương 1: {subjectName} Lớp {grade}")
            .ToList();

        foreach (var chapGrp in chapterGroups)
        {
            var chapDto = new ImportChapterDto
            {
                ChapterName = chapGrp.Key.Contains($"[Lớp {grade}]") ? chapGrp.Key : $"[Lớp {grade} - {bookEdition}] {chapGrp.Key}",
                Weight = Math.Round(1.0 / Math.Max(1, chapterGroups.Count), 2),
                Description = $"Trích xuất chuẩn SGK {bookEdition} - Môn {subjectName} Lớp {grade}"
            };

            var lessonGroups = chapGrp
                .GroupBy(c => !string.IsNullOrWhiteSpace(c.LessonName) ? c.LessonName : "Bài 1: Nội dung trọng tâm")
                .ToList();

            foreach (var lesGrp in lessonGroups)
            {
                var lesDto = new ImportLessonDto
                {
                    LessonName = lesGrp.Key,
                    Weight = Math.Round(chapDto.Weight / Math.Max(1, lessonGroups.Count), 2),
                    Summary = $"Tổng hợp từ trang {lesGrp.Min(x => x.PageNumber)} đến trang {lesGrp.Max(x => x.PageNumber)}"
                };

                var patternGroups = lesGrp
                    .GroupBy(c => !string.IsNullOrWhiteSpace(c.PatternName) ? c.PatternName : "Dạng 1: Kiến thức & Kỹ năng cốt lõi")
                    .ToList();

                int patIdx = 0;
                foreach (var patGrp in patternGroups)
                {
                    patIdx++;
                    var theoryChunks = patGrp.Where(c => c.SectionType is "THEORY" or "FORMULA" or "PASSAGE").ToList();
                    var exampleChunks = patGrp.Where(c => c.SectionType == "EXAMPLE").ToList();
                    var exerciseChunks = patGrp.Where(c => c.SectionType == "EXERCISE").ToList();

                    var theorySb = new StringBuilder();
                    foreach (var tc in theoryChunks.Take(4))
                    {
                        theorySb.AppendLine($"**[Trang {tc.PageNumber}]** {tc.Text}");
                        theorySb.AppendLine();
                    }
                    if (theorySb.Length == 0 && patGrp.Any())
                    {
                        var fallbackChunk = patGrp.First();
                        theorySb.AppendLine($"**[Trang {fallbackChunk.PageNumber}]** {fallbackChunk.Text}");
                    }

                    if (exampleChunks.Count > 0)
                    {
                        theorySb.AppendLine("\n### 📌 Ví dụ minh họa SGK:");
                        foreach (var ec in exampleChunks.Take(2))
                        {
                            theorySb.AppendLine($"**[Trang {ec.PageNumber}]** {ec.Text}");
                            theorySb.AppendLine();
                        }
                    }

                    var methodSb = new StringBuilder();
                    methodSb.AppendLine($"**Bước 1:** Xác định giả thiết và đơn vị kiến thức thuộc *{lesGrp.Key}* ({patGrp.Key}).");
                    methodSb.AppendLine("**Bước 2:** Áp dụng định lý, công thức chuẩn SGK GDPT 2018 và biến đổi tương đương.");
                    methodSb.AppendLine("**Bước 3:** Kiểm tra điều kiện xác định và đối chiếu đáp án.");

                    if (exerciseChunks.Count > 0)
                    {
                        methodSb.AppendLine("\n### 📄 Bài toán mẫu áp dụng phương pháp:");
                        foreach (var ex in exerciseChunks.Take(2))
                        {
                            methodSb.AppendLine($"**[Trang {ex.PageNumber}]** {ex.Text}");
                        }
                    }

                    var allPlaceholders = patGrp
                        .SelectMany(c => c.ImagePlaceholders ?? Enumerable.Empty<DiagramPlaceholderDto>())
                        .ToList();

                    string cleanPatName = Regex.Replace(patGrp.Key, @"^Dạng\s*\d+\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
                    var patDto = new ImportPatternDto
                    {
                        PatternName = $"Dạng {patIdx}: {cleanPatName}",
                        Weight = 0.05,
                        Classification = $"{subjectName} {grade} • {bookEdition}",
                        CoreSkill = lesGrp.Key,
                        TheoryRecap = CurriculumAiService.NormalizeCurriculumLatex(theorySb.ToString().Trim()),
                        SolutionMethod = CurriculumAiService.NormalizeCurriculumLatex(methodSb.ToString().Trim()),
                        DurationSeconds = 600,
                        HasDiagram = allPlaceholders.Count > 0,
                        ImagePlaceholders = allPlaceholders
                    };

                    lesDto.Patterns.Add(patDto);
                }

                chapDto.Lessons.Add(lesDto);
            }

            dto.Chapters.Add(chapDto);
        }

        return dto;
    }

    private static string? ExtractAttr(string raw, string key)
    {
        var m = Regex.Match(raw, $@"\b{key}\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();
        var m2 = Regex.Match(raw, $@"\b{key}\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (m2.Success) return m2.Groups[1].Value.Trim();
        var m3 = Regex.Match(raw, $@"\b{key}\s*=\s*([^|\]]+)", RegexOptions.IgnoreCase);
        if (m3.Success) return m3.Groups[1].Value.Trim().Trim('"', '\'');
        return null;
    }

    private static string NormalizeSubjectCode(string raw, string fallback)
    {
        string u = raw.Trim().ToUpperInvariant();
        if (u.Contains("MATH") || u.Contains("TOÁN") || u.Contains("TOAN")) return "MATH";
        if (u.Contains("VIET") || u.Contains("LIT") || u.Contains("VĂN") || u.Contains("VAN")) return "VIET";
        if (u.Contains("ENG") || u.Contains("ANH")) return "ENGL";
        if (u.Contains("PHYS") || u.Contains("LÍ") || u.Contains("LÝ") || u.Contains("LI")) return "PHYS";
        if (u.Contains("CHEM") || u.Contains("HÓA") || u.Contains("HOA")) return "CHEM";
        if (u.Contains("BIO") || u.Contains("SINH")) return "BIO";
        if (u.Contains("HIST") || u.Contains("SỬ") || u.Contains("SU")) return "HIST";
        if (u.Contains("GEO") || u.Contains("ĐỊA") || u.Contains("DIA")) return "GEO";
        if (u.Contains("LOGIC") || u.Contains("DATA")) return "LOGIC_DATA";
        return fallback;
    }

    private static string GetSubjectDisplayName(string code) => code switch
    {
        "MATH" => "Toán học",
        "VIET" => "Ngữ văn / Tiếng Việt",
        "ENGL" => "Tiếng Anh",
        "PHYS" => "Vật lí",
        "CHEM" => "Hóa học",
        "BIO" => "Sinh học",
        "HIST" => "Lịch sử",
        "GEO" => "Địa lí",
        "LOGIC_DATA" => "Tư duy Logic & Phân tích số liệu",
        "OUT_OF_SCOPE_DGNL" => "Kiến thức ngoài phạm vi ĐGNL",
        _ => "Toán học"
    };
}

