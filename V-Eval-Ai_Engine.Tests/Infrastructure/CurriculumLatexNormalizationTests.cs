using FluentAssertions;
using V_Eval_Ai_Engine.Infrastructure.Services;
using Xunit;

namespace V_Eval_Ai_Engine.Tests.Infrastructure;

public class CurriculumLatexNormalizationTests
{
    [Fact]
    public void Test_Explanation_Full()
    {
        string input = "Vì X là ester no đơn chức mạch hở nên n_{H_2O} = n_{CO_2} = 0,3\\text{mol}. Bảo toàn nguyên tố O: 2n_X + 2n_{O_2} = 2n_{CO_2} + n_{H_2O} \\longrightarrow 2n_X + 2 \\times 0,35 = 2 \\times 0,3 + 0,3 \\longrightarrow n_X = 0,1\\text{mol}. Số carbon: n = \\frac{0,3}{0,1} = 3. Vậy công thức phân tử là C_3H_6O_2.";
        string output = CurriculumAiService.NormalizeCurriculumLatex(input);
        System.Console.WriteLine("OUTPUT EXPLANATION: " + output);
        Assert.False(output.Contains("\\text{mol}"), "Should not contain \\text{mol} but got: " + output);
    }

    [Fact]
    public void Test_SampleProblem_Detailed()
    {
        string input = "**Bài toán mẫu:** Đun nóng 12,0\\text{gam} acetic acid (CH_3COOH) với 11,5\\text{gam} ethyl alcohol (C_2H_5OH) có H_2SO_4 đặc làm xúc tác thu được 11,44\\text{gam} ethyl acetate (CH_3COOC_2H_5). Tính hiệu suất phản ứng este hóa.\n\n**Hướng dẫn giải:**\n• Số mol CH_3COOH : n = \\frac{12,0}{60} = 0,2 \\text{mol}.\n• Số mol C_2H_5OH : n = \\frac{11,5}{46} = 0,25 \\text{mol}.\n• Theo lí thuyết:\nn_{CH_3COOC_2H_5 \\text{thực tế}} = n_{CH_3COOH} = 0,2 textmol \\implies m_{\\text{thực tế}} = 0,2 \\times 88 = 17,6 textgam";
        string output = CurriculumAiService.NormalizeCurriculumLatex(input);
        Assert.False(output.Contains("\\text{gam}"), "Should not contain \\text{gam} but got: " + output);
        Assert.False(output.Contains("\\text{mol}"), "Should not contain \\text{mol} but got: " + output);
        Assert.False(output.Contains("textmol"), "Should not contain textmol but got: " + output);
        Assert.False(output.Contains("textgam"), "Should not contain textgam but got: " + output);
    }

    public static IEnumerable<object[]> RegressionInputs() => new[]
    {
        new object[] { "• n_{NaOH} = 3 \\times n_{\\text{chất béo}} = 3 \\times n_{C_3H_5(OH)_3}\n• n_{KOH} = 3 \\times n_{\\text{chất béo}} = 3 \\times n_{C_3H_5(OH)_3}" },
        new object[] { "• n_{NaOH} = 3 \\\\times n_{\\\\text{chất béo}} = 3 \\\\times n_{C_3H_5(OH)_3}" },
        new object[] { "m_{\\text{chất béo}} + m_{NaOH} = m_{\\text{muối (xà phòng)}} + m_{\\text{glycerol}}\n\\longrightarrow m_{\\text{xà phòng}} = m_{\\text{chất béo}} + 40 \\times n_{NaOH} - 92 \\times n_{\\text{glycerol}}" },
        new object[] { "n_{CO_2} - n_{H_2O} = n_{Br_2} + 2n_{\\text{chất béo}}\nSuy ra ngay: n_{Br_2} = (n_{CO_2} - n_{H_2O}) - 2n_{\\text{chất béo}}. Bấm máy Casio trong 3 giây!" },
        new object[] { "• n_{NaOH} = 3 \\times n_{chất béo} = 3 \\times n_{C_3H_5(OH)_3}\n• n_{KOH} = 3 \\times n_{chất béo} = 3 \\times n_{C_3H_5(OH)_3}" },
        new object[] { "m_{chất béo} + m_{NaOH} = m_{muối (xà phòng)} + m_{glycerol}\n\\longrightarrow m_{xà phòng} = m_{chất béo} + 40 \\times n_{NaOH} - 92 \\times n_{glycerol}" },
        new object[] { "n_{CO_2} - n_{H_2O} = n_{Br_2} + 2n_{chất béo}\nSuy ra ngay: n_{Br_2} = (n_{CO_2} - n_{H_2O}) - 2n_{chất béo}. Bấm máy Casio trong 3 giây!" },
        new object[] { "Nếu dùng 2 acid béo (R_1, R_2): Số triester tối đa tạo thành là 2^2 \\times \\frac{2+1}{2} = 6 chất béo. Nếu dùng 3 acid béo (R_1, R_2, R_3): Số triester tối đa là 3^2 \\times \\frac{3+1}{2} = 18 chất béo." },
        new object[] { "Nếu dùng 2 acid béo ($R_1, R_2$): Số triester tối đa tạo thành là $2^2 \\times \\frac{2+1}{2} = 6$ chất béo." },
        new object[] { "Khối lượng glycerol: m_{glycerol} = 0,02 \\times 92 = 1,84 \\text{ gam}.\nÁp dụng định luật bảo toàn khối lượng:\nm = m_{chất béo} + m_{NaOH} - m_{glycerol} = 17,24 + 2,4 - 1,84 = 17,80 \\text{ gam}." },
        new object[] { "Nhầm lẫn tỉ lệ mol: lấy n_{NaOH} = n_{chất béo} thay vì 3 \\times n_{chất béo}." },
        new object[] { "Độ tăng khối lượng khi xà phòng hóa bằng NaOH: m_{muối} - m_{chất béo} = n_{chất béo} \\times (3 \\times 40 - 92) = 28 \\times n_{chất béo}." },
        new object[] { "Công thức liên hệ: n_{CO_2} - n_{H_2O} = n_{Br_2} + 2n_{chất béo}\nSuy ra ngay: n_{Br_2} = (n_{CO_2} - n_{H_2O}) - 2n_{chất béo}. Bấm máy Casio." },
        new object[] { "Quên nhân hệ số 2 của O trong ester khi bảo toàn nguyên tố oxygen (2n_{ester} + 2n_{O_2} = 2n_{CO_2} + n_{H_2O})." },
        new object[] { "Cho toàn bộ Y tác dụng với AgNO_3 trong NH_3, thu được 4a mol Ag. Đun nóng a mol X với dung dịch NaOH vừa đủ." },
    };

    [Theory]
    [MemberData(nameof(RegressionInputs))]
    public void Regression_NoBareOrBrokenMath(string input)
    {
        string output = CurriculumAiService.NormalizeCurriculumLatex(input);
        (output.Count(c => c == '$') % 2).Should().Be(0, output);
        output.Should().NotContain("^$");
        System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(output, @"\$\$[\s\S]*?\$\$", ""), @"\$[^$]*\$", ""), @"_\{|\\times|\\frac").Should().BeFalse("công thức còn trần ngoài $: " + output);
    }

    [Theory]
    [InlineData("• **Công thức đốt cháy triglyceride:**\n\nn_{\\text{triglyceride}} $=\\frac{n_{CO_2} - n_{H_2O}}{k - 1$}", "n_{\\text{triglyceride}}")]
    [InlineData("Phân tích & Lời giải:** Triolein có 3 liên kết \\pi ở gốc $C_{17}H_{33}$. 1 mol triolein phản ứng tối đa 3 mol $H_2$.", "Triolein")]
    [InlineData("Khái niệm: Khi thay thế nhóm - OH ở nhóm carboxyl (-COOH) của carboxylic acid bằng nhóm -OR' thì được ester. Công thức tổng quát : C_nH_{2n}$O_2", "$C_nH_{2n}O_2$")]
    [InlineData("RCOOR' + NaOH \\xrightarrow{t^\\circ} RCOONa + R'OH", "\\xrightarrow")]
    [InlineData("Chất béo là triester của glycerol với các acid béo (C_{15}H_{31} COOH, C_{17}H_{35} COOH, C_{17}H_{33} COOH).", "$C_{15}H_{31}COOH$")]
    [InlineData("Phân tích & Lời giải:** Dựa vào muối $CH_3COONa suy ra gốc acid là CH_3COO-, dựa vào alcohol C_2H_5OH suy ra gốc alcohol là -C_2H_5. Vậy X là CH_3COOC_2H_5$.", "Dựa vào muối")]
    [InlineData("Bước 1: Xác định độ bất bão hòa k = \\frac{2n + 2 - 2H}{2} của hợp chất.\n• Quên điều kiện (n \\ge 2).", "\\frac{2n + 2 - 2H}{2}")]
    [InlineData("• Số mol ester: n = \\frac{8,8}{88} = 0,1 \\text{mol}.\n• Phương trình:\nCH_3COOC_2H_5 + NaOH \\xrightarrow{t^\\circ} CH_3COONa + C_2H_5OH", "\\frac{8,8}{88}")]
    [InlineData("Đun nóng 12,0\\text{gam} acetic acid (CH_3COOH) với 11,5\\text{gam} ethyl alcohol", "12,0 gam")]
    [InlineData("Bảo toàn nguyên tố O: 2n_X + 2n_{O_2} = 2n_{CO_2} + n_{H_2O} \\longrightarrow n_X = 0,1 mol.", "$2n_X + 2n_{O_2} = 2n_{CO_2} + n_{H_2O}")]
    [InlineData("Vậy công thức phân tử là C_3H_6O_2.", "$C_3H_6O_2$")]
    [InlineData("Quên nhân hệ số 2 khi bảo toàn (2n_{\\text{ester}} + 2n_{O_2} = 2n_{CO_2} + n_{H_2O}).", "$2n_{\\text{ester}} + 2n_{O_2} = 2n_{CO_2} + n_{H_2O}$")]
    public void NormalizeCurriculumLatex_SanitizesAndFixesLatex(string input, string expectedSubstring)
    {
        string result = CurriculumAiService.NormalizeCurriculumLatex(input);
        result.Should().Contain(expectedSubstring);
    }

    [Fact]
    public void NormalizeCurriculumLatex_PreservesMathGraph3DBlockIntact()
    {
        string input = """
        • Do đó: $(SAC) \cap (SBD) = SO$.

        [MATH_GRAPH_3D]
        title: Giao tuyến SO của mặt phẳng (SAC) và (SBD)
        shapeType: pyramid_rect_sa
        dimensions: h=4,w=4,d=3.5,r=3,planeD=1.8
        vertices: S:S(0,0,4);A:A(0,0,0);B:B(4,0,0);C:C(4,3.5,0);D:D(0,3.5,0)
        extraPoints: O:name=O:edge=A,C:ratio=0.5:color=#ec4899
        auxLines: A-C:dash=2:color=#0f172a;B-D:dash=2:color=#0f172a;S-O:dash=0:color=#ef4444
        [/MATH_GRAPH_3D]
        """;

        string result = CurriculumAiService.NormalizeCurriculumLatex(input);
        result.Should().Contain("[MATH_GRAPH_3D]");
        result.Should().Contain("[/MATH_GRAPH_3D]");
        result.Should().NotContain("[$MATH_GRAPH_3D$]");
        result.Should().Contain("dimensions: h=4,w=4,d=3.5,r=3,planeD=1.8");
        result.Should().Contain("auxLines: A-C:dash=2:color=#0f172a;B-D:dash=2:color=#0f172a;S-O:dash=0:color=#ef4444");
    }

    [Fact]
    public void NormalizeCurriculumLatex_PreservesBoldPhanTichVaLoiGiaiHeading()
    {
        string input = "**Ví dụ:** Cho hình chóp S.ABCD, M là trung điểm của AB.\n**Phân tích & Lời giải:** Vì M thuộc đoạn thẳng AB, mà AB nằm trong mặt phẳng (SAB) nên M thuộc mặt phẳng (SAB).";
        string result = CurriculumAiService.NormalizeCurriculumLatex(input);
        result.Should().Contain("**Phân tích & Lời giải:**");
        result.Should().NotContain("**Phân tích & **Lời giải:****");
    }
}
