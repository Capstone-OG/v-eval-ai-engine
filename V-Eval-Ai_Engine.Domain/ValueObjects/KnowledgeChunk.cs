namespace V_Eval_Ai_Engine.Domain.ValueObjects;

/// <summary>
/// Mảnh văn bản (Chunk) sau khi phân tách tài liệu lý thuyết, kèm vector embedding phục vụ tìm kiếm tương đồng trên Qdrant
/// </summary>
public record KnowledgeChunk
{
    public string ChunkId { get; init; } = Guid.NewGuid().ToString();
    public string DocumentId { get; init; } = string.Empty;
    public string SkillId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public float[]? Embedding { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}
