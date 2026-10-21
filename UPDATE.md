# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [21/10/2026] - Chuẩn Hóa Bóc Tách Đáp Án Đúng (NormalizeCorrectOption), Bổ Sung Dual-Case Mapping TheoryRecap/SolutionMethod, Bảng Biến Thiên ([VARIATION_TABLE]) Và Đồ Thị ([MATH_GRAPH]) Zero-Hardcode

- **Bổ Sung Dual-Case Mapping & Khắc Phục Triệt Để Null TheoryRecap/SolutionMethod (`CurriculumAiService.cs`)**:
  - Hỗ trợ song song cả camelCase (`theoryRecap`, `solutionMethod`, `videoUrl`) lẫn snake_case (`theory_recap`, `solution_method`, `video_url`) trong bộ bóc tách linh hoạt `TryParseCurriculumFromLooseJson` và `ExtractCurriculumViaRegex`.
  - Khởi tạo mặc định `string.Empty` thay vì `null` trong [`CurriculumAiDtos.cs`](./V-Eval-Ai_Engine.Application/DTOs/CurriculumAiDtos.cs), ngăn chặn hoàn toàn lỗi 400 Bad Request (`The TheoryRecap field is required`) khi client chuyển tiếp payload nạp tri thức sang Content Service.
- **Chuẩn Hóa Toàn Diện Đáp Án Đúng `CorrectOption` Cho Mọi Phân Hệ Bóc Tách & Sinh Câu Hỏi (`CurriculumAiService.cs`)**:
  - Xây dựng phương thức `NormalizeCorrectOption` và `ExtractJsonNodeAsString` chuẩn hóa mọi định dạng thô từ LLM/OCR (`"A."`, `"(B)"`, `"[C]"`, `"D:"`, `"Đáp án C"`, `"Phương án B"`, `"Option A"`, số nguyên `0..3` hoặc `null`) về duy nhất 1 ký tự chuẩn mực `'A'`, `'B'`, `'C'`, `'D'`.
  - Bảo vệ toàn bộ 5 pipeline: bóc tách tri thức PDF (`ExtractCurriculumAsync`), sinh câu hỏi dạng bài (`GeneratePatternQuestionsAsync`), trích xuất regex dự phòng (`ExtractFallbackCurriculumFromText`, `ExtractQuestionsViaRegex`), và phân tích bản chất dạng bài (`AnalyzePatternAsync`).
- **Chuẩn Hóa Cú Pháp Thẻ Bảng Biến Thiên & Đồ Thị Toán Học (`CurriculumAiService.cs`)**:
  - Đặc tả chuẩn thẻ `[VARIATION_TABLE]` hỗ trợ hàm đa thức liên tục và hàm phân thức hữu tỉ 1/1, 2/1 với vạch kép `||`.
  - Mở rộng đặc tả thẻ `[MATH_GRAPH]` 6 loại hàm số (`cubic`, `quartic`, `rational`, `rational_21`, `quadratic`, `custom`), công thức LaTeX, khoảng hiển thị và tiệm cận.
  - Tự động nhúng vào cả Lý thuyết SGK (Khối 1), Phương pháp giải (Khối 2), nội dung câu hỏi `contentLatex` và lời giải `explanation`.
- **Kiểm Thử Biên Dịch C# Clean Architecture**:
  - `V-Eval-Ai_Engine.Application` và `V-Eval-Ai_Engine.Infrastructure` biên dịch sạch 100% (**0 Error, 0 Warning** trên .NET 9 SDK).
