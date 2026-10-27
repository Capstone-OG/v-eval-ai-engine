# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [27/10/2026] - Khởi Tạo Bộ Unit Test Cho Socratic Tutor Session Domain Và Tích Hợp CI Test

- **Khởi Tạo Dự Án Kiểm Thử `V-Eval-Ai_Engine.Tests` (3 Tests)**:
  - Thiết lập dự án kiểm thử theo chuẩn xUnit và .NET 9, tham chiếu đến `V-Eval-Ai_Engine.Domain`.
  - Triển khai bộ 3 unit test cho thực thể Gia sư ảo Socratic [`TutorSessionTests.cs`](./V-Eval-Ai_Engine.Tests/Domain/TutorSessionTests.cs):
    - Kiểm thử khởi tạo session hợp lệ: Khởi tạo đầy đủ SessionId, StudentId, QuestionId, `IsActive == true` và danh sách tin nhắn ban đầu rỗng.
    - Kiểm thử dữ liệu đầu vào rỗng (StudentId hoặc QuestionId null/whitespace) -> Ném `ArgumentException` đúng quy tắc nghiệp vụ.
    - Kiểm thử thêm tin nhắn đối thoại Socratic (`AddMessage`) và đóng phiên gia sư (`EndSession`) chuyển trạng thái `IsActive = false`.
  - Tích hợp project vào `V-Eval-Ai_Engine.sln`.
- **Tích Hợp Tự Động Hóa CI/CD Workflow**:
  - Bổ sung bước thực thi `dotnet test --no-build --configuration Release --verbosity normal` vào `.github/workflows/ci.yml`.
- **Đồng Bộ Thực Thể Miền CSDL & Sơ Đồ ERD**:
  - Đồng bộ ánh xạ 2 Domain Entities [`TheoreticalDocument.cs`](./V-Eval-Ai_Engine.Domain/Entities/TheoreticalDocument.cs) và [`TutorSession.cs`](./V-Eval-Ai_Engine.Domain/Entities/TutorSession.cs) vào CSDL chuẩn [`docs/SQL/SQL.sql`](../../docs/SQL/SQL.sql) (`v_eval_ai."TheoreticalDocuments"` và `v_eval_ai."TutorSessions"`) và sơ đồ thực thể [`docs/diagrams/erd.mmd`](../../docs/diagrams/erd.mmd).

