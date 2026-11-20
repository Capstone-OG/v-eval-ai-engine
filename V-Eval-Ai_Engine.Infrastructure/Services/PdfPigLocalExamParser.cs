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
        @"(?:Dựa (?:vào|theo) (?:các )?(?:thông tin|đoạn trích|văn bản|dữ liệu|dãy điện hóa)[^0-9\r\n]{0,80}?(?:câu(?:\s+hỏi)?\s+từ|từ\s+câu)\s+(\d+)\s+(?:đến|\-|–)\s+(\d+)[^\r\n]*|Đọc (?:đoạn trích|văn bản|thông tin)[^0-9\r\n]{0,80}?(?:câu(?:\s+hỏi)?\s+từ|từ\s+câu)\s+(\d+)\s+(?:đến|\-|–)\s+(\d+)[^\r\n]*|Questions?\s+(\d+)\s*[\-–to]+\s*(\d+)[^\r\n]*)\r?\n(.*?)(?=(?:\r?\n\s*(?:(?:Câu|Question)\s+\d+|\d{1,3}\s*[.:]\s+[^\r\n]+)|$))",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex QuestionPattern = new(
        @"(?:^|\r?\n)\s*(?:(?:Câu|Question)\s+(\d+)|(\d{1,3})\s*[.:])\s*[:.]?\s*(.*?)(?=(?:\r?\n\s*(?:(?:Câu|Question)\s+\d+|\d{1,3}\s*[.:]\s+[^\r\n]+)|(?:\r?\n\s*(?:Dựa (?:vào|theo) (?:các )?(?:thông tin|đoạn trích|văn bản|dữ liệu)|Đọc (?:đoạn trích|văn bản|thông tin)|Questions?\s+\d+\s*[\-–to]+\s*\d+))|[-–—]{3,}\s*HẾT\s*[-–—]{3,}|$))",
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
    /// <summary>
    /// Thuật toán Line-Following: ghép từ thành dòng theo hướng đọc tự nhiên (từ trái sang phải, từ trên xuống dưới).
    /// Khắc phục triệt để trang scan bị nghiêng, không trộn lẫn dòng liền kề và không xé lẻ chữ cái đơn (o, g, n, u, V, i).
    /// </summary>
    private static string ExtractOrderedPageText(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0)
        {
            return page.Text ?? string.Empty;
        }

        var unvisited = new List<Word>(words);
        var lines = new List<List<Word>>();

        while (unvisited.Count > 0)
        {
            // Tìm từ cao nhất trang trong dải 6pt đầu tiên, chọn từ nằm bên trái nhất để bắt đầu dòng mới
            double topY = unvisited.Max(w => (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0);
            var topBand = unvisited
                .Where(w => topY - ((w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0) <= 6.0)
                .ToList();
            var start = topBand.OrderBy(w => w.BoundingBox.Left).First();

            var currentLine = new List<Word> { start };
            unvisited.Remove(start);

            var curr = start;
            while (true)
            {
                double currMidY = (curr.BoundingBox.Bottom + curr.BoundingBox.Top) / 2.0;
                double currHeight = Math.Max(curr.BoundingBox.Height, 6.0);
                double yTol = Math.Max(4.5, currHeight * 0.5);

                // Tìm từ tiếp theo ngay bên phải (trong dải khoảng cách từ <= 65pt và cao độ Y lệch <= yTol)
                var candidates = unvisited
                    .Where(w =>
                    {
                        double wMidY = (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0;
                        double dx = w.BoundingBox.Left - curr.BoundingBox.Right;
                        return w.BoundingBox.Left >= curr.BoundingBox.Right - 5.0 &&
                               dx <= 65.0 &&
                               Math.Abs(wMidY - currMidY) <= yTol;
                    })
                    .ToList();

                if (candidates.Count == 0) break;

                // Chọn từ gần nhất bên phải
                var nextW = candidates
                    .OrderBy(w => w.BoundingBox.Left)
                    .ThenBy(w => Math.Abs(((w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0) - currMidY))
                    .First();
                currentLine.Add(nextW);
                unvisited.Remove(nextW);
                curr = nextW;
            }

            lines.Add(currentLine);
        }

        // Sắp xếp các dòng đã ghép từ trên xuống dưới theo tọa độ Y trung bình
        var orderedLines = lines
            .OrderByDescending(l => l.Average(w => (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0))
            .Select(l => string.Join(" ", l.Select(w => w.Text)));

        return string.Join("\n", orderedLines);
    }

    public static string CleanPuaSymbols(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = text
            .Replace("\uf0a3", " \\le ")
            .Replace("\uf0b3", " \\ge ")
            .Replace("\uf03c", " < ")
            .Replace("\uf03e", " > ")
            .Replace("\uf03d", " = ")
            .Replace("\uf02b", " + ")
            .Replace("\uf02d", " - ")
            .Replace("\uf0b1", " \\pm ")
            .Replace("\uf0b4", " \\times ")
            .Replace("\uf0d7", " \\times ")
            .Replace("\uf0b8", " \\div ")
            .Replace("\uf0b9", " \\neq ")
            .Replace("\uf0a5", " \\infty ")
            .Replace("\uf0d6", " \\sqrt ")
            .Replace("", " \\sqrt ")
            .Replace("\uf0ce", " \\in ")
            .Replace("\uf0cf", " \\notin ")
            .Replace("\uf0c7", " \\cap ")
            .Replace("\uf0c8", " \\cup ")
            .Replace("\uf0c6", " \\varnothing ")
            .Replace("\uf0de", " \\Rightarrow ")
            .Replace("\uf0ae", " \\rightarrow ")
            .Replace("\uf0db", " \\Leftrightarrow ")
            .Replace("\uf0ab", " \\leftrightarrow ")
            .Replace("\uf044", " \\Delta ")
            .Replace("\uf061", " \\alpha ")
            .Replace("\uf062", " \\beta ")
            .Replace("\uf067", " \\gamma ")
            .Replace("\uf064", " \\delta ")
            .Replace("\uf06d", " \\mu ")
            .Replace("\uf070", " \\pi ")
            .Replace("\uf077", " \\omega ")
            .Replace("\uf0a2", "'")
            .Replace("\uf0e6", "(")
            .Replace("\uf0e8", ")")
            .Replace("\uf0e7", "(")
            .Replace("\uf0eb", ")");

        s = Regex.Replace(s, @"\b([a-zA-Z])\s*≡\s*(log|ln|sin|cos|tan|\d+)", "$1 = $2");
        s = Regex.Replace(s, @"(\d+(?:[.,]\d+)?)\s*\\circ\s*([CFKcfk])(?![A-Za-z])", "$1^\\circ\\text{$2}");
        s = Regex.Replace(s, @"\\circ\s*([CFKcfk])(?![A-Za-z])", "^\\circ\\text{$1}");
        return s;
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
            string qNumStr = match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[2].Value;

            if (!int.TryParse(qNumStr, out int qNum) || qNum <= 0 || qNum > 500)
            {
                continue;
            }

            if (!seenQuestionNumbers.Add(qNum))
            {
                continue;
            }

            string qBodyRaw = StripPageMarkers(match.Groups[3].Value).Trim();
            if (string.IsNullOrWhiteSpace(qBodyRaw))
            {
                continue;
            }

            var (stem, options) = ExtractStemAndOptions(qBodyRaw);

            // Nếu câu hỏi được match theo dạng số trần (vd: "118." hoặc "91." không có tiền tố "Câu"),
            // bắt buộc phải có ít nhất phương án A và B để tránh nhận nhầm danh sách đánh số/thí nghiệm
            bool isExplicit = match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value);
            bool hasValidOptions = !string.IsNullOrEmpty(options.GetValueOrDefault("A")) && !string.IsNullOrEmpty(options.GetValueOrDefault("B"));
            if (!isExplicit && !hasValidOptions)
            {
                seenQuestionNumbers.Remove(qNum);
                continue;
            }
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
