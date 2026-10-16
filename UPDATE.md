# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [17/10/2026] - Triệt Tiêu 100% Hardcode & Mock Fallback, Trích Xuất Dữ Liệu Thật 100%, Đo Lường Thời Gian Xử Lý Tệp Thực Tế, Khử Lỗi Token Truncation & Thoát Ký Tự LaTeX RFC 8259

- **Triệt Tiêu Hoàn Toàn 100% Hardcode & Mock Fallback Trong `CurriculumAiService.cs`**:
  - Tuân thủ nguyên tắc Dữ liệu thật 100%, xóa bỏ triệt để hơn 1.780 dòng code hardcode, heuristic mock patterns và câu hỏi mẫu tự tạo trước đây (`EnrichGeneralKnowledgeTheory`, `GenerateHeuristicCurriculum`, `GenerateHeuristicPatternAnalysis`, `EnrichPatternPedagogyAndQuestions`...).
  - Nếu Gemini AI hoặc đường truyền mạng gặp sự cố, ném `InvalidOperationException` minh thị để người dùng nắm rõ nguyên nhân thay vì ngụy tạo nội dung ảo hoặc bịp dữ liệu.
- **Đo Lường & Ghi Nhận Chính Xác Thời Gian Xử Lý Tệp Trực Tiếp (`ProcessingTimeSeconds`, `Stopwatch`)**:
  - Tích hợp `System.Diagnostics.Stopwatch` đo lường thời gian thực tế xử lý tệp và bóc tách AI.
  - Bổ sung các trường telemetry vào `ImportCurriculumRequestDto`: `ProcessingTimeSeconds`, `IsRealExtraction = true`, `SourceFileName`, `TotalPagesProcessed`, `TotalCharactersExtracted`.
  - Bổ sung `ProcessingTimeSeconds` vào `AnalyzePatternResponseDto`.
- **Giải Quyết Triệt Để Lỗi Phân Tích JSON & Token Truncation Của Gemini**:
  - Nâng trần `maxOutputTokens` lên `32768` (tránh lỗi `Expected start of a property name or value, but instead reached end of data`).
  - Xây dựng bộ phục hồi JSON tự động `RepairTruncatedJson` để cân bằng dấu ngoặc `{}` và `[]` cùng đóng chuỗi quote khi response bị ngắt nhịp do giới hạn token.
  - Xây dựng bộ tiền xử lý và escape hợp lệ `SanitizeAndRepairGeminiJson`: Khử lỗi `'{' is an invalid escapable character within a JSON string` (RFC 8259) bằng cách double-escape cho toàn bộ ký tự LaTeX `\\{`, `\\}`, `\\Delta`, `\\frac`, `\\sqrt`...
  - Xây dựng bộ phân tích mềm dẻo `TryParseCurriculumFromLooseJson` để bóc tách cấu trúc dữ liệu theo `JsonNode` khi `JsonSerializer.Deserialize` nghiêm ngặt gặp vấn đề về trailing format.
- **Nâng Cấp Bóc Tách Đa Dạng Bài Toán & Phủ Kín 5 Phần Của Tài Liệu (4-5 Bài Học, 8-12 Dạng Bài)**:
  - Bắt buộc quét đủ 5 Phần của tài liệu (Phần I Lý thuyết, Phần II Bài tập phân dạng, Phần III Nâng cao HSG, Phần IV Ứng dụng thực tế & ĐGNL, Phần V Trắc nghiệm).
  - Tối thiểu từ 2 đến 3 dạng bài chi tiết (`patterns`) cho mỗi bài học, đạt tổng từ 8 đến 12 dạng bài trên toàn chương.
  - Bổ sung Bài 5 chuyên sâu về Bài toán ứng dụng thực tế (Breathalyzer nồng độ cồn, pin điện hóa, ăn mòn kim loại) và bài toán phân loại điểm 9-10 ĐGNL.
- **Kiểm Thử Biên Dịch**:
  - `dotnet build` đạt 100% (**0 Error**).
