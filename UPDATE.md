# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [15/09/2026] - Dockerize AI Engine & Chuẩn Hóa Docker Compose
- **Dockerfile Multi-Stage .NET 9**:
  - Khởi tạo `Dockerfile` chuẩn cho AI Engine (`V-Eval-Ai_Engine.API`) với cổng `5104`.
  - Kết nối chung mạng nội bộ `veval_network` trong `docker-compose.yml`.

## [14/09/2026] - Chuẩn Hóa Cấu Hình Production & Git Security
- **Tạo File Cấu Hình Mẫu Production (`appsettings.example.json`)**:
  - Khởi tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` và `QdrantSettings`.
- **Cập Nhật `.gitignore` & Bảo Mật Mã Nguồn**:
  - Cập nhật quy tắc `.gitignore` ẩn toàn bộ các file `appsettings.json` cá nhân chứa API key thật.
