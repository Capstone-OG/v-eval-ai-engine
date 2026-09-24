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
                            ?? throw new InvalidOperationException("DefaultConnection string not configured.");
        _logger = logger;
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
                await using var cmd = new NpgsqlCommand(insertChunkSql, conn, tx);
                cmd.Parameters.AddWithValue("source_id", sourceId);
                cmd.Parameters.AddWithValue("domain_id", (object?)dto.DomainId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("skill_id", (object?)dto.SkillId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("chunk_index", chunk.ChunkIndex);
                cmd.Parameters.AddWithValue("page_number", chunk.PageNumber);
                cmd.Parameters.AddWithValue("content_snippet", chunk.Text);
                cmd.Parameters.AddWithValue("document_type", (object?)dto.DocumentType ?? "TEXTBOOK");
                cmd.Parameters.AddWithValue("char_count", chunk.CharCount);
                cmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new { chunk.ChunkIndex, chunk.PageNumber, chunk.CharCount }));
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
}
