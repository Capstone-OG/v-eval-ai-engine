using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ gia sư ảo Socratic hỗ trợ học sinh ôn thi ĐGNL theo phương pháp gợi mở
/// </summary>
public class SocraticTutorService : ISocraticTutorService
{
    private readonly IVectorDbService _vectorDbService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SocraticTutorService> _logger;
    private readonly IPromptTemplateService _promptTemplateService;

    public SocraticTutorService(
        IVectorDbService vectorDbService,
        IConfiguration configuration,
        ILogger<SocraticTutorService> logger,
        IPromptTemplateService promptTemplateService)
    {
        _vectorDbService = vectorDbService;
        _configuration = configuration;
        _logger = logger;
        _promptTemplateService = promptTemplateService;
    }

    public async IAsyncEnumerable<string> StreamSocraticDialogueAsync(
        string studentId,
        string questionId,
        string currentDialogue,
        IEnumerable<string> chatHistory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Nhận yêu cầu Socratic Tutor từ học sinh {StudentId} cho câu hỏi {QuestionId}...", studentId, questionId);

        // 1. Bước RAG: Tìm kiếm tài liệu lý thuyết tương đồng trong Vector DB
        var contextChunks = await _vectorDbService.SearchSimilarChunksAsync(currentDialogue, limit: 2, cancellationToken: cancellationToken);
        string retrievedKnowledge = string.Join("\n---\n", contextChunks.Select(c => c.Content));

        // 2. Nạp System Prompt Socratic từ File Template độc lập qua IPromptTemplateService
        string socraticPrompt = await _promptTemplateService.RenderPromptAsync(
            "SocraticTutor.prompt.txt",
            new Dictionary<string, string>
            {
                ["retrievedKnowledge"] = retrievedKnowledge
            },
            cancellationToken);

        // 3. Phản hồi luồng (Streaming generator)
        // Khi kết nối mô hình LLM chính thức, sẽ stream token trực tiếp từ response stream.
        // Dưới đây là phản hồi dẫn dắt chuẩn Socratic mẫu được stream token hóa theo thời gian thực:
        string sampleResponse = $"Chào bạn, mình đã xem câu hỏi này rồi. " +
            $"Trước tiên, bạn hãy quan sát kỹ giả thiết đề bài đưa ra. " +
            $"Theo lý thuyết cốt lõi: công thức áp dụng ở đây phụ thuộc vào điều kiện xác định ban đầu. " +
            $"Theo bạn, bước đầu tiên chúng ta cần làm để biến đổi biểu thức này là gì? Hãy thử nêu hướng suy nghĩ của bạn nhé!";

        var tokens = sampleResponse.Split(' ');
        foreach (var word in tokens)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            yield return word + " ";
            await Task.Delay(40, cancellationToken); // Mô phỏng độ trễ sinh từ (typing effect)
        }
    }
}
