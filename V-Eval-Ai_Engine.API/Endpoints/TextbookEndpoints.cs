using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
            bool forceReingest = false;
            if (bool.TryParse(form["forceReingest"], out var parsedForce))
            {
                forceReingest = parsedForce;
            }

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
                // 1. Lưu file tạm thời lên server
                using (var fileStream = new FileStream(savedFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                // 2. Tính mã Hash SHA-256 của tệp
                byte[] hashBytes;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    await using var checkStream = new FileStream(savedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    hashBytes = await sha.ComputeHashAsync(checkStream);
                }
                string fileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                // 3. Kiểm tra xem file này đã có tác vụ ngầm đang chạy hay chưa
                var existingJob = jobManager.GetJobByFileHash(fileHash);
                if (!forceReingest && existingJob != null && existingJob.Status == "PROCESSING")
                {
                    return Results.Accepted($"/api/ai-engine/textbooks/jobs/{existingJob.JobId}", existingJob);
                }

                // 4. Kiểm tra Checkpoint từ CSDL Supabase
                int startPage = 1;
                int totalPages = 0;
                if (!forceReingest)
                {
                    using (var scope = scopeFactory.CreateScope())
                    {
                        var repo = scope.ServiceProvider.GetRequiredService<ITextbookRepository>();
                        var cp = await repo.GetCheckpointByFileHashAsync(fileHash);
                        if (cp != null && cp.Value.LastProcessedPage > 0)
                        {
                            totalPages = cp.Value.TotalPages;
                            startPage = cp.Value.LastProcessedPage >= cp.Value.TotalPages 
                                ? cp.Value.TotalPages 
                                : cp.Value.LastProcessedPage + 1;
                        }
                    }
                }

                // 5. Khởi tạo Background Job với trạng thái Checkpoint
                var job = jobManager.CreateJob(file.FileName, fileHash, totalPages, startPage);

                // 6. Khởi chạy tác vụ ngầm độc lập với HTTP Request
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
                            forceReingest: forceReingest,
                            onProgress: (step, percent) =>
                            {
                                int curPage = (int)((float)percent / 100 * (job.TotalPages > 0 ? job.TotalPages : 100));
                                jobManager.UpdateJobProgress(job.JobId, step, percent, curPage);
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

        // 2. Endpoint kiểm tra tác vụ đang chạy gần nhất (phục vụ khi người dùng F5 hoặc quay lại)
        group.MapGet("/active-job", ([FromServices] ITextbookJobManager jobManager) =>
        {
            var active = jobManager.GetActiveJob();
            if (active == null)
            {
                return Results.Ok(new { hasActive = false });
            }
            return Results.Ok(new { hasActive = true, job = active });
        })
        .WithName("GetActiveTextbookJob");

        // 3. Endpoint tra cứu Checkpoint của tệp theo SHA-256 Hash
        group.MapGet("/checkpoint/{fileHash}", async (
            string fileHash,
            [FromServices] ITextbookRepository repository) =>
        {
            var cp = await repository.GetCheckpointByFileHashAsync(fileHash);
            if (cp == null)
            {
                return Results.Ok(new { hasCheckpoint = false });
            }
            return Results.Ok(new
            {
                hasCheckpoint = true,
                sourceId = cp.Value.SourceId,
                lastProcessedPage = cp.Value.LastProcessedPage,
                totalPages = cp.Value.TotalPages,
                status = cp.Value.Status,
                totalChars = cp.Value.TotalChars,
                totalChunks = cp.Value.TotalChunks,
                isCompleted = cp.Value.LastProcessedPage >= cp.Value.TotalPages && cp.Value.TotalPages > 0
            });
        })
        .WithName("GetTextbookCheckpoint");

        // 4. Endpoint kiểm tra tiến độ nạp SGK (Polling Endpoint)
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

        // 5. Endpoint tra cứu danh sách Chunks theo SourceId
        group.MapGet("/chunks/{sourceId:guid}", async (
            Guid sourceId,
            [FromServices] ITextbookRepository repository) =>
        {
            var chunks = await repository.GetChunksBySourceIdAsync(sourceId);
            return Results.Ok(new
            {
                sourceId,
                totalChunks = chunks.Count,
                chunks
            });
        })
        .WithName("GetTextbookChunksBySourceId");

        // 6. Endpoint tra cứu danh sách Chunks theo fileHash
        group.MapGet("/chunks-by-hash/{fileHash}", async (
            string fileHash,
            [FromServices] ITextbookRepository repository) =>
        {
            var cp = await repository.GetCheckpointByFileHashAsync(fileHash);
            if (cp == null)
            {
                return Results.NotFound(new { message = "Không tìm thấy tài liệu với mã file hash này." });
            }
            var chunks = await repository.GetChunksBySourceIdAsync(cp.Value.SourceId);
            return Results.Ok(new
            {
                sourceId = cp.Value.SourceId,
                fileHash,
                totalPages = cp.Value.TotalPages,
                lastProcessedPage = cp.Value.LastProcessedPage,
                status = cp.Value.Status,
                totalChunks = chunks.Count,
                chunks
            });
        })
        .WithName("GetTextbookChunksByFileHash");

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

        // 4. Endpoint Ping Kiểm Tra & Đo Độ Trễ Các Mô Hình Vision AI (Gemini / OpenAI)
        group.MapGet("/ping-vision", async (
            [FromQuery] string? key,
            [FromServices] IConfiguration configuration,
            [FromServices] IHttpClientFactory httpClientFactory) =>
        {
            var results = new List<object>();
            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            // 1. Xác định Gemini API Key (Ưu tiên query param, sau đó đến cấu hình appsettings, biến môi trường)
            string? geminiKey = !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
            if (string.IsNullOrWhiteSpace(geminiKey))
            {
                var keysSection = configuration.GetSection("AiSettings:GeminiApiKeys").Get<string[]>();
                if (keysSection != null && keysSection.Length > 0 && !keysSection[0].StartsWith("YOUR_"))
                {
                    geminiKey = keysSection[0].Trim();
                }
                else
                {
                    var single = configuration["AiSettings:GeminiApiKey"];
                    if (!string.IsNullOrWhiteSpace(single) && !single.StartsWith("YOUR_"))
                    {
                        geminiKey = single.Trim();
                    }
                    else
                    {
                        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
                        if (!string.IsNullOrWhiteSpace(envKey) && !envKey.StartsWith("YOUR_"))
                        {
                            geminiKey = envKey.Trim();
                        }
                    }
                }
            }

            var geminiModels = new[]
            {
                new { Id = "gemini-1.5-flash", Role = "Top 1 Khuyến nghị: Tốc độ < 1s/trang, nhận diện LaTeX/Bảng chuẩn, Free Tier 15 RPM" },
                new { Id = "gemini-2.0-flash", Role = "Top 1 Khuyến nghị: Đa phương thức thế hệ mới, tối ưu sơ đồ/hình vẽ/thí nghiệm" },
                new { Id = "gemini-1.5-flash-8b", Role = "Chi phí siêu rẻ ($0.0375/1M), tốc độ cao nhất, thích hợp SGK thuần chữ" },
                new { Id = "gemini-1.5-pro", Role = "Tư duy sâu nhất, tối ưu đề thi 120 câu mẹo bẫy, latency ~2.5s-4s" }
            };

            if (string.IsNullOrWhiteSpace(geminiKey))
            {
                foreach (var m in geminiModels)
                {
                    results.Add(new
                    {
                        provider = "Google Gemini",
                        model = m.Id,
                        role = m.Role,
                        status = "MISSING_KEY",
                        httpCode = 0,
                        latencyMs = 0,
                        message = "Chưa cấu hình API Key. Truyền ?key=AIza... hoặc điền vào appsettings.json"
                    });
                }
            }
            else
            {
                string maskedKey = geminiKey.Length > 8 ? $"{geminiKey[..4]}...{geminiKey[^4..]}" : "****";
                var testPayload = new
                {
                    contents = new[]
                    {
                        new { parts = new object[] { new { text = "Ping test! Reply with 'OK' only." } } }
                    }
                };
                string jsonPayload = JsonSerializer.Serialize(testPayload);

                foreach (var m in geminiModels)
                {
                    var sw = Stopwatch.StartNew();
                    string testUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{m.Id}:generateContent?key={geminiKey}";
                    try
                    {
                        using var reqContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                        var response = await httpClient.PostAsync(testUrl, reqContent);
                        sw.Stop();

                        string respBody = await response.Content.ReadAsStringAsync();
                        bool isOk = response.IsSuccessStatusCode;

                        results.Add(new
                        {
                            provider = "Google Gemini",
                            model = m.Id,
                            role = m.Role,
                            status = isOk ? "OK" : "ERROR",
                            httpCode = (int)response.StatusCode,
                            latencyMs = sw.ElapsedMilliseconds,
                            apiKey = maskedKey,
                            message = isOk ? "Kết nối hoàn hảo! Sẵn sàng cho OCR bóc tách SGK." : (respBody.Length > 200 ? respBody[..200] : respBody)
                        });
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        results.Add(new
                        {
                            provider = "Google Gemini",
                            model = m.Id,
                            role = m.Role,
                            status = "EXCEPTION",
                            httpCode = 0,
                            latencyMs = sw.ElapsedMilliseconds,
                            apiKey = maskedKey,
                            message = ex.Message
                        });
                    }
                }
            }

            // 2. OpenAI Model Ping (gpt-4o-mini)
            string? openAiKey = configuration["AiSettings:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(openAiKey) && !openAiKey.StartsWith("YOUR_"))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var openAiPayload = new
                    {
                        model = "gpt-4o-mini",
                        messages = new[] { new { role = "user", content = "Ping test! Reply 'OK'." } },
                        max_tokens = 5
                    };
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(openAiPayload), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAiKey);
                    var resp = await httpClient.SendAsync(req);
                    sw.Stop();
                    string body = await resp.Content.ReadAsStringAsync();
                    results.Add(new
                    {
                        provider = "OpenAI",
                        model = "gpt-4o-mini",
                        role = "Dự phòng số 1: Tốc độ ~1.5s, nhận diện tiếng Việt tốt, không có Free Tier",
                        status = resp.IsSuccessStatusCode ? "OK" : "ERROR",
                        httpCode = (int)resp.StatusCode,
                        latencyMs = sw.ElapsedMilliseconds,
                        apiKey = openAiKey.Length > 8 ? $"{openAiKey[..4]}...{openAiKey[^4..]}" : "****",
                        message = resp.IsSuccessStatusCode ? "Kết nối OpenAI thành công!" : (body.Length > 200 ? body[..200] : body)
                    });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    results.Add(new
                    {
                        provider = "OpenAI",
                        model = "gpt-4o-mini",
                        role = "Dự phòng số 1",
                        status = "EXCEPTION",
                        httpCode = 0,
                        latencyMs = sw.ElapsedMilliseconds,
                        apiKey = "****",
                        message = ex.Message
                    });
                }
            }

            return Results.Ok(new
            {
                pingTimestamp = DateTime.UtcNow,
                hasActiveKey = !string.IsNullOrWhiteSpace(geminiKey),
                recommendations = new
                {
                    bestSpeedAndQuality = "gemini-1.5-flash & gemini-2.0-flash (Latency < 1s, Miễn phí 15 RPM, chuẩn LaTeX/Bảng)",
                    bestComplexDiagrams = "gemini-2.0-flash (Đa phương thức thế hệ mới)",
                    bestFallback = "gpt-4o-mini (OpenAI khi hết quota Google)"
                },
                models = results
            });
        })
        .WithName("PingVisionModels");

        // 5. Endpoint phục vụ giao diện Web xem và nạp SGK
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
