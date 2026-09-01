namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng điều phối hội thoại gia sư ảo Socratic hỗ trợ học sinh theo thời gian thực (Streaming)
/// </summary>
public interface ISocraticTutorService
{
    /// <summary>
    /// Tiếp nhận ngữ cảnh câu hỏi và hội thoại, kết hợp tri thức RAG và stream từng token phản hồi
    /// </summary>
    IAsyncEnumerable<string> StreamSocraticDialogueAsync(
        string studentId, 
        string questionId, 
        string currentDialogue, 
        IEnumerable<string> chatHistory, 
        CancellationToken cancellationToken = default);
}
