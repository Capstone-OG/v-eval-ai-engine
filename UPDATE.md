# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [28/09/2026] - Tối Ưu Cấu Hình HttpClient Timeout Qua AppSettings & Nghiệm Thu Kiến Trúc Textbook RAG Ingestion

- **Tối Ưu Cấu Hình Timeout HttpClient Qua `appsettings.json` (`DependencyInjection.cs`)**:
  - Loại bỏ hoàn toàn các giá trị hardcode thời gian chờ (`TimeSpan.FromMinutes(8)` và `TimeSpan.FromMinutes(10)`) trong mã nguồn C#.
  - Bổ sung cấu hình linh hoạt `AiSettings:ExamParserTimeoutMinutes = 8` và `AiSettings:TextbookParserTimeoutMinutes = 10` trong `appsettings.json`, `appsettings.example.json`, và `appsettings.Development.json`.
  - Cập nhật `DependencyInjection.cs` đọc timeout trực tiếp từ `IConfiguration` với fallback an toàn, cho phép DevOps tinh chỉnh môi trường mà không cần rebuild code.
- **Nghiệm Thu Kiến Trúc Minimal API Nạp SGK Bởi `ThinhTran2412`**:
  - Nghiệm thu chính thức cấu trúc tệp [`TextbookEndpoints.cs`](./V-Eval-Ai_Engine.API/Endpoints/TextbookEndpoints.cs) theo chuẩn ASP.NET Core Minimal APIs (.NET 9) thay thế hoàn toàn Controller truyền thống.
  - Ban hành tài liệu phân tích kỹ thuật chuyên sâu về pipeline streaming chống tràn RAM khi nạp tệp nặng tới 250 MB tại [`docs/architecture_acceptance.md`](./docs/architecture_acceptance.md).
- **Kiểm Thử Biên Dịch**:
  - `dotnet build` giải pháp `V-Eval-Ai_Engine.sln` thành công 100% (**0 Error**).
