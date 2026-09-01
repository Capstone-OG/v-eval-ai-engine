using Microsoft.AspNetCore.Mvc;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.API.Endpoints;

public static class ExamEndpoints
{
    public static IEndpointRouteBuilder MapExamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai-engine");

        // 1. Endpoint Upload Đề thi PDF và khởi tạo tiến trình nền (Background Job)
        group.MapPost("/upload-pdf", (
            IFormFile file,
            [FromServices] IExamJobManager jobManager,
            [FromServices] IServiceScopeFactory scopeFactory,
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

            // Tạo thư mục uploads trong wwwroot nếu chưa có
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
                // Lưu file PDF tạm thời
                using (var fileStream = new FileStream(savedFilePath, FileMode.Create))
                {
                    file.CopyTo(fileStream);
                }

                // Khởi tạo Background Job
                var job = jobManager.CreateJob(file.FileName);

                // Khởi chạy tiến trình phân tích nền không khóa luồng HTTP
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var parserService = scope.ServiceProvider.GetRequiredService<IExamParserService>();

                        jobManager.UpdateJobProgress(job.JobId, "Đang tải dữ liệu và phân tích đề thi bằng Gemini AI (có thể mất 1-3 phút)...");

                        await using var readStream = new FileStream(savedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var parsedExam = await parserService.ParsePdfAsync(readStream, file.FileName);
                        parsedExam.PdfUrl = $"/uploads/{uniqueFileName}";

                        jobManager.CompleteJob(job.JobId, parsedExam);
                    }
                    catch (Exception ex)
                    {
                        jobManager.FailJob(job.JobId, ex.Message);
                    }
                });

                // Trả về HTTP 202 Accepted kèm thông tin job để client polling
                return Results.Accepted($"/api/ai-engine/jobs/{job.JobId}", job);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Lỗi trong quá trình khởi tạo tác vụ phân tích đề thi"
                );
            }
        })
        .WithName("UploadAndParsePdfFile")
        .DisableAntiforgery();

        // 2. Endpoint kiểm tra trạng thái tiến trình nền (Job Polling)
        group.MapGet("/jobs/{jobId}", (
            string jobId,
            [FromServices] IExamJobManager jobManager) =>
        {
            var job = jobManager.GetJob(jobId);
            if (job == null)
            {
                return Results.NotFound(new { message = $"Không tìm thấy tác vụ với mã '{jobId}'." });
            }

            return Results.Ok(job);
        })
        .WithName("GetExamJobStatus");

        // 3. Endpoint phục vụ giao diện xem trước đề thi trực quan
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
