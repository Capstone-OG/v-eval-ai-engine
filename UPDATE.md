# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [09/11/2026] - Tinh Gọn AI Engine: Tách Template Prompt Độc Lập, Refactor MultiCloudExamParserService Chuẩn Hóa Kiến Trúc & Triển Khai Failover Đa Đám Mây Kèm Ngưỡng An Toàn

- **Tinh Gọn AI Engine & Tách File Prompt Template Độc Lập ([`IPromptTemplateService.cs`](./V-Eval-Ai_Engine.Application/Interfaces/IPromptTemplateService.cs), [`PromptTemplateService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/PromptTemplateService.cs), [`Templates/Prompts/`](./V-Eval-Ai_Engine.Infrastructure/Templates/Prompts/))**:
  - **Triệt tiêu hoàn toàn mùi mã nguồn Hardcoded Prompt trong C#**: Tách toàn bộ các chuỗi System Prompt dài hàng nghìn ký tự (đặc tả thẻ hình học 3D `[MATH_GRAPH_3D]`, bảng biến thiên `[VARIATION_TABLE]`, đồ thị `[MATH_GRAPH]`, chuẩn sư phạm Bloom L1-L4, phân định STEM vs Khoa học xã hội, và OCR đề thi nguyên văn) ra 5 tệp template độc lập trong thư mục `Templates/Prompts/`:
    - `CurriculumIngestion.prompt.txt`: Prompt bóc tách chuyên đề, bài học và dạng bài chuẩn SGK GDPT 2018.
    - `PatternAnalysis.prompt.txt`: Prompt phân tích bản chất dạng bài, 4 bước giải, bẫy đề thi và kỹ thuật Casio fx-580/880.
    - `PracticeQuestionGenerator.prompt.txt`: Prompt sinh câu hỏi rèn luyện thích ứng L3/L4 có chống trùng lặp câu hỏi (`antiDuplicationNote`).
    - `SocraticTutor.prompt.txt`: Prompt gia sư Socrates dẫn dắt gợi mở, cấm lộ đáp án đúng.
    - `ExamIngestionVerbatimOcr.prompt.txt`: Prompt bóc tách và scan chuyển tự quang học nguyên văn 120 câu hỏi đề thi V-ACT.
  - **Dịch vụ Quản lý & Nạp Prompt Mềm Dẻo (`PromptTemplateService`)**:
    - Cơ chế nạp bất đồng bộ `RenderPromptAsync` tự động quét đa đường dẫn (thư mục chạy `AppContext.BaseDirectory` và thư mục dự án gốc).
    - Bộ nhớ đệm RAM hiệu năng cao `ConcurrentDictionary<string, string>` đảm bảo chỉ đọc đĩa 1 lần duy nhất, tối ưu thời gian phản hồi microsecond.
    - Cơ chế thế biến động dạng thẻ Mustache `{{variableName}}` mềm dẻo.
    - Cơ chế phòng thủ chiều sâu (Defense-in-depth Fallback) tự động trả về prompt an toàn nếu tệp template bị xóa ngoài ý muốn.
    - Đăng ký `IPromptTemplateService` dưới dạng Singleton trong `DependencyInjection.cs` và cấu hình MSBuild tự động copy template sang output directory khi build.

- **Refactor Chuẩn Hóa Kiến Trúc: Đổi Tên `GeminiExamParserService` Thành `MultiCloudExamParserService` ([`MultiCloudExamParserService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/MultiCloudExamParserService.cs))**:
  - **Khắc phục mùi mã nguồn Naming Smell**: Lớp bóc tách đề thi thực tế ưu tiên chạy **OpenAI GPT-4o Vision** ở Bước 1 và xoay vòng sang **Google Gemini** ở Bước 2. Đổi tên class & file thành `MultiCloudExamParserService` phản ánh chính xác 100% bản chất nghiệp vụ đa đám mây.
  - Tách 80 dòng chuỗi prompt OCR cũ ra file template `ExamIngestionVerbatimOcr.prompt.txt` và nạp động qua `IPromptTemplateService`.
  - Cập nhật đăng ký DI sạch sẽ trong `DependencyInjection.cs` (`services.AddHttpClient<IExamParserService, MultiCloudExamParserService>`).

- **Cơ Chế Failover Xoay Vòng Đa Đám Mây 2 Tầng (Gemini <-> OpenAI Cascade) & Ngưỡng An Toàn ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`appsettings.json`](./V-Eval-Ai_Engine.API/appsettings.json))**:
  - **Tích hợp bộ kết nối OpenAI Chat Completions**:
    - Triển khai `ResolveOpenAiKey()` và `ResolveOpenAiModels()` nhận diện cấu hình `AiSettings:OpenAiApiKey` / biến môi trường `OPENAI_API_KEY`.
    - Danh sách mô hình GPT dự phòng ưu tiên: `gpt-4o-mini`, `gpt-4o`, `o3-mini`.
    - Bổ sung hàm gọi HTTP bất đồng bộ `TryCallOpenAiCurriculumAsync` với cấu hình `response_format: { "type": "json_object" }` và `temperature: 0.1` (hoặc `0.7` cho sinh câu hỏi).
  - **Kiến trúc Failover 2 tầng (Dual-Cloud Cascade Failover)**:
    - **Tầng 1 (Primary)**: Thử nghiệm tuần tự danh sách Google Gemini (`gemini-3.6-flash`, `gemini-3.5-flash-lite`, `gemini-3.1-flash-lite`, `gemini-flash-lite-latest`) trên toàn bộ API keys khả dụng.
    - **Tầng 2 (Secondary Fallback)**: Tự động chuyển giao sang OpenAI Chat Completions khi toàn bộ mô hình Gemini gặp mã lỗi HTTP 429 (Rate Limit), 503 (Unavailable), lỗi mạng hoặc dữ liệu trả về không vượt qua ngưỡng an toàn.
    - **Tầng 3 (Safety Fallback)**: Cơ chế bóc tách Regex Real-Data Engine cứu cánh khi cả 2 đám mây gặp sự cố định dạng JSON.
  - **Kiểm soát Ngưỡng an toàn (Safety Thresholds)**:
    - Bổ sung kiểm tra ngưỡng `minChaptersThreshold` (`AiSettings:MinChaptersThreshold`, mặc định `>= 1`). Nếu kết quả trả về có số chương `< minChaptersThreshold`, hệ thống từ chối kết quả rỗng và lập tức kích hoạt model fallback kế tiếp.
    - Áp dụng kiểm soát số lần thử tối đa `maxAttempts = 5`.
    - Refactor đồng bộ cho cả 3 phương thức nghiệp vụ: `ExtractCurriculumAsync`, `AnalyzePatternAsync`, và `GeneratePatternQuestionsAsync`.

- **Kiểm Thử & Nghiệm Thu**:
  - Xây dựng bộ kiểm thử mới `PromptTemplateServiceTests.cs` (6/6 unit tests kiểm tra render token, nạp template Socratic, nạp template Pattern, nạp template Exam Ingestion Verbatim OCR, in-memory caching và defense fallback).
  - Toàn bộ 12/12 unit tests trong `V-Eval-Ai_Engine.Tests` vượt qua 100% với thời gian thực thi 0.56s.
  - Toàn bộ solution `V-Eval-Ai_Engine.sln` biên dịch sạch sẽ 100% trên .NET 9.0 (0 Warning, 0 Error).
