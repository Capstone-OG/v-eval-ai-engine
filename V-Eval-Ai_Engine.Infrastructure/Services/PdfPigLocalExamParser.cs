using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Bộ bóc tách đề thi PDF cục bộ viết 100% bằng C# (UglyToad.PdfPig), không phụ thuộc Python hay pymupdf.
/// Dùng làm tầng dự phòng an toàn tuyệt đối khi AI Cloud quá tải/offline hoặc máy chủ chưa cài đặt pymupdf.
/// </summary>
public static class PdfPigLocalExamParser
{
    private static readonly Regex PageMarkerRegex = new(
        @"---\s*PAGE_(\d+)\s*---",
        RegexOptions.Compiled);

    private static readonly Regex PassagePattern = new(
        @"(?:Dựa vào (?:các )?(?:thông tin|đoạn trích|văn bản|dữ liệu)[^0-9\r\n]{0,60}?(?:câu(?:\s+hỏi)?\s+từ|từ\s+câu)\s+(\d+)\s+(?:đến|\-|–)\s+(\d+)[^\r\n]*|Đọc (?:đoạn trích|văn bản|thông tin)[^0-9\r\n]{0,80}?(?:câu(?:\s+hỏi)?\s+từ|từ\s+câu)\s+(\d+)\s+(?:đến|\-|–)\s+(\d+)[^\r\n]*|Questions?\s+(\d+)\s*[\-–to]+\s*(\d+)[^\r\n]*)\r?\n(.*?)(?=(?:\r?\n\s*(?:Câu|Question)\s+\d+\s*[:.]|$))",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex QuestionPattern = new(
        @"(?:^|\r?\n)\s*(?:Câu|Question)\s+(\d+)\s*[:.]\s*(.*?)(?=(?:\r?\n\s*(?:Câu|Question)\s+\d+\s*[:.]|\r?\n\s*(?:Dựa vào (?:các )?(?:thông tin|đoạn trích|văn bản|dữ liệu)|Đọc (?:đoạn trích|văn bản|thông tin)|Questions?\s+\d+\s*[\-–to]+\s*\d+)|[-–—]{3,}\s*HẾT\s*[-–—]{3,}|$))",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex OptionPattern = new(
        @"(?:^|[\r\n\s])([A-D])\s*[.)]\s+(.*?)(?=(?:[\r\n\s]+[A-D]\s*[.)]\s+|$))",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ErrorIdentificationTailRegex = new(
        @"[\r\n\s]+A\s+B\s+C\s+D\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ParsedExamDto Parse(byte[] pdfBytes, string fileName)
    {
        return ParsePageRange(pdfBytes, fileName, null);
    }

    public static ParsedExamDto ParsePageRange(byte[] pdfBytes, string fileName, IReadOnlyCollection<int>? pageNumbers)
    {
        using var document = PdfDocument.Open(pdfBytes);
        var fullTextBuilder = new StringBuilder();
        int totalPages = document.NumberOfPages;
        HashSet<int>? filter = pageNumbers != null && pageNumbers.Count > 0 ? new HashSet<int>(pageNumbers) : null;

        foreach (var page in document.GetPages())
        {
            if (filter != null && !filter.Contains(page.Number)) continue;
            string pageText = ExtractOrderedPageText(page);
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                fullTextBuilder.AppendLine($"\n--- PAGE_{page.Number} ---");
                fullTextBuilder.AppendLine(CleanPuaSymbols(pageText));
            }
        }

        return ParseExamText(fullTextBuilder.ToString(), fileName, totalPages);
    }

    /// <summary>
    /// Sắp xếp từ theo từng dòng (tọa độ Y giảm dần, X tăng dần) để giữ đúng cấu trúc câu hỏi và đáp án A/B/C/D.
    /// </summary>
    private static string ExtractOrderedPageText(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0)
        {
            return page.Text ?? string.Empty;
        }

        // Nhóm các từ có cùng cao độ Y (dung sai 3.5 pt) thành từng dòng đọc từ trên xuống dưới
        var sortedByY = words
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var lines = new List<List<Word>>();
        foreach (var word in sortedByY)
        {
            double midY = (word.BoundingBox.Bottom + word.BoundingBox.Top) / 2.0;
            var matchedLine = lines.FirstOrDefault(line =>
            {
                var first = line[0];
                double lineMidY = (first.BoundingBox.Bottom + first.BoundingBox.Top) / 2.0;
                double tolerance = Math.Max(3.5, first.BoundingBox.Height * 0.45);
                return Math.Abs(midY - lineMidY) <= tolerance;
            });

            if (matchedLine != null)
            {
                matchedLine.Add(word);
            }
            else
            {
                lines.Add(new List<Word> { word });
            }
        }

        // Sắp xếp lại các dòng từ trên xuống dưới theo tọa độ trung bình, và các từ trong dòng từ trái sang phải
        var orderedLines = lines
            .OrderByDescending(line => line.Average(w => w.BoundingBox.Bottom))
            .Select(line => string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

        return string.Join("\n", orderedLines);
    }

    public static string CleanPuaSymbols(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return text
            .Replace("\uf0a3", " \\le ")
            .Replace("\uf0b3", " \\ge ")
            .Replace("\uf03c", " < ")
            .Replace("\uf03e", " > ")
            .Replace("\uf070", " \\pi ")
            .Replace("\uf0a2", "'")
            .Replace("\uf0e6", "(")
            .Replace("\uf0e8", ")")
            .Replace("\uf0e7", "(")
            .Replace("\uf0eb", ")");
    }

    /// <summary>
    /// Phân tích văn bản đề thi đã trích xuất theo từng trang thành cấu trúc Passages và SingleQuestions.
    /// Được đặt public để unit test trực tiếp không phụ thuộc file PDF nhị phân.
    /// </summary>
    public static ParsedExamDto ParseExamText(string rawFullText, string fileName, int totalPages = 1)
    {
        string normalizedText = Regex.Replace(rawFullText ?? string.Empty, @"\r\n|\r", "\n");
        normalizedText = Regex.Replace(normalizedText, @"\n{2,}", "\n");

        var pageMarkers = PageMarkerRegex.Matches(normalizedText)
            .Select(m => (Index: m.Index, PageNumber: int.TryParse(m.Groups[1].Value, out int p) ? p : 1))
            .ToList();

        int ResolvePageNumber(int charIndex)
        {
            int pageNum = 1;
            foreach (var marker in pageMarkers)
            {
                if (marker.Index <= charIndex)
                {
                    pageNum = marker.PageNumber;
                }
                else
                {
                    break;
                }
            }
            return pageNum;
        }

        // 1. Trích xuất các chùm câu hỏi đọc hiểu / ngữ liệu thí nghiệm (Passages)
        var passages = new List<ParsedPassageDto>();
        foreach (Match match in PassagePattern.Matches(normalizedText))
        {
            string startStr = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[5].Value;
            string endStr = match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[4].Success ? match.Groups[4].Value
                : match.Groups[6].Value;

            if (!int.TryParse(startStr, out int startQ) || !int.TryParse(endStr, out int endQ))
            {
                continue;
            }

            if (endQ < startQ || endQ - startQ > 25)
            {
                continue;
            }

            string content = StripPageMarkers(match.Groups[7].Value).Trim();
            passages.Add(new ParsedPassageDto
            {
                StartQuestion = startQ,
                EndQuestion = endQ,
                Content = content,
                Questions = new List<ParsedQuestionDto>()
            });
        }

        // 2. Trích xuất toàn bộ câu hỏi và đáp án A, B, C, D
        var singleQuestions = new List<ParsedQuestionDto>();
        var seenQuestionNumbers = new HashSet<int>();

        foreach (Match match in QuestionPattern.Matches(normalizedText))
        {
            if (!int.TryParse(match.Groups[1].Value, out int qNum) || qNum <= 0 || qNum > 500)
            {
                continue;
            }

            if (!seenQuestionNumbers.Add(qNum))
            {
                continue;
            }

            string qBodyRaw = StripPageMarkers(match.Groups[2].Value).Trim();
            if (string.IsNullOrWhiteSpace(qBodyRaw))
            {
                continue;
            }

            var (stem, options) = ExtractStemAndOptions(qBodyRaw);
            int pageNum = ResolvePageNumber(match.Index);

            var questionDto = new ParsedQuestionDto
            {
                QuestionNumber = qNum,
                PageNumber = pageNum,
                Content = stem,
                Options = options,
                SuggestedSkillName = "Trắc nghiệm tổng hợp"
            };

            var parentPassage = passages.FirstOrDefault(p => qNum >= p.StartQuestion && qNum <= p.EndQuestion);
            if (parentPassage != null)
            {
                parentPassage.Questions.Add(questionDto);
            }
            else
            {
                singleQuestions.Add(questionDto);
            }
        }

        // 3. Loại bỏ các passage rỗng không có câu hỏi con nào và sắp xếp thứ tự chuẩn
        passages = passages
            .Where(p => p.Questions.Count > 0 || !string.IsNullOrWhiteSpace(p.Content))
            .OrderBy(p => p.StartQuestion)
            .ToList();

        foreach (var p in passages)
        {
            p.Questions = p.Questions.OrderBy(q => q.QuestionNumber).ToList();
        }

        singleQuestions = singleQuestions.OrderBy(q => q.QuestionNumber).ToList();

        int maxPageDetected = Math.Max(1, totalPages);
        foreach (var q in singleQuestions)
            if (q.PageNumber > maxPageDetected) maxPageDetected = q.PageNumber;
        foreach (var p in passages)
            foreach (var q in p.Questions)
                if (q.PageNumber > maxPageDetected) maxPageDetected = q.PageNumber;

        return new ParsedExamDto
        {
            Format = "V-ACT Exam (Local PdfPig Parser)",
            FileName = fileName,
            TotalPages = maxPageDetected,
            TotalPassages = passages.Count,
            TotalSingleQuestions = singleQuestions.Count,
            Passages = passages,
            SingleQuestions = singleQuestions
        };
    }

    private static (string Stem, Dictionary<string, string> Options) ExtractStemAndOptions(string qBodyRaw)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string stem = qBodyRaw;

        var optMatches = OptionPattern.Matches(qBodyRaw);
        // Chỉ coi là khối đáp án hợp lệ nếu tìm thấy ít nhất 2 lựa chọn (có A và B)
        var validOptMatches = new List<Match>();
        foreach (Match m in optMatches)
        {
            string key = m.Groups[1].Value.ToUpperInvariant();
            if (validOptMatches.Count == 0 && key != "A")
            {
                continue;
            }
            validOptMatches.Add(m);
        }

        if (validOptMatches.Count >= 2)
        {
            stem = qBodyRaw[..validOptMatches[0].Index].Trim();
            foreach (var m in validOptMatches)
            {
                string key = m.Groups[1].Value.ToUpperInvariant();
                string val = Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " ");
                if (!options.ContainsKey(key))
                {
                    options[key] = val;
                }
            }
        }
        else
        {
            var errMatch = ErrorIdentificationTailRegex.Match(qBodyRaw);
            if (errMatch.Success)
            {
                stem = qBodyRaw[..errMatch.Index].Trim();
                options["A"] = "A";
                options["B"] = "B";
                options["C"] = "C";
                options["D"] = "D";
            }
        }

        foreach (var k in new[] { "A", "B", "C", "D" })
        {
            if (!options.ContainsKey(k))
            {
                options[k] = string.Empty;
            }
        }

        return (stem, options);
    }

    private static string StripPageMarkers(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return PageMarkerRegex.Replace(input, string.Empty);
    }
}
