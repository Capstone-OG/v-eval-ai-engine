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

    [Fact]
    public void SanitizeAndRepairGeminiJson_WithMarkdownCodeFenceAndPreamble_ExtractsCleanJson()
    {
        string input = """
        Dưới đây là kết quả trích xuất cấu trúc tri thức theo chuẩn SGK GDPT 2018:
        ```json
        {
          "domainId": "dom_chem",
          "domainName": "Hóa học",
          "chapters": [
            {
              "chapterName": "Chương 1: Ester - Lipid",
              "weight": 0.4,
              "lessons": []
            }
          ]
        }
        ```
        Hy vọng kết quả này hữu ích!
        """;

        string cleaned = CurriculumAiService.SanitizeAndRepairGeminiJson(input);
        var doc = System.Text.Json.JsonDocument.Parse(cleaned);
        doc.Should().NotBeNull();
        cleaned.Should().StartWith("{");
        cleaned.Should().EndWith("}");
        cleaned.Should().NotContain("Dưới đây");
        cleaned.Should().NotContain("Hy vọng");
    }

    [Fact]
    public void TryParseCurriculumFromLooseJson_WithRootArrayOfChapters_ParsesDirectly()
    {
        string input = """
        [
          {
            "chapterName": "Chương 1: Ester - Lipid",
            "weight": 0.4,
            "lessons": [
              {
                "lessonName": "Bài 1: Ester",
                "weight": 0.2,
                "patterns": [
                  {
                    "pattern_name": "Dạng 1: Đồng phân danh pháp",
                    "weight": 0.05,
                    "theory_recap": "Khái niệm este",
                    "solution_method": "Bước 1: Viết đồng phân",
                    "duration_seconds": 600,
                    "questions": [
                      {
                        "difficulty_level": 1,
                        "content_latex": "Chất nào sau đây là este?",
                        "option_a": "$HCOOCH_3$",
                        "option_b": "$CH_3COOH$",
                        "option_c": "$C_2H_5OH$",
                        "option_d": "$CH_3CHO$",
                        "correct_option": "A",
                        "explanation": "HCOOCH3 là este metyl fomat"
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
        """;

        var result = CurriculumAiService.TryParseCurriculumFromLooseJson(input, "dom_chem", "Hóa học");
        result.Should().NotBeNull();
        result!.Chapters.Should().HaveCount(1);
        result.Chapters[0].ChapterName.Should().Be("Chương 1: Ester - Lipid");
        result.Chapters[0].Lessons.Should().HaveCount(1);
        result.Chapters[0].Lessons[0].LessonName.Should().Be("Bài 1: Ester");
        result.Chapters[0].Lessons[0].Patterns.Should().HaveCount(1);
        result.Chapters[0].Lessons[0].Patterns[0].PatternName.Should().Be("Dạng 1: Đồng phân danh pháp");
        result.Chapters[0].Lessons[0].Patterns[0].Questions.Should().HaveCount(1);
        result.Chapters[0].Lessons[0].Patterns[0].Questions[0].OptionA.Should().Be("$HCOOCH_3$");
        result.Chapters[0].Lessons[0].Patterns[0].Questions[0].CorrectOption.Should().Be("A");
    }

    [Fact]
    public void NormalizeCurriculumLatex_WithChemistryFormulas_ShouldNotCorruptVietnameseOrDollarPlacement()
    {
        string t1 = "Khái niệm: Khi thay thế nhóm - OH ở nhóm carboxyl (-COOH) của carboxylic acid bằng nhóm -OR' thì được ester. Công thức tổng quát của ester đơn chức, mạch hở : C_nH_{2n}O_2\n\nRCOOR' + NaOH \\xrightarrow{t^\\circ} RCOONa + R'OH\n\nRCOOCH=CH_2 + NaOH \\xrightarrow{t^\\circ} RCOONa + CH_3CHO";
        string out1 = CurriculumAiService.NormalizeCurriculumLatex(t1);

        string t2 = "Triolein có 3 liên kết \\pi ở gốc $C_{17}H_{33}$. 1 mol triolein phản ứng tối đa 3 mol $H_2$. Số mol triolein n = \\frac{17,68}{884} = 0,02 mol \\rightarrow n_{H_2} = 0,06 mol.";
        string out2 = CurriculumAiService.NormalizeCurriculumLatex(t2);

        string t3 = "n_{\\text{triglyceride}} = \\frac{n_{CO_2} - n_{H_2O}}{k - 1}";
        string out3 = CurriculumAiService.NormalizeCurriculumLatex(t3);

        string t4 = "-CH = CH_2. Do đó công thức cấu tạo của ester là CH_3COOCH = CH_2.";
        string out4 = CurriculumAiService.NormalizeCurriculumLatex(t4);

        // Assert 1: Standalone chemical equations are wrapped cleanly without merging lines
        out1.Should().Contain("$RCOOR' + NaOH \\xrightarrow{t^\\circ} RCOONa + R'OH$");
        out1.Should().Contain("$RCOOCH=CH_2 + NaOH \\xrightarrow{t^\\circ} RCOONa + CH_3CHO$");

        // Assert 2: Formulas ending before punctuation keep their closing dollar without swallowing Vietnamese text
        out2.Should().Contain("$C_{17}H_{33}$.");
        out2.Should().NotContain("$C_{17}H_{33}. 1 mol");

        // Assert 3: Equations with fractions are wrapped cleanly as a single formula, never inserting $ into = or denominator
        out3.Should().Be("$n_{\\text{triglyceride}} = \\frac{n_{CO_2} - n_{H_2O}}{k - 1}$");
        out3.Should().NotContain("$=");
        out3.Should().NotContain("k - 1$}");

        // Assert 4: Plain Vietnamese text is never corrupted by stray formulas
        out4.Should().Contain("Do đó công thức cấu tạo của ester là");
    }
}
