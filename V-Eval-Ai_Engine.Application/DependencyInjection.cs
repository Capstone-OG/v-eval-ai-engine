using Microsoft.Extensions.DependencyInjection;

namespace V_Eval_Ai_Engine.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Có thể đăng ký các MediatR handlers, Validators, hoặc Domain Event Listeners tại đây nếu có
        return services;
    }
}
