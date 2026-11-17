using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using V_Eval_Ai_Engine.Application.DTOs;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Repositories;

public class TextbookRepository : ITextbookRepository
{
    private readonly string _connectionString;
    private readonly ILogger<TextbookRepository> _logger;

    public TextbookRepository(IConfiguration configuration, ILogger<TextbookRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
                            ?? configuration["ConnectionStrings:DefaultConnection"]
                            ?? "Host=aws-0-ap-southeast-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.zwxavhoszodmagisoiqu;Password=ThinhTran2412;SSL Mode=Require;Trust Server Certificate=true";
        _logger = logger;
    }

    public async Task<(Guid SourceId, int LastProcessedPage, int TotalPages, string Status, int TotalChars, int TotalChunks)?> GetCheckpointByFileHashAsync(string fileHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileHash)) return null;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            SELECT s.source_id, s.total_pages, s.vector_status, s.total_chars, s.total_chunks,
                   COALESCE(MAX(c.page_number), 0) as last_page
            FROM v_eval_ai.""KnowledgeSources"" s
            LEFT JOIN v_eval_ai.""KnowledgeVectorChunks"" c ON s.source_id = c.source_id
            WHERE s.file_hash = @file_hash
            GROUP BY s.source_id, s.total_pages, s.vector_status, s.total_chars, s.total_chunks
            ORDER BY s.updated_at DESC
            LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("file_hash", fileHash);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var sourceId = reader.GetGuid(0);
            var totalPages = reader.GetInt32(1);
            var status = reader.GetString(2);
            var totalChars = reader.GetInt32(3);
            var totalChunks = reader.GetInt32(4);
            var lastPage = reader.GetInt32(5);

            return (sourceId, lastPage, totalPages, status, totalChars, totalChunks);
        }

        return null;
    }

    public async Task<Guid> GetOrCreateSourceAsync(
        string fileHash,
        string title,
        string fileUrl,
        int totalPages,
        string domainId,
        string skillId,
        string docType,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // 1. Kiểm tra nếu đã có source_id với file_hash này
        const string checkSql = @"SELECT source_id FROM v_eval_ai.""KnowledgeSources"" WHERE file_hash = @file_hash LIMIT 1;";
        await using (var checkCmd = new NpgsqlCommand(checkSql, conn))
        {
            checkCmd.Parameters.AddWithValue("file_hash", (object?)fileHash ?? DBNull.Value);
            var existing = await checkCmd.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid g)
            {
                // Cập nhật lại status PROCESSING và totalPages
                const string updateSql = @"
                    UPDATE v_eval_ai.""KnowledgeSources""
                    SET total_pages = @total_pages, vector_status = 'PROCESSING', updated_at = now()
                    WHERE source_id = @source_id;";
                await using var updateCmd = new NpgsqlCommand(updateSql, conn);
                updateCmd.Parameters.AddWithValue("total_pages", totalPages);
                updateCmd.Parameters.AddWithValue("source_id", g);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);

                return g;
            }
        }

        // 2. Chưa có thì tạo mới
        var newSourceId = Guid.NewGuid();
        const string insertSql = @"
            INSERT INTO v_eval_ai.""KnowledgeSources""
            (source_id, domain_id, skill_id, title, file_url, file_hash, document_type, total_pages, total_chars, total_chunks, vector_status, created_at, updated_at)
            VALUES (@source_id, @domain_id, @skill_id, @title, @file_url, @file_hash, @document_type, @total_pages, 0, 0, 'PROCESSING', now(), now());";

        await using (var insertCmd = new NpgsqlCommand(insertSql, conn))
        {
            insertCmd.Parameters.AddWithValue("source_id", newSourceId);
            insertCmd.Parameters.AddWithValue("domain_id", (object?)domainId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("skill_id", (object?)skillId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("title", title);
            insertCmd.Parameters.AddWithValue("file_url", (object?)fileUrl ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("file_hash", (object?)fileHash ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("document_type", (object?)docType ?? "TEXTBOOK");
            insertCmd.Parameters.AddWithValue("total_pages", totalPages);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return newSourceId;
    }

    public async Task SavePageChunkAsync(
        Guid sourceId,
        int pageNumber,
        int chunkIndex,
        string contentSnippet,
        string domainId,
        string skillId,
        string docType,
        CancellationToken cancellationToken = default,
        TextbookChunkDto? chunkMetadata = null)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            INSERT INTO v_eval_ai.""KnowledgeVectorChunks""
            (chunk_id, source_id, domain_id, skill_id, chunk_index, page_number, content_snippet, document_type, char_count, metadata, created_at)
            VALUES (gen_random_uuid(), @source_id, @domain_id, @skill_id, @chunk_index, @page_number, @content_snippet, @document_type, @char_count, @metadata::jsonb, now());";

        var metaPayload = new
        {
            chunkIndex,
            pageNumber,
            charCount = contentSnippet.Length,
            subjectCode = chunkMetadata?.SubjectCode ?? "MATH",
            subjectName = chunkMetadata?.SubjectName ?? "Toán học",
            grade = chunkMetadata?.Grade ?? 12,
            bookEdition = chunkMetadata?.BookEdition ?? "Chân trời sáng tạo",
            chapterName = chunkMetadata?.ChapterName ?? string.Empty,
            lessonName = chunkMetadata?.LessonName ?? string.Empty,
            patternName = chunkMetadata?.PatternName ?? string.Empty,
            sectionType = chunkMetadata?.SectionType ?? "THEORY",
            hasDiagram = chunkMetadata?.HasDiagram ?? false,
            imagePlaceholders = chunkMetadata?.ImagePlaceholders ?? new List<DiagramPlaceholderDto>(),
            dpiUsed = chunkMetadata?.DpiUsed ?? 56
        };

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("source_id", sourceId);
        cmd.Parameters.AddWithValue("domain_id", (object?)domainId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("skill_id", (object?)skillId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("chunk_index", chunkIndex);
        cmd.Parameters.AddWithValue("page_number", pageNumber);
        cmd.Parameters.AddWithValue("content_snippet", contentSnippet);
        cmd.Parameters.AddWithValue("document_type", (object?)docType ?? "TEXTBOOK");
        cmd.Parameters.AddWithValue("char_count", contentSnippet.Length);
        cmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(metaPayload));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateSourceProgressAsync(
        Guid sourceId,
        int totalPages,
        int totalChars,
        int totalChunks,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            UPDATE v_eval_ai.""KnowledgeSources""
            SET total_pages = @total_pages,
                total_chars = @total_chars,
                total_chunks = @total_chunks,
                vector_status = @status,
                updated_at = now()
            WHERE source_id = @source_id;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("source_id", sourceId);
        cmd.Parameters.AddWithValue("total_pages", totalPages);
        cmd.Parameters.AddWithValue("total_chars", totalChars);
        cmd.Parameters.AddWithValue("total_chunks", totalChunks);
        cmd.Parameters.AddWithValue("status", status);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<TextbookChunkDto>> GetChunksBySourceIdAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var list = new List<TextbookChunkDto>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            SELECT chunk_index, page_number, content_snippet, char_count, metadata::text
            FROM v_eval_ai.""KnowledgeVectorChunks""
            WHERE source_id = @source_id
            ORDER BY page_number ASC, chunk_index ASC;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chunk = new TextbookChunkDto
            {
                ChunkIndex = reader.GetInt32(0),
                PageNumber = reader.GetInt32(1),
                Text = reader.GetString(2),
                CharCount = reader.GetInt32(3)
            };

            if (!reader.IsDBNull(4))
            {
                try
                {
                    string metaJson = reader.GetString(4);
                    using var doc = JsonDocument.Parse(metaJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("subjectCode", out var sc)) chunk.SubjectCode = sc.GetString() ?? "MATH";
                    if (root.TryGetProperty("subjectName", out var sn)) chunk.SubjectName = sn.GetString() ?? "Toán học";
                    if (root.TryGetProperty("grade", out var gr) && gr.TryGetInt32(out var grVal)) chunk.Grade = grVal;
                    if (root.TryGetProperty("bookEdition", out var be)) chunk.BookEdition = be.GetString() ?? "Chân trời sáng tạo";
                    if (root.TryGetProperty("chapterName", out var cn)) chunk.ChapterName = cn.GetString() ?? string.Empty;
                    if (root.TryGetProperty("lessonName", out var ln)) chunk.LessonName = ln.GetString() ?? string.Empty;
                    if (root.TryGetProperty("patternName", out var pn)) chunk.PatternName = pn.GetString() ?? string.Empty;
                    if (root.TryGetProperty("sectionType", out var st)) chunk.SectionType = st.GetString() ?? "THEORY";
                    if (root.TryGetProperty("hasDiagram", out var hd) && (hd.ValueKind == JsonValueKind.True || hd.ValueKind == JsonValueKind.False))
                        chunk.HasDiagram = hd.GetBoolean();
                    if (root.TryGetProperty("dpiUsed", out var dpi) && dpi.TryGetInt32(out var dpiVal)) chunk.DpiUsed = dpiVal;
                    if (root.TryGetProperty("imagePlaceholders", out var ip) && ip.ValueKind == JsonValueKind.Array)
                    {
                        var placeholders = JsonSerializer.Deserialize<List<DiagramPlaceholderDto>>(ip.GetRawText(), new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (placeholders != null) chunk.ImagePlaceholders = placeholders;
                    }
                }
                catch
                {
                    // Ignore metadata parse errors for legacy rows
                }
            }

            list.Add(chunk);
        }

        return list;
    }

    public async Task<int> SaveTextbookAndChunksAsync(TextbookIngestResultDto dto, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            var sourceId = Guid.TryParse(dto.DocumentId, out var parsedGuid) ? parsedGuid : Guid.NewGuid();

            // 1. Insert vào KnowledgeSources
            const string insertSourceSql = @"
                INSERT INTO v_eval_ai.""KnowledgeSources"" 
                (source_id, domain_id, skill_id, title, file_url, file_hash, document_type, total_pages, total_chars, total_chunks, vector_status)
                VALUES (@source_id, @domain_id, @skill_id, @title, @file_url, @file_hash, @document_type, @total_pages, @total_chars, @total_chunks, 'COMPLETED')
                ON CONFLICT (source_id) DO UPDATE SET
                    title = EXCLUDED.title,
                    total_pages = EXCLUDED.total_pages,
                    total_chars = EXCLUDED.total_chars,
                    total_chunks = EXCLUDED.total_chunks,
                    vector_status = 'COMPLETED',
                    updated_at = now();";

            await using (var cmd = new NpgsqlCommand(insertSourceSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("source_id", sourceId);
                cmd.Parameters.AddWithValue("domain_id", (object?)dto.DomainId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("skill_id", (object?)dto.SkillId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("title", dto.FileName);
                cmd.Parameters.AddWithValue("file_url", (object?)dto.PdfUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("file_hash", (object?)dto.FileHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("document_type", (object?)dto.DocumentType ?? "TEXTBOOK");
                cmd.Parameters.AddWithValue("total_pages", dto.TotalPages);
                cmd.Parameters.AddWithValue("total_chars", dto.TotalChars);
                cmd.Parameters.AddWithValue("total_chunks", dto.TotalChunks);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 2. Insert vào KnowledgeVectorChunks
            int insertedChunks = 0;
            const string insertChunkSql = @"
                INSERT INTO v_eval_ai.""KnowledgeVectorChunks""
                (chunk_id, source_id, domain_id, skill_id, chunk_index, page_number, content_snippet, document_type, char_count, metadata)
                VALUES (gen_random_uuid(), @source_id, @domain_id, @skill_id, @chunk_index, @page_number, @content_snippet, @document_type, @char_count, @metadata::jsonb);";

            foreach (var chunk in dto.Chunks)
            {
                var metaPayload = new
                {
                    chunkIndex = chunk.ChunkIndex,
                    pageNumber = chunk.PageNumber,
                    charCount = chunk.CharCount,
                    subjectCode = chunk.SubjectCode,
                    subjectName = chunk.SubjectName,
                    grade = chunk.Grade,
                    bookEdition = chunk.BookEdition,
                    chapterName = chunk.ChapterName,
                    lessonName = chunk.LessonName,
                    patternName = chunk.PatternName,
                    sectionType = chunk.SectionType,
                    hasDiagram = chunk.HasDiagram,
                    imagePlaceholders = chunk.ImagePlaceholders,
                    dpiUsed = chunk.DpiUsed
                };

                await using var cmd = new NpgsqlCommand(insertChunkSql, conn, tx);
                cmd.Parameters.AddWithValue("source_id", sourceId);
                cmd.Parameters.AddWithValue("domain_id", (object?)dto.DomainId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("skill_id", (object?)dto.SkillId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("chunk_index", chunk.ChunkIndex);
                cmd.Parameters.AddWithValue("page_number", chunk.PageNumber);
                cmd.Parameters.AddWithValue("content_snippet", chunk.Text);
                cmd.Parameters.AddWithValue("document_type", (object?)dto.DocumentType ?? "TEXTBOOK");
                cmd.Parameters.AddWithValue("char_count", chunk.CharCount);
                cmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(metaPayload));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                insertedChunks++;
            }

            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation("Lưu thành công {Count} chunks vào v_eval_ai trên Supabase cho file {FileName}", insertedChunks, dto.FileName);
            return insertedChunks;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Lỗi khi lưu tri thức SGK vào Supabase PostgreSQL");
            throw;
        }
    }

    public async Task ResetSourceChunksAsync(Guid sourceId, int totalPages, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            const string deleteChunksSql = @"DELETE FROM v_eval_ai.""KnowledgeVectorChunks"" WHERE source_id = @source_id;";
            await using (var delCmd = new NpgsqlCommand(deleteChunksSql, conn, tx))
            {
                delCmd.Parameters.AddWithValue("source_id", sourceId);
                await delCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string updateSourceSql = @"
                UPDATE v_eval_ai.""KnowledgeSources""
                SET total_pages = @total_pages, total_chars = 0, total_chunks = 0, vector_status = 'PROCESSING', updated_at = now()
                WHERE source_id = @source_id;";
            await using (var updateCmd = new NpgsqlCommand(updateSourceSql, conn, tx))
            {
                updateCmd.Parameters.AddWithValue("total_pages", totalPages);
                updateCmd.Parameters.AddWithValue("source_id", sourceId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation("Đã reset sạch các chunks cũ và đặt lại trạng thái PROCESSING cho source_id={SourceId}", sourceId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Lỗi khi reset chunks cho source_id={SourceId}", sourceId);
            throw;
        }
    }

    public async Task<List<IngestedTextbookSummaryDto>> GetIngestedSourcesWithSummaryAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<IngestedTextbookSummaryDto>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            SELECT source_id, title, file_hash, domain_id, vector_status, total_pages, total_chunks
            FROM v_eval_ai.""KnowledgeSources""
            WHERE total_chunks > 0
            ORDER BY updated_at DESC
            LIMIT 50;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var sources = new List<IngestedTextbookSummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new IngestedTextbookSummaryDto
            {
                SourceId = reader.GetGuid(0).ToString(),
                FileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                FileHash = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                DomainId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Status = reader.IsDBNull(4) ? "COMPLETED" : reader.GetString(4),
                TotalPages = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                TotalChunks = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
            });
        }
        await reader.CloseAsync();

        foreach (var src in sources)
        {
            if (!Guid.TryParse(src.SourceId, out var sid)) continue;
            var chunks = await GetChunksBySourceIdAsync(sid, cancellationToken);
            src.Chunks = chunks;
            if (chunks.Count > 0)
            {
                src.SubjectCode = chunks[0].SubjectCode;
                src.SubjectName = chunks[0].SubjectName;
                src.Grade = chunks[0].Grade;
                src.BookEdition = chunks[0].BookEdition;
            }
            src.TotalDiagrams = chunks.Sum(c => c.ImagePlaceholders?.Count ?? 0);
            src.Chapters = chunks.Select(c => c.ChapterName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            src.Lessons = chunks.Select(c => c.LessonName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            src.Patterns = chunks.Select(c => c.PatternName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            list.Add(src);
        }

        return list;
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            const string deleteChunksSql = @"DELETE FROM v_eval_ai.""KnowledgeVectorChunks"" WHERE source_id = @source_id;";
            await using (var delChunksCmd = new NpgsqlCommand(deleteChunksSql, conn, tx))
            {
                delChunksCmd.Parameters.AddWithValue("source_id", sourceId);
                await delChunksCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deleteSourceSql = @"DELETE FROM v_eval_ai.""KnowledgeSources"" WHERE source_id = @source_id;";
            await using (var delSrcCmd = new NpgsqlCommand(deleteSourceSql, conn, tx))
            {
                delSrcCmd.Parameters.AddWithValue("source_id", sourceId);
                await delSrcCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation("Đã xóa hoàn toàn tài liệu SGK source_id={SourceId} và toàn bộ chunks liên quan", sourceId);
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Lỗi khi xóa tài liệu SGK source_id={SourceId}", sourceId);
            return false;
        }
    }
}
