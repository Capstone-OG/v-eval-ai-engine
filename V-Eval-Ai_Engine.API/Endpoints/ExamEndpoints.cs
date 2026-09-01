using Microsoft.AspNetCore.Mvc;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.API.Endpoints;

public static class ExamEndpoints
{
    public static IEndpointRouteBuilder MapExamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai-engine");

        // 1. Endpoint Upload và Trích xuất Đề thi PDF trực tiếp bằng Gemini 2.5 Flash Native
        group.MapPost("/upload-pdf", async (
            IFormFile file,
            [FromServices] IExamParserService examParserService,
            IWebHostEnvironment env) =>
        {
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "Không nhận được file hoặc file rỗng." });
            }

            if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Chỉ chấp nhận file định dạng PDF." });
            }

            // Tạo thư mục uploads trong wwwroot để phục vụ preview PDF nếu chưa có
            string webRoot = env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string uploadsDir = Path.Combine(webRoot, "uploads");
            if (!Directory.Exists(uploadsDir))
            {
                Directory.CreateDirectory(uploadsDir);
            }

            string uniqueFileName = $"{Guid.NewGuid()}.pdf";
            string savedFilePath = Path.Combine(uploadsDir, uniqueFileName);

            try
            {
                // Lưu file tạm thời phục vụ render PDF trên UI
                await using (var fileStream = new FileStream(savedFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                // Đọc file và gọi bóc tách đề thi qua Gemini Native C#
                await using (var readStream = new FileStream(savedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var parsedExam = await examParserService.ParsePdfAsync(readStream, file.FileName);
                    parsedExam.PdfUrl = $"/uploads/{uniqueFileName}";

                    return Results.Ok(parsedExam);
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Lỗi trong quá trình trích xuất đề thi AI"
                );
            }
        })
        .WithName("UploadAndParsePdfFile")
        .DisableAntiforgery();

        // 2. Endpoint phục vụ giao diện xem trước đề thi trực quan
        group.MapGet("/view-exam", async (IWebHostEnvironment env) =>
        {
            string webRoot = env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string filePath = Path.Combine(webRoot, "view-exam.html");

            if (File.Exists(filePath))
            {
                string htmlContent = await File.ReadAllTextAsync(filePath);
                return Results.Content(htmlContent, "text/html");
            }

            return Results.NotFound(new { message = "Không tìm thấy file view-exam.html trong wwwroot." });
        })
        .WithName("ViewExamViewerPage");

        return app;
    }
}
