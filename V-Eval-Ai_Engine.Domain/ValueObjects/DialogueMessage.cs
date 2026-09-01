namespace V_Eval_Ai_Engine.Domain.ValueObjects;

/// <summary>
/// Đại diện cho một câu thoại trong cuộc trò chuyện Socratic
/// </summary>
public record DialogueMessage
{
    public string Role { get; init; } = "user"; // "user", "assistant", "system"
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
