using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using V_Eval_Ai_Engine.Application.Interfaces;
using V_Eval_Ai_Engine.Infrastructure.Services;

namespace V_Eval_Ai_Engine.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Đăng ký HttpClient cho Gemini Parser với timeout tối ưu
        services.AddHttpClient<IExamParserService, GeminiExamParserService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(180);
        });

        // Đăng ký Vector Database Service cho RAG
        services.AddSingleton<IVectorDbService, QdrantVectorDbService>();

        // Đăng ký Socratic AI Tutor Service
        services.AddScoped<ISocraticTutorService, SocraticTutorService>();

        return services;
    }
}
