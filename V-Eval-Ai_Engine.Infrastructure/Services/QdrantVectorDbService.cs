using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ lưu trữ và truy xuất vector embeddings phục vụ RAG (kết nối Qdrant Vector DB)
/// </summary>
public class QdrantVectorDbService : IVectorDbService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<QdrantVectorDbService> _logger;

    // Bộ nhớ đệm tạm thời in-memory cho đến khi cụm Qdrant chính thức được kết nối
    private static readonly ConcurrentDictionary<string, KnowledgeChunkDto> InMemoryChunks = new();

    public QdrantVectorDbService(
        IConfiguration configuration,
        ILogger<QdrantVectorDbService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task IndexTheoreticalDocumentAsync(
        string documentId, 
        string title, 
        string content, 
        string skillId, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Đang lập chỉ mục tài liệu lý thuyết '{Title}' (Skill: {SkillId}) vào Vector DB...", title, skillId);

        // Đơn giản hóa chia đoạn văn bản (chunking) theo dòng hoặc đoạn
        var paragraphs = content.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        
        int index = 0;
        foreach (var p in paragraphs)
        {
            var chunkId = $"{documentId}_chunk_{++index}";
            var chunk = new KnowledgeChunkDto
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                SkillId = skillId,
                Content = p.Trim(),
                SimilarityScore = 1.0f
            };
            InMemoryChunks[chunkId] = chunk;
        }

        _logger.LogInformation("Lập chỉ mục thành công {Count} mảnh tri thức cho tài liệu '{Title}'.", paragraphs.Length, title);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KnowledgeChunkDto>> SearchSimilarChunksAsync(
        string query, 
        string? skillId = null, 
        int limit = 3, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tìm kiếm tri thức tương đồng cho truy vấn: '{Query}' (Skill: {SkillId})", query, skillId);

        var queryWords = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Tìm kiếm tạm thời theo từ khóa liên quan trong in-memory store
        var matched = InMemoryChunks.Values
            .Where(c => string.IsNullOrEmpty(skillId) || c.SkillId.Equals(skillId, StringComparison.OrdinalIgnoreCase))
            .Select(c =>
            {
                int matchCount = queryWords.Count(w => c.Content.Contains(w, StringComparison.OrdinalIgnoreCase));
                float score = queryWords.Length > 0 ? (float)matchCount / queryWords.Length : 0.5f;
                return c with { SimilarityScore = score };
            })
            .OrderByDescending(c => c.SimilarityScore)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<KnowledgeChunkDto>>(matched);
    }
}
