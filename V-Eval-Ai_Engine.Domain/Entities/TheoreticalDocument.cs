namespace V_Eval_Ai_Engine.Domain.Entities;

/// <summary>
/// Đại diện cho một tài liệu hoặc bài giảng lý thuyết được nạp vào AI Engine để phục vụ RAG
/// </summary>
public class TheoreticalDocument
{
    public string DocumentId { get; private set; }
    public string Title { get; private set; }
    public string Content { get; private set; }
    public string SkillId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public TheoreticalDocument(string documentId, string title, string content, string skillId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId không được để trống.", nameof(documentId));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title không được để trống.", nameof(title));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content không được để trống.", nameof(content));

        DocumentId = documentId;
        Title = title;
        Content = content;
        SkillId = skillId;
        CreatedAt = DateTime.UtcNow;
    }
}
