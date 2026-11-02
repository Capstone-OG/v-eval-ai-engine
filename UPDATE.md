# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [02/11/2026] - Chuẩn Hóa Cấu Trúc Unit Test Nền Tảng (xUnit, Moq, FluentAssertions) & Tích Hợp Script Kiểm Thử

- **Nâng Cấp và Chuẩn Hóa Hạ Tầng Kiểm Thử (.NET 9 SDK)**:
  - Cập nhật file dự án kiểm thử `V-Eval-Ai_Engine.Tests.csproj` đồng bộ chuẩn kiến trúc OJT: `xunit 2.9.2`, `Moq 4.20.72`, `FluentAssertions 7.2.0`, `coverlet.collector 6.0.2` kết hợp `Global Usings` (`Xunit`, `Moq`, `FluentAssertions`).
  - Khởi tạo thư mục và khôi phục công cụ đo lường độ bao phủ `.config/dotnet-tools.json` (`dotnet-reportgenerator-globaltool 5.4.18`).
  - Thiết lập sẵn cấu trúc thư mục khung cho kiểm thử Unit Test: `Commands/`, `Queries/`, `Validators/`, `Common/`.
  - Cập nhật `.gitignore` loại trừ thư mục kết quả test `TestResults/` và file đo lường `*.cobertura.xml`.
- **Triển Khai Script Thực Thi Kiểm Thử Cục Bộ (`Scripts/test.bat`)**:
  - Tạo script chạy test độc lập cho dịch vụ, tự động nhận diện SDK .NET 9 trên Host (`%USERPROFILE%\.dotnet\dotnet.exe`), chạy `dotnet test` kèm thu thập Cobertura coverage và sinh báo cáo HTML chi tiết.
