using V_Eval_Ai_Engine.API.Endpoints;
using V_Eval_Ai_Engine.API.GrpcServices;
using V_Eval_Ai_Engine.Application;
using V_Eval_Ai_Engine.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình dịch vụ hạ tầng & ứng dụng theo chuẩn Clean Architecture
builder.Services.AddOpenApi();
builder.Services.AddGrpc();
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

// 2. Cấu hình HTTP Request Pipeline & Swagger UI
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
  <title>V-Eval AI Engine - Swagger UI</title>
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

// 3. Đăng ký các endpoints và gRPC Services
app.MapGrpcService<AiGrpcService>();
app.MapExamEndpoints();

app.Run();
