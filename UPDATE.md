# UPDATE - V-Eval-Ai_Engine

## [17/11/2026]
- **Nâng cấp toàn diện cơ chế Vision AI xử lý SGK Scan thuần ảnh & Đồng bộ Checkpoint/Job Manager**:
  - **Quét Mục Lục Bước 1 (10 trang đầu & 6 trang cuối)**:
    - Nâng cấp `renderPages`: Luôn render ảnh 10 trang đầu (`Math.Min(10, totalPages)`) và 6 trang cuối (`Math.Max(1, totalPages - 5)` đến `totalPages`) bằng SkiaSharp (DPI 72, JPEG 80%) gửi trực tiếp đến Gemini Vision AI. Đảm bảo bắt trọn trang Mục lục của tất cả các bộ SGK Cánh Diều, Kết Nối Tri Thức, Chân Trời Sáng Tạo.
    - Cải tiến hàm phân giải model `ResolveGeminiModels`: Bổ sung toàn diện các model chính thức chuẩn của Google AI Studio (`gemini-2.5-flash`, `gemini-2.0-flash`, `gemini-1.5-pro`, `gemini-1.5-flash`, `gemini-flash-lite-latest`) và các model cấu hình (`gemini-3.5-flash`, `gemini-3.1-flash-lite`).
    - Cơ chế fallback thông minh: Khi AI chỉ trích xuất được <= 2 chương nhưng sách có >= 25 trang, tự động hợp nhất các Chủ đề chuẩn GDPT 2018 (Vật lí 10 có 5 Chủ đề: Mô tả chuyển động, Lực và Chuyển động, Năng lượng, Động lượng, Chuyển động tròn).
  - **Quét Sâu Toàn Bộ SGK Bước 2 (Độ phủ 70%–80%) & Đồng Bộ Checkpoint**:
    - Tự động kích hoạt chế độ `VISION_AI` khi phát hiện PDF scan thuần ảnh (`digitalPages.Count < totalPages * 0.5`), DPI 72 và gộp batch 4 trang/lượt.
    - Cập nhật `InMemoryTextbookJobManager.cs`: Thiết lập `entry.Dto.SourceId = result.DocumentId` khi job hoàn tất để Frontend lập tức nhận được `sourceId` và tự động fetch đầy đủ chunks.
    - Cập nhật `TextbookEndpoints.cs`: Gán `job.SourceId` từ Checkpoint Supabase khi người dùng nạp lại file đã có trong CSDL.
  - **Kiến trúc & Runtime**:
    - Sửa `AppContext.BaseDirectory` và `ContentRootPath` trong `Program.cs` để nhận diện chính xác `appsettings.json`.
    - Thêm fallback an toàn chuỗi kết nối Supabase trong `TextbookRepository.cs`.
- **Kiểm thử & Khởi chạy**:
  - `dotnet build` đạt 0 Warnings, 0 Errors.
  - Dịch vụ `V-Eval-Ai_Engine.API` đang chạy ổn định ở background trên cổng 5104.
