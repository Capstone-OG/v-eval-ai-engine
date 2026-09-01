namespace V_Eval_Ai_Engine.Application.DTOs;

/// <summary>
/// DTO chứa thông tin mảnh tri thức sau khi tìm kiếm tương đồng trên Vector DB
/// </summary>
public record KnowledgeChunkDto
{
    public string ChunkId { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
    public string SkillId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public float SimilarityScore { get; init; }
}

/// <summary>
/// DTO gửi yêu cầu đối thoại gia sư gợi mở
/// </summary>
public record SocraticChatRequestDto
{
    public string StudentId { get; init; } = string.Empty;
    public string QuestionId { get; init; } = string.Empty;
    public string CurrentDialogue { get; init; } = string.Empty;
    public List<string> ChatHistory { get; init; } = new();
}
