var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    
    app.MapGet("/swagger/index.html", () => 
    {
        string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
  <title>Swagger UI</title>
  <link rel=""stylesheet"" href=""https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"" />
</head>
<body>
  <div id=""swagger-ui""></div>
  <script src=""https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"" charset=""UTF-8""></script>
  <script>
    window.onload = () => {
      window.ui = SwaggerUIBundle({
        url: '/openapi/v1.json',
        dom_id: '#swagger-ui',
      });
    };
  </script>
</body>
</html>";
        return Results.Content(html, "text/html");
    })
    .ExcludeFromDescription();

    app.MapGet("/swagger", () => Results.Redirect("/swagger/index.html"))
    .ExcludeFromDescription();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapGet("/api/ai-engine/parsed-document", async () =>
{
    string[] candidatePaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "ml_training", "parsed_output.json"),
        Path.Combine(AppContext.BaseDirectory, "ml_training", "parsed_output.json"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ml_training", "parsed_output.json"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ml_training", "parsed_output.json")
    };

    string? foundPath = null;
    foreach (var path in candidatePaths)
    {
        if (File.Exists(path))
        {
            foundPath = path;
            break;
        }
    }

    if (foundPath == null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy file parsed_output.json. Đã tìm ở các thư mục: {string.Join(", ", candidatePaths)}" });
    }

    try
    {
        string jsonContent = await File.ReadAllTextAsync(foundPath);
        return Results.Content(jsonContent, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi đọc file: {ex.Message}");
    }
})
.WithName("GetParsedDocument");

app.MapGet("/api/ai-engine/parse-pdf", async (string filePath, IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(filePath))
    {
        return Results.BadRequest(new { message = "Đường dẫn filePath không được để trống." });
    }

    // Tìm đường dẫn tuyệt đối của script Python
    string[] candidateScriptPaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "ml_training", "parse_single_pdf.py"),
        Path.Combine(Directory.GetCurrentDirectory(), "..", "ml_training", "parse_single_pdf.py"),
        Path.Combine(AppContext.BaseDirectory, "ml_training", "parse_single_pdf.py"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ml_training", "parse_single_pdf.py"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ml_training", "parse_single_pdf.py")
    };

    string? scriptPath = null;
    foreach (var path in candidateScriptPaths)
    {
        if (File.Exists(path))
        {
            scriptPath = path;
            break;
        }
    }

    if (scriptPath == null)
    {
        return Results.Problem($"Không tìm thấy script Python parse_single_pdf.py. Đã tìm ở các thư mục: {string.Join(", ", candidateScriptPaths)}");
    }

    try
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "python";
        process.StartInfo.Arguments = $"\"{scriptPath}\" \"{filePath}\"";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;

        var apiKey = configuration.GetSection("AiSettings")["ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            process.StartInfo.Environment["OPENAI_API_KEY"] = apiKey;
        }
        var geminiKey = configuration.GetSection("AiSettings")["GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(geminiKey))
        {
            process.StartInfo.Environment["GEMINI_API_KEY"] = geminiKey;
        }

        process.Start();

        var errorList = new System.Collections.Generic.List<string>();
        var readErrorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
            {
                Console.WriteLine($"[AI-Engine Python] {line}");
                errorList.Add(line);
            }
        });

        string output = await process.StandardOutput.ReadToEndAsync();
        await readErrorTask;
        await process.WaitForExitAsync();
        string error = string.Join(Environment.NewLine, errorList);

        if (process.ExitCode == 0)
        {
            return Results.Content(output, "application/json");
        }
        else
        {
            return Results.Problem(
                detail: string.IsNullOrWhiteSpace(error) ? "Lỗi không xác định khi chạy Python" : error,
                statusCode: 500,
                title: "Lỗi chạy script trích xuất Python"
            );
        }
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi hệ thống khi gọi Python: {ex.Message}");
    }
})
.WithName("ParsePdfFile");

app.MapPost("/api/ai-engine/upload-pdf", async (IFormFile file, IConfiguration configuration) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "Không nhận được file hoặc file rỗng." });
    }

    if (Path.GetExtension(file.FileName).ToLower() != ".pdf")
    {
        return Results.BadRequest(new { message = "Chỉ chấp nhận file định dạng PDF." });
    }

    // Tạo đường dẫn lưu file trong wwwroot/uploads để hiển thị nguyên bản trên UI
    string uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
    if (!Directory.Exists(uploadsDir))
    {
        Directory.CreateDirectory(uploadsDir);
    }

    string uniqueFileName = Guid.NewGuid().ToString() + ".pdf";
    string savedFilePath = Path.Combine(uploadsDir, uniqueFileName);
    try
    {
        using (var stream = new FileStream(savedFilePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Tìm đường dẫn tuyệt đối của script Python
        string[] candidateScriptPaths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "ml_training", "parse_single_pdf.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "ml_training", "parse_single_pdf.py"),
            Path.Combine(AppContext.BaseDirectory, "ml_training", "parse_single_pdf.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ml_training", "parse_single_pdf.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ml_training", "parse_single_pdf.py")
        };

        string? scriptPath = null;
        foreach (var path in candidateScriptPaths)
        {
            if (File.Exists(path))
            {
                scriptPath = path;
                break;
            }
        }

        if (scriptPath == null)
        {
            return Results.Problem($"Không tìm thấy script Python parse_single_pdf.py. Đã tìm ở các thư mục: {string.Join(", ", candidateScriptPaths)}");
        }

        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "python";
        process.StartInfo.Arguments = $"\"{scriptPath}\" \"{savedFilePath}\"";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;

        var apiKey = configuration.GetSection("AiSettings")["ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            process.StartInfo.Environment["OPENAI_API_KEY"] = apiKey;
        }
        var geminiKey = configuration.GetSection("AiSettings")["GeminiApiKey"];
        if (!string.IsNullOrWhiteSpace(geminiKey))
        {
            process.StartInfo.Environment["GEMINI_API_KEY"] = geminiKey;
        }

        process.Start();

        var errorList = new System.Collections.Generic.List<string>();
        var readErrorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
            {
                Console.WriteLine($"[AI-Engine Python] {line}");
                errorList.Add(line);
            }
        });

        string output = await process.StandardOutput.ReadToEndAsync();
        await readErrorTask;
        await process.WaitForExitAsync();
        string error = string.Join(Environment.NewLine, errorList);

        if (process.ExitCode == 0)
        {
            try
            {
                var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(output);
                if (jsonNode != null)
                {
                    jsonNode["pdf_url"] = $"/uploads/{uniqueFileName}";
                    return Results.Content(jsonNode.ToJsonString(), "application/json");
                }
            }
            catch { }
            return Results.Content(output, "application/json");
        }
        else
        {
            return Results.Problem(
                detail: string.IsNullOrWhiteSpace(error) ? "Lỗi không xác định khi chạy Python" : error,
                statusCode: 500,
                title: "Lỗi chạy script trích xuất Python"
            );
        }
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi hệ thống khi gọi Python: {ex.Message}");
    }
})
.WithName("UploadAndParsePdfFile")
.DisableAntiforgery();

app.MapGet("/api/ai-engine/view-exam", async () =>
{
    string[] candidatePaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "view-exam.html"),
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "view-exam.html"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot", "view-exam.html"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "wwwroot", "view-exam.html")
    };

    foreach (var path in candidatePaths)
    {
        if (File.Exists(path))
        {
            string htmlContent = await File.ReadAllTextAsync(path);
            return Results.Content(htmlContent, "text/html");
        }
    }

    return Results.NotFound(new { message = $"Không tìm thấy file view-exam.html. Đã tìm ở: {string.Join(", ", candidatePaths)}" });
})
.WithName("ViewExamViewerPage");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
