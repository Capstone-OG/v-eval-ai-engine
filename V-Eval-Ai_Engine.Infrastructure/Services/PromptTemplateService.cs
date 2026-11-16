using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ quản lý và biên dịch các mẫu Prompt từ file template:
/// - Quản lý File-based Prompt Templates độc lập tại thư mục Templates/Prompts/
/// - Tự động nạp, cache bộ nhớ đệm ConcurrentDictionary, tối ưu hiệu năng I/O
/// - Hỗ trợ thay thế biến động dạng {{variable}} không gây xung đột với cú pháp JSON
/// </summary>
public class PromptTemplateService : IPromptTemplateService
{
    private readonly ILogger<PromptTemplateService> _logger;
    private readonly ConcurrentDictionary<string, string> _templateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _templateBasePath;

    public PromptTemplateService(ILogger<PromptTemplateService> logger)
    {
        _logger = logger;
        _templateBasePath = ResolveTemplateBasePath();
    }

    private string ResolveTemplateBasePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Templates", "Prompts"),
            Path.Combine(Directory.GetCurrentDirectory(), "Templates", "Prompts"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Templates", "Prompts"),
            Path.Combine(Directory.GetCurrentDirectory(), "All Services", "V-Eval-Ai_Engine", "V-Eval-Ai_Engine.Infrastructure", "Templates", "Prompts")
        };

        foreach (var path in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                {
                    _logger.LogInformation("PromptTemplateService: Đã tìm thấy thư mục template tại '{Path}'", fullPath);
                    return fullPath;
                }
            }
            catch
            {
                // Bỏ qua lỗi truy cập đường dẫn
            }
        }

        string fallbackPath = Path.Combine(AppContext.BaseDirectory, "Templates", "Prompts");
        _logger.LogWarning("PromptTemplateService: Không tìm thấy thư mục template tồn tại, sử dụng fallback: '{Path}'", fallbackPath);
        return fallbackPath;
    }

    public async Task<string> RenderPromptAsync(
        string templateName,
        IDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        string rawTemplate = await GetRawTemplateAsync(templateName, cancellationToken);
        return ApplyVariables(rawTemplate, variables);
    }

    public string RenderPrompt(
        string templateName,
        IDictionary<string, string>? variables = null)
    {
        string rawTemplate = GetRawTemplate(templateName);
        return ApplyVariables(rawTemplate, variables);
    }

    private async Task<string> GetRawTemplateAsync(string templateName, CancellationToken cancellationToken)
    {
        string normalizedName = NormalizeTemplateName(templateName);

        if (_templateCache.TryGetValue(normalizedName, out var cached))
        {
            return cached;
        }

        string filePath = Path.Combine(_templateBasePath, normalizedName);
        if (File.Exists(filePath))
        {
            try
            {
                string content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
                _templateCache[normalizedName] = content;
                _logger.LogInformation("PromptTemplateService: Nạp thành công template '{Name}' từ đĩa ({Bytes} bytes).", normalizedName, content.Length);
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PromptTemplateService: Lỗi đọc file template '{Path}', chuyển sang fallback bộ nhớ.", filePath);
            }
        }
        else
        {
            _logger.LogWarning("PromptTemplateService: Không tìm thấy file template tại '{Path}'.", filePath);
        }

        string fallbackContent = GetHardcodedFallbackTemplate(normalizedName);
        _templateCache[normalizedName] = fallbackContent;
        return fallbackContent;
    }

    private string GetRawTemplate(string templateName)
    {
        string normalizedName = NormalizeTemplateName(templateName);

        if (_templateCache.TryGetValue(normalizedName, out var cached))
        {
            return cached;
        }

        string filePath = Path.Combine(_templateBasePath, normalizedName);
        if (File.Exists(filePath))
        {
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                _templateCache[normalizedName] = content;
                _logger.LogInformation("PromptTemplateService: Nạp thành công template '{Name}' từ đĩa ({Bytes} bytes).", normalizedName, content.Length);
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PromptTemplateService: Lỗi đọc file template '{Path}', chuyển sang fallback bộ nhớ.", filePath);
            }
        }

        string fallbackContent = GetHardcodedFallbackTemplate(normalizedName);
        _templateCache[normalizedName] = fallbackContent;
        return fallbackContent;
    }

    private static string NormalizeTemplateName(string name)
    {
        string trimmed = name.Trim();
        if (!trimmed.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && 
            !trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += ".prompt.txt";
        }
        return trimmed;
    }

    private static string ApplyVariables(string template, IDictionary<string, string>? variables)
    {
        if (variables == null || variables.Count == 0 || string.IsNullOrEmpty(template))
        {
            return template;
        }

        var sb = new StringBuilder(template);
        foreach (var (key, value) in variables)
        {
            string token = "{{" + key + "}}";
            sb.Replace(token, value ?? string.Empty);
        }

        return sb.ToString();
    }

    private static string GetHardcodedFallbackTemplate(string templateName)
    {
        return templateName.ToLowerInvariant() switch
        {
            var n when n.Contains("socratic") =>
                "Bạn là một Gia sư trí tuệ nhân tạo (AI Tutor) hỗ trợ học sinh ôn thi Đánh giá năng lực ĐHQG-HCM theo phương pháp gợi mở (Socratic Method).\n" +
                "Tài liệu lý thuyết tham khảo:\n{{retrievedKnowledge}}\n\n" +
                "Nhiệm vụ của bạn:\n" +
                "1. KHÔNG BAO GIỜ được cho học sinh biết đáp án đúng hay lời giải đầy đủ ngay lập tức.\n" +
                "2. Hãy chỉ ra điểm chưa hợp lý trong cách tư duy hoặc phương án học sinh đưa ra.\n" +
                "3. Giải thích ngắn gọn khái niệm lý thuyết cốt lõi cần dùng.\n" +
                "4. Đặt 1-2 câu hỏi nhỏ dẫn dắt gợi ý để học sinh tự suy nghĩ và tự tìm ra câu trả lời đúng.\n" +
                "5. Luôn phản hồi bằng tiếng Việt lịch sự, định dạng Markdown gọn gàng.",

            _ => "BẠN LÀ CHUYÊN GIA SƯ PHẠM V-EVAL & ĐÁNH GIÁ NĂNG LỰC ĐHQG."
        };
    }
}

