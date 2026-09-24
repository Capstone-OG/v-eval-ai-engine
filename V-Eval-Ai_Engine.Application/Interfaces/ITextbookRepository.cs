using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Application.Interfaces;

public interface ITextbookRepository
{
    Task<int> SaveTextbookAndChunksAsync(TextbookIngestResultDto dto, CancellationToken cancellationToken = default);
}
