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
