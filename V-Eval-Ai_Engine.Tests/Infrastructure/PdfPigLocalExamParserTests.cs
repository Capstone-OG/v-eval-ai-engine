using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Infrastructure.Services;
using Xunit;

namespace V_Eval_Ai_Engine.Tests.Infrastructure;

public class PdfPigLocalExamParserTests
{
    [Fact]
    public void ParseExamText_ShouldExtractSingleQuestionsPassagesAndPageNumbersCleanly()
    {
        string sampleExamText =
            "--- PAGE_1 ---\n" +
            "ĐỀ THI THỬ ĐÁNH GIÁ NĂNG LỰC 2026\n" +
            "Câu 1: Giá trị của góc \uf070 rad bằng bao nhiêu độ?\n" +
            "A. 90 độ    B. 180 độ\n" +
            "C. 270 độ   D. 360 độ\n" +
            "Câu 2. Tìm điều kiện để x \uf0a3 5 thỏa mãn bất phương trình.\n" +
            "A. x nhỏ hơn hoặc bằng 5\n" +
            "B. x lớn hơn 5\n" +
            "C. x bằng 0\n" +
            "D. Vô nghiệm\n" +
            "--- PAGE_2 ---\n" +
            "Dựa vào thông tin dưới đây để trả lời các câu từ 3 đến 4\n" +
            "Trong quá trình quang hợp ở thực vật C3, pha tối diễn ra trong chất nền lục lạp theo chu trình Calvin.\n" +
            "Câu 3: Pha tối của quang hợp diễn ra ở đâu?\n" +
            "A. Màng tilacoit B. Chất nền lục lạp C. Tế bào chất D. Ti thể\n" +
            "Câu 4: Chu trình cố định CO2 ở thực vật C3 là gì?\n" +
            "A. Chu trình Krebs B. Chu trình Calvin C. Đường phân D. Chu trình CAM\n" +
            "Questions 5-6: Read the passage carefully.\n" +
            "Renewable energy sources such as solar and wind power reduce carbon emissions.\n" +
            "Question 5: Which energy source is mentioned in the passage?\n" +
            "A. Coal B. Solar power C. Nuclear D. Diesel\n" +
            "Question 6: What do renewable sources reduce?\n" +
            "A. Emissions B. Sunlight C. Rainfall D. Wind speed\n" +
            "--------------- HẾT ---------------\n";

        var parsed = PdfPigLocalExamParser.ParseExamText(
            PdfPigLocalExamParser.CleanPuaSymbols(sampleExamText),
            "de-thi-thu-2026.pdf",
            totalPages: 2);

        Assert.Equal("de-thi-thu-2026.pdf", parsed.FileName);
        Assert.Equal(2, parsed.TotalPages);
        Assert.Equal(2, parsed.TotalSingleQuestions);
        Assert.Equal(2, parsed.TotalPassages);

        Assert.Equal(1, parsed.SingleQuestions[0].QuestionNumber);
        Assert.Equal(1, parsed.SingleQuestions[0].PageNumber);
        Assert.Contains("\\pi", parsed.SingleQuestions[0].Content);
        Assert.Equal("180 độ", parsed.SingleQuestions[0].Options["B"]);

        Assert.Equal(2, parsed.SingleQuestions[1].QuestionNumber);
        Assert.Contains("\\le", parsed.SingleQuestions[1].Content);
        Assert.Equal("x nhỏ hơn hoặc bằng 5", parsed.SingleQuestions[1].Options["A"]);

        Assert.Equal(3, parsed.Passages[0].StartQuestion);
        Assert.Equal(4, parsed.Passages[0].EndQuestion);
        Assert.Contains("chu trình Calvin", parsed.Passages[0].Content);
        Assert.Equal(2, parsed.Passages[0].Questions.Count);
        Assert.Equal(2, parsed.Passages[0].Questions[0].PageNumber);
        Assert.Equal("Chất nền lục lạp", parsed.Passages[0].Questions[0].Options["B"]);

        Assert.Equal(5, parsed.Passages[1].StartQuestion);
        Assert.Equal(6, parsed.Passages[1].EndQuestion);
        Assert.Equal(2, parsed.Passages[1].Questions.Count);
        Assert.Equal("Solar power", parsed.Passages[1].Questions[0].Options["B"]);
    }

    [Fact]
    public void ResolveGeminiEndpointUrl_ShouldMatchTextbookPipelineAndRespectCustomBaseUrl()
    {
        string defaultUrl = MultiCloudExamParserService.ResolveGeminiEndpointUrl(
            "gemini-3.1-flash-lite",
            "AQ.Ab8RN6TestKey123");

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key=AQ.Ab8RN6TestKey123",
            defaultUrl);

        string customProxyUrl = MultiCloudExamParserService.ResolveGeminiEndpointUrl(
            "gemini-3.5-flash",
            "AQ.Ab8RN6TestKey123",
            "https://gateway.internal.local/v1beta/");

        Assert.Equal(
            "https://gateway.internal.local/v1beta/models/gemini-3.5-flash:generateContent?key=AQ.Ab8RN6TestKey123",
            customProxyUrl);
    }

    [Fact]
    public void MergeBatchExamResults_ShouldStitchCrossPagePassagesAndPreserveAiAnswersAndExplanations()
    {
        var batch1 = new ParsedExamDto
        {
            SingleQuestions = new List<ParsedQuestionDto>
            {
                new()
                {
                    QuestionNumber = 31,
                    PageNumber = 8,
                    Content = "Jane was careful. She didn't start the test until she ______ all the instructions.",
                    Options = new() { ["A"] = "read", ["B"] = "reads", ["C"] = "had read", ["D"] = "had been reading" },
                    CorrectOption = "C.",
                    Explanation = "Hành động đọc hướng dẫn xảy ra trước hành động bắt đầu bài kiểm tra trong quá khứ -> dùng quá khứ hoàn thành (had read).",
                    DifficultyLevel = 2,
                    SuggestedSkillName = "Tiếng Anh · Thì quá khứ hoàn thành"
                }
            },
            Passages = new List<ParsedPassageDto>
            {
                new()
                {
                    StartQuestion = 53,
                    EndQuestion = 54,
                    Content = "Climate change is making the world's weather more extreme.",
                    Questions = new List<ParsedQuestionDto>()
                }
            }
        };

        var batch2 = new ParsedExamDto
        {
            SingleQuestions = new List<ParsedQuestionDto>
            {
                new()
                {
                    QuestionNumber = 53,
                    PageNumber = 10,
                    Content = "What is the main topic of the passage?",
                    Options = new() { ["A"] = "Climate change", ["B"] = "Tourism", ["C"] = "Cooking", ["D"] = "History" },
                    CorrectOption = "A",
                    Explanation = "Đoạn văn nói về biến đổi khí hậu làm thời tiết cực đoan hơn.",
                    DifficultyLevel = 2
                },
                new()
                {
                    QuestionNumber = 54,
                    PageNumber = 10,
                    Content = "Which phenomenon becomes more extreme?",
                    Options = new() { ["A"] = "Traffic", ["B"] = "Weather", ["C"] = "Prices", ["D"] = "Schools" },
                    CorrectOption = "(B)",
                    Explanation = "Theo câu đầu: world's weather more extreme.",
                    DifficultyLevel = 1
                }
            }
        };

        var merged = MultiCloudExamParserService.MergeBatchExamResults(new[] { batch1, batch2 }, "de-dgnl.pdf", 16);

        Assert.Equal(16, merged.TotalPages);
        Assert.Equal(1, merged.TotalSingleQuestions);
        Assert.Equal(1, merged.TotalPassages);
        Assert.Equal("C", merged.SingleQuestions[0].CorrectOption);
        Assert.Contains("quá khứ hoàn thành", merged.SingleQuestions[0].Explanation);

        // Cross-page questions 53 and 54 from batch2 must be stitched into passage 53-54 from batch1
        Assert.Equal(2, merged.Passages[0].Questions.Count);
        Assert.Equal(53, merged.Passages[0].Questions[0].QuestionNumber);
        Assert.Equal("A", merged.Passages[0].Questions[0].CorrectOption);
        Assert.Equal(54, merged.Passages[0].Questions[1].QuestionNumber);
        Assert.Equal("B", merged.Passages[0].Questions[1].CorrectOption);
    }

    [Fact]
    public void ParseExamText_ShouldHandleBareNumberedQuestionsAndPassagesWithoutSwallowing()
    {
        string text =
            "--- PAGE_10 ---\n" +
            "Thí nghiệm 1. Một sinh viên thực hiện quá trình điện phân dung dịch chứa Pb(NO3)2 và Mg(NO3)2.\n" +
            "Dựa theo dãy điện hóa đã cho ở trên và từ Thí nghiệm 1, hãy cho biết:\n" +
            "91. Bán phản ứng nào xảy ra ở anot?\n" +
            "A. Pb -> Pb2+ + 2e.    B. Mg -> Mg2+ + 2e.\n" +
            "C. 2H2O -> O2 + 4H+ + 4e.    D. 4NO3- -> 2N2O5 + O2 + 4e.\n" +
            "92. Giá trị pH của dung dịch thay đổi như thế nào?\n" +
            "A. pH tăng do OH- sinh ra ở catot.\n" +
            "B. pH giảm do H+ sinh ra ở anot.\n" +
            "C. pH không đổi do không có H+ và OH- sinh ra.\n" +
            "D. pH không đổi do lượng H+ sinh ra ở anot bằng với lượng OH- sinh ra ở catot.\n" +
            "--- PAGE_15 ---\n" +
            "Dựa vào thông tin dưới đây để trả lời các câu từ 118 đến 120\n" +
            "Từ năm 1897, sau khi đàn áp cuộc khởi nghĩa cuối cùng trong phong trào Cần Vương là khởi nghĩa Hương Khê...\n" +
            "118. Lực lượng xã hội nào đã có đóng góp quan trọng đối với phong trào yêu nước ở Việt Nam trong thập niên đầu thế kỉ XX\n" +
            "A. Công nhân, nông dân B. Trí thức, tiểu tư sản thành thị C. Trí thức Nho học D. Tư sản dân tộc\n" +
            "119. Kinh tế Việt Nam phát triển như thế nào trong cuộc khai thác thuộc địa của thực dân Pháp?\n" +
            "A. Bị kìm hãm, không phát triển được B. Phát triển chậm và không toàn diện C. Phụ thuộc D. Tư bản\n" +
            "120. Giai cấp, tầng lớp nào đã tiếp thu ý thức hệ dân chủ tư sản\n" +
            "A. Tư sản B. Tiểu tư sản C. Trí thức D. Tư sản và tiểu tư sản\n" +
            "--------------- HẾT ---------------\n";

        var parsed = PdfPigLocalExamParser.ParseExamText(
            PdfPigLocalExamParser.CleanPuaSymbols(text),
            "de-mau-15-trang.pdf",
            totalPages: 15);

        // Questions 91 and 92 (bare numbered without "Câu") must be extracted as single questions
        var q91 = parsed.SingleQuestions.FirstOrDefault(q => q.QuestionNumber == 91);
        Assert.NotNull(q91);
        Assert.Equal(10, q91.PageNumber);
        Assert.Contains("Bán phản ứng nào xảy ra ở anot", q91.Content);
        Assert.Equal("Pb -> Pb2+ + 2e.", q91.Options["A"]);

        var q92 = parsed.SingleQuestions.FirstOrDefault(q => q.QuestionNumber == 92);
        Assert.NotNull(q92);
        Assert.Contains("Giá trị pH của dung dịch", q92.Content);
        Assert.Equal("pH tăng do OH- sinh ra ở catot.", q92.Options["A"]);

        // Passage 118-120 must NOT swallow questions 118, 119, 120 into its content
        var passage = parsed.Passages.FirstOrDefault(p => p.StartQuestion == 118 && p.EndQuestion == 120);
        Assert.NotNull(passage);
        Assert.DoesNotContain("118.", passage.Content);
        Assert.DoesNotContain("Lực lượng xã hội nào", passage.Content);
        Assert.Equal(3, passage.Questions.Count);

        Assert.Equal(118, passage.Questions[0].QuestionNumber);
        Assert.Contains("Lực lượng xã hội nào", passage.Questions[0].Content);
        Assert.Equal("Trí thức Nho học", passage.Questions[0].Options["C"]);

        Assert.Equal(119, passage.Questions[1].QuestionNumber);
        Assert.Contains("Kinh tế Việt Nam", passage.Questions[1].Content);

        Assert.Equal(120, passage.Questions[2].QuestionNumber);
        Assert.Contains("Giai cấp, tầng lớp nào", passage.Questions[2].Content);
    }
}
