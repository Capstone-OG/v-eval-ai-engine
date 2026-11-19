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
}
