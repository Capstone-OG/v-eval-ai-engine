using FluentAssertions;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Infrastructure.Services;
using Xunit;

namespace V_Eval_Ai_Engine.Tests.Infrastructure;

public class ExamNodeClassificationTests
{
    private static List<ExamTaxonomyNodeRefDto> BuildSampleTaxonomy() => new()
    {
        new ExamTaxonomyNodeRefDto
        {
            SkillId = "11111111-1111-1111-1111-111111111111",
            DomainName = "Toán học & Tư duy định lượng",
            ChapterName = "Chương 1: Ứng dụng đạo hàm để khảo sát hàm số (Toán học - Lớp 12)",
            LessonName = "Bài 1: Tính đơn điệu và cực trị của hàm số",
            PatternName = "Dạng 1: Tìm cực trị hàm số đa thức"
        },
        new ExamTaxonomyNodeRefDto
        {
            SkillId = "22222222-2222-2222-2222-222222222222",
            DomainName = "Tư duy Khoa học / Khoa học tự nhiên",
            ChapterName = "Chương 1: Ester - Lipid (Hóa học - Lớp 12)",
            LessonName = "Bài 1: Ester - Lipid",
            PatternName = "Dạng 2: Phản ứng xà phòng hóa chất béo"
        },
        new ExamTaxonomyNodeRefDto
        {
            SkillId = "33333333-3333-3333-3333-333333333333",
            DomainName = "Tư duy Khoa học / Khoa học tự nhiên",
            ChapterName = "Chương 1: Dao động (Vật lí - Lớp 11)",
            LessonName = "Bài 2: Con lắc lò xo và con lắc đơn",
            PatternName = "Dạng 1: Chu kì dao động điều hòa con lắc lò xo"
        }
    };

    [Fact]
    public void ClassifyQuestionLexically_ShouldMatchExactMathChapterAndLesson()
    {
        var nodes = BuildSampleTaxonomy();
        var q = new ExamQuestionClassifyInputDto
        {
            QuestionNumber = 12,
            Content = "Cho hàm số y = x^3 - 3x + 2. Tìm giá trị cực đại và cực trị của hàm số đa thức.",
            SuggestedSkillName = "Cực trị hàm số"
        };

        var mapping = MultiCloudExamParserService.ClassifyQuestionLexically(q, nodes, "MATH");

        mapping.Should().NotBeNull();
        mapping!.SkillId.Should().Be("11111111-1111-1111-1111-111111111111");
        mapping.ChapterName.Should().Contain("Ứng dụng đạo hàm");
        mapping.LessonName.Should().Contain("cực trị");
        mapping.Confidence.Should().BeGreaterOrEqualTo(0.6);
    }

    [Fact]
    public void ClassifyQuestionLexically_ShouldMatchChemistryEsterQuestionAcrossGeneralExam()
    {
        var nodes = BuildSampleTaxonomy();
        var q = new ExamQuestionClassifyInputDto
        {
            QuestionNumber = 94,
            Content = "Đun nóng hoàn toàn chất béo tristearin với dung dịch NaOH xảy ra phản ứng xà phòng hóa.",
            SuggestedSkillName = "Phản ứng xà phòng hóa ester"
        };

        var mapping = MultiCloudExamParserService.ClassifyQuestionLexically(q, nodes, "GENERAL");

        mapping.Should().NotBeNull();
        mapping!.SkillId.Should().Be("22222222-2222-2222-2222-222222222222");
        mapping.ChapterName.Should().Contain("Ester - Lipid");
    }
}

