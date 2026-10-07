# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [07/10/2026] - Chuẩn Hóa Khung Cấu Hình .NET Ingestion, Python RAG & Khử Lộ Bí Mật Mẫu

- **Chuẩn Hóa File Cấu Hình Mẫu .NET API (`appsettings.example.json`)**:
  - Bổ sung `ConnectionStrings:DefaultConnection` đồng bộ với `appsettings.json`.
  - Loại bỏ các trường cấu hình cũ không còn sử dụng (`QdrantSettings`), bảo toàn cấu hình danh sách mô hình Gemini/OpenAI và timeout xử lý bóc tách tài liệu.
- **Chuẩn Hóa File Cấu Hình Mẫu Python RAG (`rag-service/.env.example`)**:
  - Ẩn toàn bộ mật khẩu kết nối CSDL PostgreSQL trong `DATABASE_URL`, thay bằng placeholder `YOUR_PASSWORD`.
  - Đồng bộ cùng cấu hình chuẩn trong `Configs/V-Eval-Ai_Engine/` của System-Repo.
- **Kiểm Thử Biên Dịch**:
  - `dotnet build` đạt 100% thành công (0 warning, 0 error).
