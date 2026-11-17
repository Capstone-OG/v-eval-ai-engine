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
            int targetDpi = 48;
            if (int.TryParse(form["targetDpi"], out var parsedDpi) && parsedDpi >= 40 && parsedDpi <= 120)
            {
                targetDpi = parsedDpi;
            }
            int batchSize = 2;
            if (int.TryParse(form["batchSize"], out var parsedBatch) && parsedBatch >= 1 && parsedBatch <= 15)
            {
                batchSize = parsedBatch;
            }
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
                // 1. Lưu file lên đĩa và đồng thời tính mã băm SHA-256 trong 1 lượt đọc (Single-Pass CryptoStream)
                // Giảm 50% Disk I/O cho file lớn (100MB-250MB), không cần mở lại file từ đĩa để tính hash
                byte[] hashBytes;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    await using (var fileStream = new FileStream(savedFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await using (var cryptoStream = new System.Security.Cryptography.CryptoStream(fileStream, sha, System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
                    {
                        await file.CopyToAsync(cryptoStream);
                        await cryptoStream.FlushFinalBlockAsync();
                    }
                    hashBytes = sha.Hash!;
                }
                string fileHash = Convert.ToHexStringLower(hashBytes);

                // 2. Kiểm tra xem file này đã có tác vụ ngầm đang chạy hay chưa (Deduplication)
                var existingJob = jobManager.GetJobByFileHash(fileHash);
                if (!forceReingest && existingJob != null && existingJob.Status == "PROCESSING")
                {
                    // Dọn dẹp file vừa tải lên vì đã có tác vụ đang xử lý cùng file hash này
                    try
                    {
                        if (File.Exists(savedFilePath))
                        {
                            File.Delete(savedFilePath);
                        }
                    }
                    catch
                    {
                        // Nuốt lỗi nếu tệp bị khóa tạm thời
                    }

                    return Results.Accepted($"/api/ai-engine/textbooks/jobs/{existingJob.JobId}", existingJob);
                }

                // 4. Kiểm tra Checkpoint từ CSDL Supabase
                int startPage = 1;
                int totalPages = 0;
                string existingSourceId = string.Empty;
                if (!forceReingest)
                {
                    using (var scope = scopeFactory.CreateScope())
                    {
                        var repo = scope.ServiceProvider.GetRequiredService<ITextbookRepository>();
                        var cp = await repo.GetCheckpointByFileHashAsync(fileHash);
                        if (cp != null && cp.Value.LastProcessedPage > 0)
                        {
                            existingSourceId = cp.Value.SourceId.ToString();
                            totalPages = cp.Value.TotalPages;
                            startPage = cp.Value.LastProcessedPage >= cp.Value.TotalPages 
                                ? cp.Value.TotalPages 
                                : cp.Value.LastProcessedPage + 1;
                        }
                    }
                }

                // 5. Khởi tạo Background Job với trạng thái Checkpoint
                var job = jobManager.CreateJob(file.FileName, fileHash, totalPages, startPage);
                if (!string.IsNullOrWhiteSpace(existingSourceId))
                {
                    job.SourceId = existingSourceId;
                }
                job.TargetDpi = targetDpi;

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
                            },
                            targetDpi: targetDpi,
                            batchSize: batchSize
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

        // 6. Endpoint tra cứu danh sách Chunks theo fileHash kèm Cây Chương - Bài - Dạng bài đã tổng hợp
        group.MapGet("/chunks-by-hash/{fileHash}", async (
            string fileHash,
            [FromServices] ITextbookRepository repository,
            [FromServices] ITextbookParserService parserService) =>
        {
            var cp = await repository.GetCheckpointByFileHashAsync(fileHash);
            if (cp == null)
            {
                return Results.NotFound(new { message = "Không tìm thấy tài liệu với mã file hash này." });
            }
            var chunks = await repository.GetChunksBySourceIdAsync(cp.Value.SourceId);
            var first = chunks.FirstOrDefault();
            var synthesized = parserService.SynthesizeCurriculumFromChunks(
                $"SGK_{first?.SubjectCode ?? "MATH"}_{first?.Grade ?? 12}",
                string.Empty,
                chunks);

            return Results.Ok(new
            {
                sourceId = cp.Value.SourceId,
                fileHash,
                totalPages = cp.Value.TotalPages,
                lastProcessedPage = cp.Value.LastProcessedPage,
                status = cp.Value.Status,
                totalChunks = chunks.Count,
                detectedSubjectCode = first?.SubjectCode ?? synthesized.SubjectCode ?? "MATH",
                detectedSubjectName = first?.SubjectName ?? synthesized.SubjectName ?? "Toán học",
                detectedGrade = first?.Grade ?? synthesized.Grade,
                detectedBookEdition = first?.BookEdition ?? synthesized.BookEdition,
                dpiUsed = first?.DpiUsed ?? 56,
                totalDiagramsDetected = chunks.Sum(c => c.ImagePlaceholders?.Count ?? 0),
                chaptersDetected = chunks.Select(c => c.ChapterName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
                lessonsDetected = chunks.Select(c => c.LessonName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
                patternsDetected = chunks.Select(c => c.PatternName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
                synthesizedCurriculum = synthesized,
                chunks
            });
        })
        .WithName("GetTextbookChunksByFileHash");

        // 7. Endpoint tổng hợp Chunks SGK thành cấu trúc Cây Chương - Bài - Dạng bài chuẩn Content Service
        group.MapPost("/synthesize-curriculum/{fileHash}", async (
            string fileHash,
            [FromQuery] string? domainId,
            [FromServices] ITextbookRepository repository,
            [FromServices] ITextbookParserService parserService) =>
        {
            var cp = await repository.GetCheckpointByFileHashAsync(fileHash);
            if (cp == null)
            {
                return Results.NotFound(new { message = "Không tìm thấy tài liệu SGK với mã fileHash này." });
            }
            var chunks = await repository.GetChunksBySourceIdAsync(cp.Value.SourceId);
            if (chunks.Count == 0)
            {
                return Results.BadRequest(new { message = "Tài liệu chưa có Chunks nào được bóc tách." });
            }

            var first = chunks.FirstOrDefault();
            var curriculumDto = parserService.SynthesizeCurriculumFromChunks(
                $"SGK_{first?.SubjectName ?? "GDPT2018"}_Lop{first?.Grade ?? 12}",
                domainId ?? string.Empty,
                chunks);

            return Results.Ok(curriculumDto);
        })
        .WithName("SynthesizeCurriculumFromTextbook");

        // 8. Endpoint tra cứu Ma trận Nội dung Chương trình GDPT 2018 (Lớp 10-11-12 Chân trời sáng tạo & ĐGNL V-ACT)
        group.MapGet("/taxonomy", () =>
        {
            var root = V_Eval_Ai_Engine.Infrastructure.Services.CurriculumTaxonomyRegistry.GetTaxonomyRoot();
            if (root == null)
            {
                return Results.NotFound(new { message = "Không tìm thấy tệp cấu hình gdpt2018_dgnl_taxonomy.json." });
            }
            return Results.Content(root.ToJsonString(), "application/json");
        })
        .WithName("GetGdpt2018MasterTaxonomy");

        // 9. Endpoint liệt kê toàn bộ các SGK đã nạp vào CSDL kèm cấu trúc Chương - Bài - Dạng bài & Chunks để UI Giáo trình chung đồng bộ tick xanh
        group.MapGet("/sources", async ([FromServices] ITextbookRepository repository) =>
        {
            var sources = await repository.GetIngestedSourcesWithSummaryAsync();
            return Results.Ok(new
            {
                totalSources = sources.Count,
                sources
            });
        })
        .WithName("GetIngestedTextbookSources");

        // 9A. Endpoint xóa một cuốn sách SGK đã nạp khỏi CSDL và xóa toàn bộ Chunks liên quan
        group.MapDelete("/sources/{sourceId:guid}", async (
            Guid sourceId,
            [FromServices] ITextbookRepository repository) =>
        {
            var success = await repository.DeleteSourceAsync(sourceId);
            if (!success)
            {
                return Results.BadRequest(new { message = $"Không thể xóa tài liệu SGK với mã '{sourceId}'." });
            }
            return Results.Ok(new { success = true, message = "Đã xóa thành công tài liệu SGK khỏi hệ thống." });
        })
        .WithName("DeleteTextbookSource");

        // 9B. Endpoint quét nhanh Khung Mục lục SGK (Stage 1: 5-10s -> Chương -> Bài -> Dạng bài + Bloom 6 cấp độ)
        group.MapPost("/scan-skeleton", async (
            IFormFile file,
            [FromForm] string? subjectCode,
            [FromForm] int? grade,
            [FromForm] string? bookEdition,
            [FromServices] ITextbookParserService parserService,
            CancellationToken cancellationToken) =>
        {
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "Vui lòng đính kèm file PDF SGK." });
            }

            await using var stream = file.OpenReadStream();
            var skeleton = await parserService.ScanTextbookSkeletonAsync(stream, file.FileName, subjectCode, grade, bookEdition, cancellationToken);
            return Results.Ok(skeleton);
        })
        .DisableAntiforgery()
        .WithName("ScanTextbookSkeleton");

        // 9C. Endpoint AI Sinh bài tập tự động theo Dạng bài & Bloom Taxonomy 6 cấp độ
        group.MapPost("/generate-exercises", async (
            [FromBody] GenerateExercisesRequestDto request,
            [FromServices] ITextbookParserService parserService,
            CancellationToken cancellationToken) =>
        {
            if (request == null || string.IsNullOrWhiteSpace(request.PatternName))
            {
                return Results.BadRequest(new { message = "Vui lòng cung cấp thông tin Dạng bài và Môn học." });
            }

            var exercises = await parserService.GeneratePatternExercisesAsync(request, cancellationToken);
            return Results.Ok(new
            {
                patternName = request.PatternName,
                bloomLevel = request.BloomLevel,
                totalExercises = exercises.Count,
                exercises
            });
        })
        .WithName("GeneratePatternExercises");

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
                        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
                        if (!string.IsNullOrWhiteSpace(envKey) && !envKey.StartsWith("YOUR_"))
                        {
                            geminiKey = envKey.Trim();
                        }
                    }
                }
            }

            // 2. Xác định danh sách Gemini Models từ cấu hình (appsettings.json)
            var configuredGeminiModels = configuration.GetSection("AiSettings:GeminiModels").Get<string[]>();
            var geminiModelList = (configuredGeminiModels != null && configuredGeminiModels.Length > 0)
                ? configuredGeminiModels.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList()
                : new List<string> { "gemini-flash-lite-latest", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.8-flash", "gemini-3.6-flash" };

            string GetGeminiRoleDescription(string modelId) => modelId switch
            {
                "gemini-flash-lite-latest" => "Top 1 Khuyến nghị: Tốc độ < 1s/trang, nhận diện LaTeX/Bảng chuẩn, tiết kiệm token tối đa",
                "gemini-3.5-flash-lite" => "Dòng Flash-Lite mới: Nhận diện ảnh scan tiếng Việt chính xác cao, chi phí tối ưu",
                "gemini-3.1-flash-lite" => "Dòng Flash-Lite ổn định: Tốc độ cao, tối ưu bóc tách công thức STEM",
                "gemini-3.8-flash" => "Thế hệ Flash 3.8: Đa phương thức nâng cao, xử lý sơ đồ/hình vẽ/bảng biểu phức tạp",
                "gemini-3.6-flash" => "Thế hệ Flash 3.6: Tư duy phân tích sâu, bóc tách tri thức và bài toán khó",
                _ => $"Mô hình Gemini cấu hình từ appsettings ({modelId})"
            };

            var geminiModels = geminiModelList.Select(m => new { Id = m, Role = GetGeminiRoleDescription(m) }).ToList();

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

            // 3. OpenAI Model Ping từ cấu hình (appsettings.json)
            string? openAiKey = configuration["AiSettings:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var configuredOpenAiModels = configuration.GetSection("AiSettings:OpenAiModels").Get<string[]>();
            string openAiModel = (configuredOpenAiModels != null && configuredOpenAiModels.Length > 0 && !string.IsNullOrWhiteSpace(configuredOpenAiModels[0]))
                ? configuredOpenAiModels[0].Trim()
                : "gpt-4o-mini";

            if (!string.IsNullOrWhiteSpace(openAiKey) && !openAiKey.StartsWith("YOUR_"))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var openAiPayload = new
                    {
                        model = openAiModel,
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
                        model = openAiModel,
                        role = $"Dự phòng Cloud: Mô hình {openAiModel} nhận diện tiếng Việt tốt, dự phòng khi Google Cloud chạm quota",
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
                        model = openAiModel,
                        role = "Dự phòng Cloud",
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
                    bestSpeedAndQuality = "gemini-flash-lite-latest & gemini-3.5-flash-lite (Latency < 1s, chuẩn LaTeX/Bảng, tiết kiệm token)",
                    bestComplexDiagrams = "gemini-3.8-flash & gemini-3.6-flash (Đa phương thức thế hệ mới, tối ưu sơ đồ/hình vẽ)",
                    bestFallback = $"{openAiModel} (OpenAI khi hết quota Google)"
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
