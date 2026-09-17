# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [18/09/2026] - Phát Hành Công Cụ Push Độc Lập `Scripts/push.bat` Cho AI Engine
- **Tích Hợp `Scripts/push.bat` Độc Lập**:
  - Khởi tạo script [`Scripts/push.bat`](file:///e:/CapStone/All%20Services/V-Eval-Ai_Engine/Scripts/push.bat) độc lập cho AI Engine.
  - Hỗ trợ Push nhanh trên nhánh hiện tại, chọn nhánh đã có qua Menu đánh số, hoặc tạo nhánh mới tự động.
  - Tích hợp tự động kiểm tra đồng bộ lịch sử Git với Remote, tự động pull code khi bi cham (behind) và đưa ra **Cảnh báo Đỏ (Red Warning)** ngắt quy trình khi bị xung đột lịch sử (Conflict/Diverged).

## [15/09/2026] - Dockerize AI Engine & Chuẩn Hóa Docker Compose
- **Dockerfile Multi-Stage .NET 9**:
  - Khởi tạo `Dockerfile` chuẩn cho AI Engine (`V-Eval-Ai_Engine.API`) với cổng `5104`.
  - Kết nối chung mạng nội bộ `veval_network` trong `docker-compose.yml`.

## [14/09/2026] - Chuẩn Hóa Cấu Hình Production & Git Security
- **Tạo File Cấu Hình Mẫu Production (`appsettings.example.json`)**:
  - Khởi tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` và `QdrantSettings`.
- **Cập Nhật `.gitignore` & Bảo Mật Mã Nguồn**:
  - Cập nhật quy tắc `.gitignore` ẩn toàn bộ các file `appsettings.json` cá nhân chứa API key thật.
