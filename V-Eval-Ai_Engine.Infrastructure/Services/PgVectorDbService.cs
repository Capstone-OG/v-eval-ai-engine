using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ truy vấn & lập chỉ mục Vector Database thật trên PostgreSQL pgvector (HNSW 768 chiều)
/// Thay thế hoàn toàn QdrantVectorDbService in-memory cũ.
/// </summary>
public class PgVectorDbService : IVectorDbService
{
    private readonly string _connectionString;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITextbookRepository _textbookRepository;
    private readonly ILogger<PgVectorDbService> _logger;

    public PgVectorDbService(
        IConfiguration configuration,
        IEmbeddingService embeddingService,
        ITextbookRepository textbookRepository,
        ILogger<PgVectorDbService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? configuration["ConnectionStrings:DefaultConnection"]
                            ?? string.Empty;
        _embeddingService = embeddingService;
        _textbookRepository = textbookRepository;
        _logger = logger;
    }

    public async Task IndexTheoreticalDocumentAsync(
        string documentId,
        string title,
        string content,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var chunks = SplitIntoOverlappingChunks(content, chunkSize: 1000, overlap: 200);
        if (chunks.Count == 0) return;

        var embeddings = await _embeddingService.EmbedBatchAsync(chunks, "RETRIEVAL_DOCUMENT", cancellationToken);
        var sourceId = await _textbookRepository.GetOrCreateSourceAsync(
            fileHash: $"doc_{documentId}",
            title: title,
            fileUrl: string.Empty,
            totalPages: 1,
            domainId: string.Empty,
            skillId: skillId ?? string.Empty,
            docType: "CURRICULUM",
            cancellationToken: cancellationToken);

        for (int i = 0; i < chunks.Count; i++)
        {
            await _textbookRepository.SavePageChunkAsync(
                sourceId: sourceId,
                pageNumber: 1,
                chunkIndex: i + 1,
                contentSnippet: chunks[i],
                domainId: string.Empty,
                skillId: skillId ?? string.Empty,
                docType: "CURRICULUM",
                cancellationToken: cancellationToken,
                chunkMetadata: null,
                embedding: embeddings[i]);
        }

        await _textbookRepository.UpdateSourceProgressAsync(
            sourceId, 1, content.Length, chunks.Count, "COMPLETED", cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeChunkDto>> SearchSimilarChunksAsync(
        string query,
        string? skillId = null,
        int limit = 3,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(_connectionString))
            return Array.Empty<KnowledgeChunkDto>();

        var queryVec = await _embeddingService.EmbedTextAsync(query, "RETRIEVAL_QUERY", cancellationToken);
        if (queryVec == null)
        {
            _logger.LogWarning("Không thể sinh vector truy vấn cho '{Query}', trả về danh sách rỗng.", query);
            return Array.Empty<KnowledgeChunkDto>();
        }

        var vecLiteral = GeminiEmbeddingService.ToPgVectorLiteral(queryVec);
        int safeLimit = Math.Clamp(limit, 1, 20);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            SELECT chunk_id, source_id, COALESCE(skill_id, ''), page_number, chunk_index, content_snippet,
                   1 - (embedding <=> @query_vec::vector) AS similarity
            FROM v_eval_ai.""KnowledgeVectorChunks""
            WHERE embedding IS NOT NULL
              AND (@skill_id IS NULL OR skill_id = @skill_id)
            ORDER BY embedding <=> @query_vec::vector
            LIMIT @limit;";

        var topHits = new List<(Guid ChunkId, Guid SourceId, string SkillId, int PageNumber, int ChunkIndex, string Content, float Similarity)>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("query_vec", vecLiteral);
            cmd.Parameters.AddWithValue("skill_id", string.IsNullOrWhiteSpace(skillId) ? DBNull.Value : skillId);
            cmd.Parameters.AddWithValue("limit", safeLimit);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                topHits.Add((
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    (float)reader.GetDouble(6)
                ));
            }
        }

        if (topHits.Count == 0)
            return Array.Empty<KnowledgeChunkDto>();

        // Neighbor Chunk Expansion: ghép thêm chunk_index - 1 và chunk_index + 1 cùng source_id để đủ ngữ cảnh
        var results = new List<KnowledgeChunkDto>(topHits.Count);
        const string neighborSql = @"
            SELECT chunk_index, content_snippet
            FROM v_eval_ai.""KnowledgeVectorChunks""
            WHERE source_id = @source_id
              AND chunk_index BETWEEN @min_idx AND @max_idx
            ORDER BY chunk_index ASC;";

        foreach (var hit in topHits)
        {
            var sb = new StringBuilder();
            await using (var nCmd = new NpgsqlCommand(neighborSql, conn))
            {
                nCmd.Parameters.AddWithValue("source_id", hit.SourceId);
                nCmd.Parameters.AddWithValue("min_idx", Math.Max(0, hit.ChunkIndex - 1));
                nCmd.Parameters.AddWithValue("max_idx", hit.ChunkIndex + 1);

                await using var nReader = await nCmd.ExecuteReaderAsync(cancellationToken);
                while (await nReader.ReadAsync(cancellationToken))
                {
                    var snippet = nReader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(snippet))
                    {
                        if (sb.Length > 0) sb.AppendLine("\n");
                        sb.Append(snippet.Trim());
                    }
                }
            }

            results.Add(new KnowledgeChunkDto
            {
                ChunkId = hit.ChunkId.ToString(),
                DocumentId = hit.SourceId.ToString(),
                SkillId = hit.SkillId,
                Content = sb.Length > 0 ? sb.ToString() : hit.Content,
                SimilarityScore = hit.Similarity
            });
        }

        return results;
    }

    public static List<string> SplitIntoOverlappingChunks(string text, int chunkSize = 1000, int overlap = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var trimmed = text.Trim();
        if (trimmed.Length <= chunkSize) return [trimmed];

        var chunks = new List<string>();
        int start = 0;
        while (start < trimmed.Length)
        {
            int maxEnd = Math.Min(start + chunkSize, trimmed.Length);
            int end = maxEnd;

            if (maxEnd < trimmed.Length)
            {
                // Ưu tiên ngắt tại ranh giới đoạn (\n\n) hoặc câu (.) ngoài vùng công thức $...$
                int paraBreak = trimmed.LastIndexOf("\n\n", maxEnd, Math.Min(300, maxEnd - start), StringComparison.Ordinal);
                int sentenceBreak = trimmed.LastIndexOf(". ", maxEnd, Math.Min(250, maxEnd - start), StringComparison.Ordinal);
                int candidate = paraBreak > start + chunkSize / 2
                    ? paraBreak + 2
                    : sentenceBreak > start + chunkSize / 2
                        ? sentenceBreak + 1
                        : maxEnd;

                // Tránh cắt ngang bên trong cặp $...$
                int dollarCount = 0;
                for (int i = start; i < candidate; i++)
                {
                    if (trimmed[i] == '$' && (i == 0 || trimmed[i - 1] != '\\'))
                        dollarCount++;
                }
                if (dollarCount % 2 == 1)
                {
                    int closingDollar = trimmed.IndexOf('$', candidate);
                    if (closingDollar > candidate && closingDollar - start <= chunkSize + 250)
                        candidate = closingDollar + 1;
                }
                end = candidate;
            }

            var slice = trimmed[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(slice))
                chunks.Add(slice);

            if (end >= trimmed.Length) break;
            int nextStart = Math.Max(end - overlap, start + 1);
            start = nextStart;
        }

        return chunks;
    }
}
