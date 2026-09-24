using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.API.Endpoints;

public static class TextbookEndpoints
{
    public static IEndpointRouteBuilder MapTextbookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai-engine/textbooks");

        // 1. Endpoint Upload SGK PDF và khởi tạo tiến trình nạp ngầm (Background Job)
        group.MapPost("/upload-pdf", async (
            HttpRequest request,
            [FromServices] ITextbookJobManager jobManager,
            [FromServices] IServiceScopeFactory scopeFactory,
            IWebHostEnvironment env) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Yêu cầu phải có định dạng multipart/form-data." });
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            string domainId = form["domainId"].ToString();
            string skillId = form["skillId"].ToString();
            string docType = form["docType"].ToString();
            string ocrMode = form["ocrMode"].ToString();

            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "Không nhận được file SGK hoặc file rỗng." });
            }

            if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Chỉ chấp nhận file tài liệu định dạng PDF." });
            }

            string webRoot = env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string uploadsDir = Path.Combine(webRoot, "uploads", "textbooks");
            if (!Directory.Exists(uploadsDir))
            {
                Directory.CreateDirectory(uploadsDir);
            }

            string uniqueFileName = $"{Guid.NewGuid()}_{file.FileName}";
            string savedFilePath = Path.Combine(uploadsDir, uniqueFileName);

            try
            {
                // Lưu file tạm thời
                using (var fileStream = new FileStream(savedFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                // Khởi tạo Background Job
                var job = jobManager.CreateJob(file.FileName);

                // Khởi chạy tác vụ ngầm trích xuất chữ local và cắt chunk
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var parserService = scope.ServiceProvider.GetRequiredService<ITextbookParserService>();

                        await using var readStream = new FileStream(savedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        
                        var result = await parserService.ParseAndIngestTextbookAsync(
                            readStream,
                            file.FileName,
                            string.IsNullOrWhiteSpace(domainId) ? "Domain-Default" : domainId,
                            string.IsNullOrWhiteSpace(skillId) ? "Skill-Default" : skillId,
                            string.IsNullOrWhiteSpace(docType) ? "TEXTBOOK" : docType,
                            ocrMode: string.IsNullOrWhiteSpace(ocrMode) ? "TEXT_HUMANITIES" : ocrMode,
                            onProgress: (step, percent) =>
                            {
                                jobManager.UpdateJobProgress(job.JobId, step, percent);
                            }
                        );

                        result.PdfUrl = $"/uploads/textbooks/{uniqueFileName}";
                        jobManager.CompleteJob(job.JobId, result);
                    }
                    catch (Exception ex)
                    {
                        jobManager.FailJob(job.JobId, ex.Message);
                    }
                });

                return Results.Accepted($"/api/ai-engine/textbooks/jobs/{job.JobId}", job);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Lỗi trong quá trình tiếp nhận file SGK"
                );
            }
        })
        .WithName("UploadAndIngestTextbookPdf")
        .WithMetadata(new RequestSizeLimitAttribute(262_144_000))
        .DisableAntiforgery();

        // 2. Endpoint kiểm tra tiến độ nạp SGK (Polling Endpoint)
        group.MapGet("/jobs/{jobId}", (
            string jobId,
            [FromServices] ITextbookJobManager jobManager) =>
        {
            var job = jobManager.GetJob(jobId);
            if (job == null)
            {
                return Results.NotFound(new { message = $"Không tìm thấy tác vụ nạp SGK với mã '{jobId}'." });
            }

            return Results.Ok(job);
        })
        .WithName("GetTextbookJobStatus");

        // 3. Endpoint lưu các Vector Chunks và tri thức SGK vào Database (Schema v_eval_ai)
        group.MapPost("/save-db", async (
            [FromBody] TextbookIngestResultDto dto,
            [FromServices] ITextbookRepository repository) =>
        {
            if (dto == null || dto.Chunks == null || dto.Chunks.Count == 0)
            {
                return Results.BadRequest(new { message = "Dữ liệu Chunks tri thức rỗng, không thể lưu DB." });
            }

            try
            {
                int savedCount = await repository.SaveTextbookAndChunksAsync(dto);

                return Results.Ok(new
                {
                    success = true,
                    message = $"Đã lưu thành công {savedCount} Vector Chunks của tài liệu '{dto.FileName}' vào Database Supabase (Schema v_eval_ai)!",
                    sourceId = dto.DocumentId,
                    totalChunksSaved = savedCount,
                    fileHash = dto.FileHash,
                    savedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Lỗi khi lưu tri thức SGK vào Supabase Database"
                );
            }
        })
        .WithName("SaveTextbookToDatabase");

        // 3. Endpoint phục vụ giao diện Web xem và nạp SGK
        app.MapGet("/api/ai-engine/view-textbook", async (IWebHostEnvironment env) =>
        {
            string webRoot = env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string filePath = Path.Combine(webRoot, "view-textbook.html");

            if (File.Exists(filePath))
            {
                string htmlContent = await File.ReadAllTextAsync(filePath);
                return Results.Content(htmlContent, "text/html");
            }

            return Results.NotFound(new { message = "Không tìm thấy file view-textbook.html trong wwwroot." });
        })
        .WithName("ViewTextbookIngestPage");

        return app;
    }
}
