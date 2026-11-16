namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng dịch vụ quản lý và nạp mẫu Prompt độc lập cho AI Engine:
/// - Tách biệt tri thức sư phạm và prompt engineering ra khỏi mã nguồn logic C#
/// - Hỗ trợ nạp template từ file, bộ nhớ đệm cache và điền biến động {{variable}}
/// </summary>
public interface IPromptTemplateService
{
    /// <summary>
    /// Nạp và render prompt bất đồng bộ theo tên mẫu template và các tham số biến động
    /// </summary>
    Task<string> RenderPromptAsync(string templateName, IDictionary<string, string>? variables = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nạp và render prompt đồng bộ theo tên mẫu template và các tham số biến động
    /// </summary>
    string RenderPrompt(string templateName, IDictionary<string, string>? variables = null);
}

