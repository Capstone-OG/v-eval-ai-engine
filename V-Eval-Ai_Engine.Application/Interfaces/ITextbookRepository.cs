using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

public interface ITextbookRepository
{
    Task<int> SaveTextbookAndChunksAsync(TextbookIngestResultDto dto, CancellationToken cancellationToken = default);
    Task<(Guid SourceId, int LastProcessedPage, int TotalPages, string Status, int TotalChars, int TotalChunks)?> GetCheckpointByFileHashAsync(string fileHash, CancellationToken cancellationToken = default);
    Task<Guid> GetOrCreateSourceAsync(string fileHash, string title, string fileUrl, int totalPages, string domainId, string skillId, string docType, CancellationToken cancellationToken = default);
    Task SavePageChunkAsync(Guid sourceId, int pageNumber, int chunkIndex, string contentSnippet, string domainId, string skillId, string docType, CancellationToken cancellationToken = default);
    Task UpdateSourceProgressAsync(Guid sourceId, int totalPages, int totalChars, int totalChunks, string status, CancellationToken cancellationToken = default);
    Task<List<TextbookChunkDto>> GetChunksBySourceIdAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task ResetSourceChunksAsync(Guid sourceId, int totalPages, CancellationToken cancellationToken = default);
}
