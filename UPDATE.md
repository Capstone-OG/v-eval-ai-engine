# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [14/09/2026] - Chuẩn Hóa Cấu Hình Production & Git Security
- **Tạo File Cấu Hình Mẫu Production (`appsettings.example.json`)**:
  - Khởi tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` (OpenAI ApiKey, Gemini ApiKeys, Supported Models, MaxAttempts) và `QdrantSettings` (Host, Port, ApiKey).
  - Sử dụng các label tham số mẫu (`YOUR_OPENAI_API_KEY`, `YOUR_GEMINI_API_KEY`, `YOUR_QDRANT_API_KEY`) phục vụ triển khai Production.
- **Cập Nhật `.gitignore` & Bảo Mật Mã Nguồn**:
  - Cập nhật quy tắc `.gitignore` ẩn toàn bộ các file `appsettings.json` và `appsettings.*.json` chứa API key thật.
  - Bảo vệ tuyệt đối các chìa khóa API cá nhân trên máy local khỏi bị lỡ tay đẩy lên GitHub.

## [07/09/2026] - Sửa Lỗi Hiển Thị Công Thức Vật Lý & Hóa Học, Tái Tạo Delta (\Delta) & Bảng Số Liệu 2 Tầng
- **Khắc Phục Triệt Để Lỗi Công Thức Vật Lý & Hóa Học (Câu 93 & Đoạn Văn Truyền Tải Điện)**:
  - Khôi phục ký tự thoát JSON cho LaTeX: Sửa lỗi JSON Deserialization nuốt các ký tự điều khiển ASCII.
  - Sửa lỗi nhận diện ký hiệu Delta ($\Delta$): Khắc phục hiện tượng OCR nhìn nhầm ký hiệu tam giác biến thiên $\Delta$.
  - Tự động bọc dấu `$...$` cho công thức KaTeX.
- **Tự Động Nhận Diện & Tái Tạo Bảng Tiêu Đề 2 Tầng (2-Tier Nested Header with Colspan/Rowspan)**.
- **Khôi Phục & Tối Ưu Hóa Chart.js & HTML Table Tương Tác**.
- **Phân Tuyến Thông Minh Bộ Trích Xuất Hình Ảnh (`PdfImageExtractor.cs`)**.
