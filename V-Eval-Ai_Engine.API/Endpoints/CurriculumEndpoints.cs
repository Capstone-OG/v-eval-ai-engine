using System.Text;
using Microsoft.AspNetCore.Mvc;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.API.Endpoints;

public static class CurriculumEndpoints
{
    public static IEndpointRouteBuilder MapCurriculumEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai-engine/curriculum")
            .WithTags("Curriculum & Pattern AI Engine");

        // 1. Endpoint trích xuất khung chương trình từ file tài liệu (PDF, Word, TXT) hoặc văn bản thô
        group.MapPost("/extract", async (
            HttpRequest httpRequest,
            [FromServices] ICurriculumAiService curriculumAiService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                Stream? fileStream = null;
                string? fileName = null;
                string? textContent = null;
                string domainId = "dom_math_analysis";
                string domainName = "Toán học & Tư duy định lượng";
                int? startPage = null;
                int? endPage = null;
                string extractionDepth = "deep";

                if (httpRequest.HasFormContentType)
                {
                    var form = await httpRequest.ReadFormAsync(cancellationToken);
                    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                    if (file != null && file.Length > 0)
                    {
                        fileName = file.FileName;
                        fileStream = file.OpenReadStream();
                    }

                    if (form.TryGetValue("textContent", out var formText) || form.TryGetValue("text_content", out formText))
                    {
                        textContent = formText.ToString();
                    }

                    if (form.TryGetValue("domainId", out var formDomId) || form.TryGetValue("domain_id", out formDomId))
                    {
                        if (!string.IsNullOrWhiteSpace(formDomId)) domainId = formDomId.ToString();
                    }

                    if (form.TryGetValue("domainName", out var formDomName) || form.TryGetValue("domain_name", out formDomName))
                    {
                        if (!string.IsNullOrWhiteSpace(formDomName)) domainName = formDomName.ToString();
                    }

                    if (form.TryGetValue("startPage", out var sp) || form.TryGetValue("start_page", out sp))
                    {
                        if (int.TryParse(sp.ToString(), out var spVal)) startPage = spVal;
                    }

                    if (form.TryGetValue("endPage", out var ep) || form.TryGetValue("end_page", out ep))
                    {
                        if (int.TryParse(ep.ToString(), out var epVal)) endPage = epVal;
                    }

                    if (form.TryGetValue("extractionDepth", out var ed) || form.TryGetValue("extraction_depth", out ed))
                    {
                        if (!string.IsNullOrWhiteSpace(ed)) extractionDepth = ed.ToString();
                    }
                }
                else if (httpRequest.ContentType?.Contains("application/json") == true)
                {
                    var jsonBody = await httpRequest.ReadFromJsonAsync<CurriculumExtractTextPayload>(cancellationToken: cancellationToken);
                    if (jsonBody != null)
                    {
                        textContent = jsonBody.TextContent;
                        if (!string.IsNullOrWhiteSpace(jsonBody.DomainId)) domainId = jsonBody.DomainId;
                        if (!string.IsNullOrWhiteSpace(jsonBody.DomainName)) domainName = jsonBody.DomainName;
                        if (jsonBody.StartPage.HasValue) startPage = jsonBody.StartPage;
                        if (jsonBody.EndPage.HasValue) endPage = jsonBody.EndPage;
                        if (!string.IsNullOrWhiteSpace(jsonBody.ExtractionDepth)) extractionDepth = jsonBody.ExtractionDepth;
                    }
                }

                if (fileStream == null && string.IsNullOrWhiteSpace(textContent))
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        message = "Vui lòng đính kèm file tài liệu (PDF, Word, TXT) hoặc cung cấp nội dung văn bản qua trường 'textContent'/'text_content'."
                    });
                }

                using (fileStream)
                {
                    var result = await curriculumAiService.ExtractCurriculumAsync(fileStream, fileName, domainId, domainName, textContent, startPage, endPage, extractionDepth, cancellationToken);
                    return Results.Ok(result);
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: $"Lỗi khi trích xuất tài liệu khung chương trình: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .Produces<ImportCurriculumRequestDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status500InternalServerError)
        .DisableAntiforgery();

        // 2. Endpoint phân tích chuyên sâu bản chất Sư phạm của Dạng bài Toán (Bloom L1-L4, 4 bước, bẫy, Casio)
        group.MapPost("/analyze-pattern", async (
            [FromBody] AnalyzePatternRequestDto request,
            [FromServices] ICurriculumAiService curriculumAiService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request?.PatternName))
            {
                return Results.BadRequest(new
                {
                    success = false,
                    message = "Tên dạng bài (PatternName) là bắt buộc để AI phân tích phương pháp giải và tri thức."
                });
            }

            try
            {
                var result = await curriculumAiService.AnalyzePatternAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: $"Lỗi khi phân tích tri thức dạng bài: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .Produces<AnalyzePatternResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status500InternalServerError);

        // 3. Endpoint tạo thêm câu hỏi rèn luyện bám sát 100% vào Dạng bài đang phân tích
        group.MapPost("/generate-questions", async (
            [FromBody] GeneratePatternQuestionsRequestDto request,
            [FromServices] ICurriculumAiService curriculumAiService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request?.PatternName))
            {
                return Results.BadRequest(new
                {
                    success = false,
                    message = "Tên dạng bài (PatternName) là bắt buộc để AI tạo câu hỏi rèn luyện bám sát dạng."
                });
            }

            try
            {
                var result = await curriculumAiService.GeneratePatternQuestionsAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: $"Lỗi khi tạo câu hỏi rèn luyện cho dạng bài: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .Produces<GeneratePatternQuestionsResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status500InternalServerError);

        return app;
    }
}

public class CurriculumExtractTextPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("textContent")]
    public string? TextContent { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("text_content")]
    public string? TextContentSnake { set { if (!string.IsNullOrWhiteSpace(value)) TextContent = value; } }

    [System.Text.Json.Serialization.JsonPropertyName("domainId")]
    public string? DomainId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("domain_id")]
    public string? DomainIdSnake { set { if (!string.IsNullOrWhiteSpace(value)) DomainId = value; } }

    [System.Text.Json.Serialization.JsonPropertyName("domainName")]
    public string? DomainName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("domain_name")]
    public string? DomainNameSnake { set { if (!string.IsNullOrWhiteSpace(value)) DomainName = value; } }

    [System.Text.Json.Serialization.JsonPropertyName("startPage")]
    public int? StartPage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("start_page")]
    public int? StartPageSnake { set { if (value.HasValue) StartPage = value; } }

    [System.Text.Json.Serialization.JsonPropertyName("endPage")]
    public int? EndPage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("end_page")]
    public int? EndPageSnake { set { if (value.HasValue) EndPage = value; } }

    [System.Text.Json.Serialization.JsonPropertyName("extractionDepth")]
    public string? ExtractionDepth { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("extraction_depth")]
    public string? ExtractionDepthSnake { set { if (!string.IsNullOrWhiteSpace(value)) ExtractionDepth = value; } }
}
