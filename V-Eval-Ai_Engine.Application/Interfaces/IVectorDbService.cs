using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Hợp đồng tương tác với Vector Database (Qdrant) phục vụ RAG
/// </summary>
public interface IVectorDbService
{
    /// <summary>
    /// Chunking văn bản bài giảng lý thuyết, sinh Vector Embeddings và nạp vào Qdrant
    /// </summary>
    Task IndexTheoreticalDocumentAsync(string documentId, string title, string content, string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm các mảnh tài liệu có độ tương đồng cosine cao nhất với câu truy vấn
    /// </summary>
    Task<IReadOnlyList<KnowledgeChunkDto>> SearchSimilarChunksAsync(string query, string? skillId = null, int limit = 3, CancellationToken cancellationToken = default);
}
