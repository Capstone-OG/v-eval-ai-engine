using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Infrastructure.Services;

namespace V_Eval_Ai_Engine.Tests.Infrastructure;

public class PromptTemplateServiceTests
{
    private readonly Mock<ILogger<PromptTemplateService>> _loggerMock = new();

    [Fact]
    public async Task RenderPromptAsync_WithKnownTemplate_RendersTokensCorrectly()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);
        var variables = new Dictionary<string, string>
        {
            ["domainName"] = "Toán học & Tư duy định lượng",
            ["domainScope"] = "TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS)",
            ["depthDirective"] = "Bóc tách 3 bài học",
            ["sampleHeading"] = "### 📄 Bài toán mẫu áp dụng phương pháp:",
            ["tipHeading"] = "### 💡 Kỹ thuật Casio:",
            ["tipGuideline"] = "Mẹo bấm máy Casio",
            ["sampleKey"] = "Bài toán mẫu:",
            ["domainId"] = "domain-math",
            ["textSnippet"] = "Đạo hàm và hàm số đồng biến nghịch biến"
        };

        // Act
        string rendered = await service.RenderPromptAsync("CurriculumIngestion.prompt.txt", variables);

        // Assert
        rendered.Should().NotBeNullOrWhiteSpace();
        rendered.Should().Contain("Toán học & Tư duy định lượng");
        rendered.Should().Contain("TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS)");
        rendered.Should().Contain("domain-math");
        rendered.Should().NotContain("{{domainName}}");
        rendered.Should().NotContain("{{domainScope}}");
    }

    [Fact]
    public async Task RenderPromptAsync_WithPatternAnalysisTemplate_RendersAllVariables()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);
        var variables = new Dictionary<string, string>
        {
            ["subjectType"] = "toán",
            ["patternName"] = "Tìm cực trị hàm số bậc ba",
            ["lessonName"] = "Khảo sát hàm số",
            ["domainName"] = "Toán học",
            ["casioGuideline"] = "Bấm Mode 9-2-3"
        };

        // Act
        string rendered = await service.RenderPromptAsync("PatternAnalysis.prompt.txt", variables);

        // Assert
        rendered.Should().Contain("Tìm cực trị hàm số bậc ba");
        rendered.Should().Contain("Khảo sát hàm số");
        rendered.Should().Contain("Bấm Mode 9-2-3");
        rendered.Should().NotContain("{{patternName}}");
    }

    [Fact]
    public async Task RenderPromptAsync_WithExamIngestionVerbatimOcrTemplate_LoadsAccurately()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);

        // Act
        string rendered = await service.RenderPromptAsync("ExamIngestionVerbatimOcr.prompt.txt");

        // Assert
        rendered.Should().NotBeNullOrWhiteSpace();
        rendered.Should().Contain("HIGH-PRECISION VERBATIM OCR");
        rendered.Should().Contain("[MATH_GRAPH_3D]");
        rendered.Should().Contain("[VARIATION_TABLE]");
        rendered.Should().Contain("[MATH_GRAPH]");
    }

    [Fact]
    public async Task RenderPromptAsync_WithSocraticTemplate_RendersPedagogyPrompt()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);
        var variables = new Dictionary<string, string>
        {
            ["retrievedKnowledge"] = "Lý thuyết đạo hàm"
        };

        // Act
        string rendered = await service.RenderPromptAsync("SocraticTutor.prompt.txt", variables);

        // Assert
        rendered.Should().Contain("AI Tutor");
        rendered.Should().Contain("Socratic Method");
        rendered.Should().Contain("Lý thuyết đạo hàm");
        rendered.Should().NotContain("{{retrievedKnowledge}}");
    }

    [Fact]
    public async Task RenderPromptAsync_WithNonExistentTemplate_ReturnsDefenseFallbackPrompt()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);
        var variables = new Dictionary<string, string>
        {
            ["topic"] = "Trắc nghiệm Lịch sử"
        };

        // Act
        string rendered = await service.RenderPromptAsync("NonExistentTemplate.txt", variables);

        // Assert
        rendered.Should().NotBeNullOrWhiteSpace();
        rendered.Should().Contain("CHUYÊN GIA SƯ PHẠM V-EVAL");
    }

    [Fact]
    public async Task RenderPromptAsync_CachesTemplateContentInMemory()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);
        var variables = new Dictionary<string, string>
        {
            ["domainName"] = "Văn học"
        };

        // Act - Call twice
        string firstCall = await service.RenderPromptAsync("CurriculumIngestion.prompt.txt", variables);
        string secondCall = await service.RenderPromptAsync("CurriculumIngestion.prompt.txt", variables);

        // Assert
        firstCall.Should().Be(secondCall);
    }

    [Fact]
    public async Task RenderPromptAsync_WithCurriculumTemplate_IncludesMaximizingPatternsRule()
    {
        // Arrange
        var service = new PromptTemplateService(_loggerMock.Object);
        var variables = new Dictionary<string, string>
        {
            ["domainName"] = "Hóa học",
            ["domainScope"] = "TỪNG DẠNG BÀI TOÁN CỤ THỂ (PATTERNS)",
            ["depthDirective"] = "Tối đa hóa các dạng bài",
            ["sampleHeading"] = "### 📄 Bài toán mẫu:",
            ["tipHeading"] = "### 💡 Mẹo:",
            ["tipGuideline"] = "Mẹo giải nhanh",
            ["sampleKey"] = "Bài toán mẫu:",
            ["domainId"] = "dom_chem",
            ["textSnippet"] = "Chuyên đề Ester - Lipid"
        };

        // Act
        string rendered = await service.RenderPromptAsync("CurriculumIngestion.prompt.txt", variables);

        // Assert
        rendered.Should().Contain("TỐI ĐA HÓA CÁC DẠNG BÀI");
        rendered.Should().Contain("Dạng 3:");
    }

    [Fact]
    public void SanitizeAndRepairGeminiJson_WithUnescapedQuotesInChemistryProse_RepairsToValidJson()
    {
        string input = """
        {
          "domainId": "dom_chem",
          "domainName": "Hóa học",
          "chapters": [
            {
              "chapterName": "Chương 1: Este - Lipit",
              "weight": 0.35,
              "lessons": [
                {
                  "lessonName": "Bài 1: Este",
                  "weight": 0.1,
                  "patterns": [
                    {
                      "patternName": "Dạng 1: Đồng phân danh pháp",
                      "weight": 0.05,
                      "theoryRecap": "Este có mùi thơm như "isoamyl axetat", là este có mùi chuối chín. "Aspirin", là thuốc giảm đau.",
                      "solutionMethod": "Bước 1: Viết đồng phân\nBước 2: Gọi tên theo quy tắc",
                      "questions": []
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        string cleaned = CurriculumAiService.SanitizeAndRepairGeminiJson(input);
        var doc = System.Text.Json.JsonDocument.Parse(cleaned);
        doc.Should().NotBeNull();
        cleaned.Should().Contain("'isoamyl axetat'");
        cleaned.Should().Contain("'Aspirin'");
    }

    [Fact]
    public void SanitizeAndRepairGeminiJson_WithCStyleAndLineComments_StripsCommentsAndParsesSuccessfully()
    {
        string input = """
        {
          "domainId": "dom_chem",
          /* LƯU Ý BẮT BUỘC VỀ SỐ LƯỢNG DẠNG BÀI:
             Trích xuất từ 6 đến 12 dạng bài */
          "domainName": "Hóa học",
          // Chú thích dòng
          "chapters": []
        }
        """;

        string cleaned = CurriculumAiService.SanitizeAndRepairGeminiJson(input);
        var doc = System.Text.Json.JsonDocument.Parse(cleaned);
        doc.Should().NotBeNull();
        cleaned.Should().NotContain("/*");
        cleaned.Should().NotContain("*/");
    }
}
