using Grpc.Core;
using VEval.Shared.Grpc.Ai;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.API.GrpcServices;

/// <summary>
/// Triển khai gRPC Service cho AI Engine theo định nghĩa từ ai.proto
/// </summary>
public class AiGrpcService : AiService.AiServiceBase
{
    private readonly ISocraticTutorService _socraticTutorService;
    private readonly IVectorDbService _vectorDbService;
    private readonly ILogger<AiGrpcService> _logger;

    public AiGrpcService(
        ISocraticTutorService socraticTutorService,
        IVectorDbService vectorDbService,
        ILogger<AiGrpcService> logger)
    {
        _socraticTutorService = socraticTutorService;
        _vectorDbService = vectorDbService;
        _logger = logger;
    }

    /// <summary>
    /// Socratic AI Tutor - Stream từng token chữ về client qua gRPC Server Streaming
    /// </summary>
    public override async Task ChatSocraticTutor(
        ChatRequest request, 
        IServerStreamWriter<ChatResponse> responseStream, 
        ServerCallContext context)
    {
        _logger.LogInformation("Nhận yêu cầu gRPC ChatSocraticTutor từ học sinh {StudentId} cho câu hỏi {QuestionId}", 
            request.StudentId, request.QuestionId);

        try
        {
            var tokenStream = _socraticTutorService.StreamSocraticDialogueAsync(
                request.StudentId,
                request.QuestionId,
                request.CurrentDialogue,
                request.ChatHistory,
                context.CancellationToken);

            await foreach (var token in tokenStream.WithCancellation(context.CancellationToken))
            {
                await responseStream.WriteAsync(new ChatResponse
                {
                    Token = token,
                    IsEnd = false
                });
            }

            // Gửi thông điệp kết thúc dòng stream
            await responseStream.WriteAsync(new ChatResponse
            {
                Token = string.Empty,
                IsEnd = true
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client đã hủy kết nối stream gRPC ChatSocraticTutor.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xảy ra trong quá trình stream gRPC ChatSocraticTutor.");
            throw new RpcException(new Status(StatusCode.Internal, $"Lỗi nội bộ AI Tutor: {ex.Message}"));
        }
    }

    /// <summary>
    /// Nạp bài giảng lý thuyết mới vào Vector DB phục vụ RAG
    /// </summary>
    public override async Task<IndexDocumentResponse> IndexTheoreticalDocument(
        IndexDocumentRequest request, 
        ServerCallContext context)
    {
        _logger.LogInformation("Nhận yêu cầu gRPC IndexTheoreticalDocument: '{Title}' (ID: {DocId})", 
            request.Title, request.DocumentId);

        try
        {
            await _vectorDbService.IndexTheoreticalDocumentAsync(
                request.DocumentId,
                request.Title,
                request.Content,
                request.SkillId,
                context.CancellationToken);

            return new IndexDocumentResponse
            {
                Success = true,
                Message = "Tài liệu đã được lập chỉ mục thành công vào Vector DB."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi lập chỉ mục tài liệu vào Vector DB.");
            return new IndexDocumentResponse
            {
                Success = false,
                Message = $"Lỗi lập chỉ mục: {ex.Message}"
            };
        }
    }
}
