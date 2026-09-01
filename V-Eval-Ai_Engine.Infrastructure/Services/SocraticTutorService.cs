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

    public SocraticTutorService(
        IVectorDbService vectorDbService,
        IConfiguration configuration,
        ILogger<SocraticTutorService> logger)
    {
        _vectorDbService = vectorDbService;
        _configuration = configuration;
        _logger = logger;
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

        // 2. Chuẩn bị System Prompt Socratic theo quy định nghiệp vụ V-Eval
        string socraticPrompt = $@"Bạn là một Gia sư trí tuệ nhân tạo (AI Tutor) hỗ trợ học sinh ôn thi Đánh giá năng lực ĐHQG-HCM theo phương pháp gợi mở (Socratic Method).
Tài liệu lý thuyết tham khảo:
{retrievedKnowledge}

Nhiệm vụ của bạn:
1. KHÔNG BAO GIỜ được cho học sinh biết đáp án đúng hay lời giải đầy đủ ngay lập tức.
2. Hãy chỉ ra điểm chưa hợp lý trong cách tư duy hoặc phương án học sinh đưa ra.
3. Giải thích ngắn gọn khái niệm lý thuyết cốt lõi cần dùng.
4. Đặt 1-2 câu hỏi nhỏ dẫn dắt gợi ý để học sinh tự suy nghĩ và tự tìm ra câu trả lời đúng.
5. Luôn phản hồi bằng tiếng Việt lịch sự, định dạng Markdown gọn gàng.";

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
