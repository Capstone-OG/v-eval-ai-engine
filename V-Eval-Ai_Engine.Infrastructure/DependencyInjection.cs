using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using V_Eval_Ai_Engine.Application.Interfaces;
using V_Eval_Ai_Engine.Infrastructure.Services;

namespace V_Eval_Ai_Engine.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Đăng ký HttpClient cho Gemini Parser với timeout thoải mái cho đề thi phức tạp (8 phút)
        services.AddHttpClient<IExamParserService, GeminiExamParserService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(8);
        });

        // Đăng ký Background Job Manager quản lý tiến trình nền
        services.AddSingleton<IExamJobManager, InMemoryExamJobManager>();
        services.AddSingleton<ITextbookJobManager, InMemoryTextbookJobManager>();

        // Đăng ký dịch vụ nạp tri thức SGK local offline & Vision OCR
        services.AddHttpClient<ITextbookParserService, PdfPigTextbookParserService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        // Đăng ký Vector Database Service cho RAG
        services.AddSingleton<IVectorDbService, QdrantVectorDbService>();

        // Đăng ký Socratic AI Tutor Service
        services.AddScoped<ISocraticTutorService, SocraticTutorService>();

        // Đăng ký dịch vụ trích xuất ảnh cục bộ từ PDF (PdfPig 100% Offline)
        services.AddSingleton<PdfImageExtractor>();

        // Đăng ký Repository lưu tri thức SGK vào Supabase PostgreSQL (Schema v_eval_ai)
        services.AddScoped<ITextbookRepository, V_Eval_Ai_Engine.Infrastructure.Repositories.TextbookRepository>();

        return services;
    }
}
