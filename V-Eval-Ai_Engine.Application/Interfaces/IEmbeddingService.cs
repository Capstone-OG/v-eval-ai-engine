namespace V_Eval_Ai_Engine.Application.Interfaces;

/// <summary>
/// Dịch vụ nhúng văn bản thành vector 768 chiều (Gemini Embedding API - MRL) phục vụ pgvector HNSW
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Nhúng 1 đoạn văn bản / câu hỏi thành vector 768 chiều đã chuẩn hóa L2
    /// </summary>
    Task<float[]?> EmbedTextAsync(
        string text,
        string taskType = "RETRIEVAL_QUERY",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Nhúng hàng loạt nhiều đoạn văn bản (batchEmbedContents) thành các vector 768 chiều đã chuẩn hóa L2
    /// </summary>
    Task<List<float[]?>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        string taskType = "RETRIEVAL_DOCUMENT",
        CancellationToken cancellationToken = default);
}
