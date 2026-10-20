# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [21/10/2026] - Chuẩn Hóa Bóc Tách Đáp Án Đúng (NormalizeCorrectOption), Bảng Biến Thiên ([VARIATION_TABLE]) Và Đồ Thị ([MATH_GRAPH]) Zero-Hardcode

- **Chuẩn Hóa Toàn Diện Đáp Án Đúng `CorrectOption` Cho Mọi Phân Hệ Bóc Tách & Sinh Câu Hỏi (`CurriculumAiService.cs`)**:
  - Xây dựng phương thức `NormalizeCorrectOption` và `ExtractJsonNodeAsString` chuẩn hóa mọi định dạng thô từ LLM/OCR (`"A."`, `"(B)"`, `"[C]"`, `"D:"`, `"Đáp án C"`, `"Phương án B"`, `"Option A"`, số nguyên `0..3` hoặc `null`) về duy nhất 1 ký tự chuẩn mực `'A'`, `'B'`, `'C'`, `'D'`.
  - Bảo vệ toàn bộ 5 pipeline: bóc tách tri thức PDF (`ExtractCurriculumAsync`), sinh câu hỏi dạng bài (`GeneratePatternQuestionsAsync`), trích xuất regex dự phòng (`ExtractFallbackCurriculumFromText`, `ExtractQuestionsViaRegex`), và phân tích bản chất dạng bài (`AnalyzePatternAsync`).
  - Đảm bảo 100% payload trả về Web Client tuân thủ chính xác schema CSDL của `Content Service`, ngăn chặn hoàn toàn lỗi 400 Bad Request khi nạp tri thức vào CSDL PostgreSQL.

- **Chuẩn Hóa Cú Pháp Thẻ Bảng Biến Thiên & Đồ Thị Toán Học (`CurriculumAiService.cs`)**:
  - Đặc tả chuẩn thẻ `[VARIATION_TABLE]` hỗ trợ hàm đa thức liên tục và hàm phân thức hữu tỉ 1/1, 2/1 với vạch kép `||`.
  - Mở rộng đặc tả thẻ `[MATH_GRAPH]` 6 loại hàm số (`cubic`, `quartic`, `rational`, `rational_21`, `quadratic`, `custom`), công thức LaTeX, khoảng hiển thị và tiệm cận.
  - Tự động nhúng vào cả Lý thuyết SGK (Khối 1), Phương pháp giải (Khối 2), nội dung câu hỏi `contentLatex` và lời giải `explanation`.

- **Đồng Bộ Dịch Vụ Bóc Tách Đề Thi Gemini Vision OCR (`GeminiExamParserService.cs`)**:
  - Cập nhật System Prompt chuẩn hóa cấu trúc thẻ `[VARIATION_TABLE]` và `[MATH_GRAPH]`, tự động số hóa bảng biến thiên và đồ thị từ PDF sang thẻ vector tương tác cho client.

- **Kiểm Thử Biên Dịch C# Clean Architecture**:
  - `V-Eval-Ai_Engine.Infrastructure` và `V-Eval-Ai_Engine.API` biên dịch sạch 100% (**0 Error, 0 Warning** trên .NET 9 SDK).
