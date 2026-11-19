using FluentAssertions;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Infrastructure.Services;
using Xunit;

namespace V_Eval_Ai_Engine.Tests.Infrastructure;

public class CurriculumTaxonomyRegistryTests
{
    [Fact]
    public void DetectDocumentIdentity_ShouldIdentifyMath12_ChanTroiSangTao()
    {
        var identity = CurriculumTaxonomyRegistry.DetectDocumentIdentity(
            "SGK_Toan_12_Tap1_ChanTroiSangTao.pdf",
            "Chương 1: Ứng dụng đạo hàm để khảo sát hàm số. Tính đơn điệu và cực trị.");

        identity.SubjectCode.Should().Be("MATH");
        identity.Grade.Should().Be(12);
        identity.BookEdition.Should().Be("Chân trời sáng tạo");
        identity.IsInDgnlScope.Should().BeTrue();
    }

    [Fact]
    public void DetectDocumentIdentity_ShouldIdentifyLiterature10_AsInScope()
    {
        var identity = CurriculumTaxonomyRegistry.DetectDocumentIdentity(
            "Ngu_Van_10_Tap1.pdf",
            "Bài 1: Tạo lập thế giới (Thần thoại). Thần Trụ Trời.");

        identity.SubjectCode.Should().Be("VIET");
        identity.Grade.Should().Be(10);
        identity.IsInDgnlScope.Should().BeTrue();
    }

    [Fact]
    public void DetectDocumentIdentity_ShouldMarkMusicAndArtAsOutOfDgnlScope()
    {
        var music = CurriculumTaxonomyRegistry.DetectDocumentIdentity("SGK_Am_Nhac_10.pdf", "Giáo dục âm nhạc lớp 10");
        var art = CurriculumTaxonomyRegistry.DetectDocumentIdentity("SGK_My_Thuat_11.pdf", "Mỹ thuật và Hội họa");

        music.SubjectCode.Should().Be("OUT_OF_SCOPE_DGNL");
        music.IsInDgnlScope.Should().BeFalse();

        art.SubjectCode.Should().Be("OUT_OF_SCOPE_DGNL");
        art.IsInDgnlScope.Should().BeFalse();
    }

    [Fact]
    public void ClassifyAndEnrichChunk_ShouldExtractTaxonomy_Placeholders_AndPreserveLatex()
    {
        string subCode = "MATH";
        string subName = "Toán học";
        int grade = 12;
        string book = "Chân trời sáng tạo";
        string chap = "";
        string les = "";
        string pat = "";

        var chunk = new TextbookChunkDto
        {
            ChunkIndex = 1,
            PageNumber = 14,
            Text = "[TAXONOMY: subject=\"MATH\" | grade=\"12\" | chapter=\"Chương 1: Ứng dụng đạo hàm\" | lesson=\"Bài 1: Tính đơn điệu và cực trị\" | pattern=\"Dạng 2: Tìm m để hàm số đơn điệu\" | section=\"EXAMPLE\"]\n" +
                   "Ví dụ 2: Xét hàm số $y = x^3 - 3x + 1$. Ta có $y' = 3x^2 - 3$.\n" +
                   "[IMAGE_PLACEHOLDER: id=\"img_p14_1\" type=\"GRAPH_2D\" desc=\"Đồ thị hàm số bậc ba y = x^3 - 3x + 1\"]"
        };

        CurriculumTaxonomyRegistry.ClassifyAndEnrichChunk(
            chunk,
            ref subCode,
            ref subName,
            ref grade,
            ref book,
            ref chap,
            ref les,
            ref pat);

        chunk.SubjectCode.Should().Be("MATH");
        chunk.Grade.Should().Be(12);
        chunk.ChapterName.Should().Contain("Ứng dụng đạo hàm");
        chunk.LessonName.Should().Contain("Tính đơn điệu");
        chunk.PatternName.Should().Contain("Dạng 2");
        chunk.SectionType.Should().Be("EXAMPLE");
        chunk.HasDiagram.Should().BeTrue();
        chunk.ImagePlaceholders.Should().HaveCount(1);
        chunk.ImagePlaceholders[0].DiagramType.Should().Be("GRAPH_2D");
        chunk.Text.Should().Contain("[IMAGE_PLACEHOLDER:");
        chunk.Text.Should().Contain("$y = x^3 - 3x + 1$");
    }

    [Fact]
    public void SynthesizeCurriculumFromChunks_ShouldBuildValidImportDto()
    {
        var chunks = new List<TextbookChunkDto>
        {
            new()
            {
                ChunkIndex = 1,
                PageNumber = 10,
                SubjectCode = "CHEM",
                SubjectName = "Hóa học",
                Grade = 12,
                BookEdition = "Chân trời sáng tạo",
                ChapterName = "Chương 1: Ester - Lipid",
                LessonName = "Bài 1: Ester và Lipid",
                PatternName = "Dạng 2: Bài toán xà phòng hóa",
                SectionType = "THEORY",
                Text = "Phản ứng xà phòng hóa: $$RCOOR' + NaOH \\xrightarrow{t^o} RCOONa + R'OH$$",
                DpiUsed = 56
            }
        };

        var dto = CurriculumTaxonomyRegistry.SynthesizeCurriculumFromChunks("Hoa_12.pdf", "", chunks);

        dto.SubjectCode.Should().Be("CHEM");
        dto.Grade.Should().Be(12);
        dto.Chapters.Should().HaveCount(1);
        dto.Chapters[0].Lessons.Should().HaveCount(1);
        dto.Chapters[0].Lessons[0].Patterns.Should().HaveCount(1);
        dto.Chapters[0].Lessons[0].Patterns[0].TheoryRecap.Should().Contain("RCOOR'");
    }

    [Fact]
    public void GetStandardSkeletonChapters_ShouldMatchExactGrade11_AndNotInjectStubDescriptions()
    {
        var chem11 = CurriculumTaxonomyRegistry.GetStandardSkeletonChapters("CHEM", 11);

        chem11.Should().NotBeEmpty();
        chem11[0].Title.Should().Contain("Cân bằng");
        chem11.Should().NotContain(c =>
            c.Title.Contains("Lớp 10", StringComparison.OrdinalIgnoreCase) ||
            c.Title.Contains("Nguyên tố nhóm VIIA", StringComparison.OrdinalIgnoreCase));

        var allPatterns = chem11
            .SelectMany(c => c.Lessons)
            .SelectMany(l => l.Patterns)
            .ToList();
        allPatterns.Should().NotBeEmpty();
        allPatterns.Should().OnlyContain(p => string.IsNullOrEmpty(p.Description) && string.IsNullOrEmpty(p.Method));
    }

    [Fact]
    public void DetectDocumentIdentity_ShouldHandleOldStyleVietnameseAccentHoaHoc11()
    {
        var identity = CurriculumTaxonomyRegistry.DetectDocumentIdentity(
            "SGK_Hoá_học_11_CTST.pdf",
            "Chương 1: Cân bằng hoá học. Bài 1: Khái niệm về cân bằng hoá học.");

        identity.SubjectCode.Should().Be("CHEM");
        identity.Grade.Should().Be(11);
        identity.IsInDgnlScope.Should().BeTrue();
    }
}

