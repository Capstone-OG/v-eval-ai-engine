# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [26/09/2026] - Trực Quan Hóa Chunks Tri Thức (Markdown + KaTeX + Tables), Khắc Phục Lỗi Font InDesign/CID & Resumable Ingestion
- **Nâng Cấp Giao Diện Trực Quan Hóa Chunks Tri Thức (`view-textbook.html`)**:
  - **Chuyển Đổi Từ Text Thuần Sang Định Dạng Trực Quan Cao Cấp (Rich Markdown & KaTeX Preview)**:
    - Tích hợp thư viện `marked.js` và `katex` để tự động render toàn bộ văn bản SGK đã cắt: Tiêu đề (`h1` - `h4`), danh sách gạch đầu dòng, khối trích dẫn (`blockquote`), chữ đậm/nghiêng.
    - **Hiển Thị Bảng Biểu Số Liệu Glassmorphic**: Tự động chuyển đổi các bảng Markdown thành bảng HTML hiện đại với hiệu ứng xen kẽ dòng (`striped rows`), bo góc và hover làm nổi bật số liệu.
    - **Render Công Thức Toán/Lý/Hóa Chuẩn KaTeX**: Tự động nhận diện và render sắc nét các công thức inline (`$...$`) và block (`$$...$$`) mà không bị xung đột với parser Markdown.
  - **Thanh Công Cụ Điều Khiển & Tìm Kiếm Chunks Đa Năng**:
    - **Bộ Lọc & Tìm Kiếm Real-time**: Ô input tìm kiếm tức thì theo từ khóa văn bản hoặc số trang (`Trang 5`, `p10`), hiển thị số lượng chunks khớp.
    - **Chế Độ Xem Linh Hoạt (Global & Per-Card View Switcher)**: Cho phép chuyển đổi linh hoạt giữa `👁️ Trực Quan` và `📄 Raw Text` cho toàn bộ danh sách hoặc riêng biệt từng Chunk.
    - **Xuất & Sao Chép Nhanh**: Tích hợp nút `📋 Copy` từng chunk, `📋 Sao Chép Hết` (toàn bộ nội dung cuốn sách) và `💾 Tải File .MD` để lưu toàn bộ sách dưới dạng Markdown chuẩn phục vụ huấn luyện RAG.
- **Tự Động Phát Hiện & Khắc Phục Triệt Để Lỗi Font InDesign / CID Subsetting (Mojibake)**:
  - Triển khai thuật toán kiểm định `IsCorruptedFontEncoding(rawText)` với 3 tầng lọc (mật độ ký tự lạ, tỷ lệ nguyên âm < 23%, cụm phụ âm >= 5), tự động chuyển hướng sang Gemini Vision AI (DPI 96) cho ra văn bản tiếng Việt sạch sẽ 100%.
- **Khắc Phục Lỗi 400 Bad Request & Cập Nhật Danh Mục Mô Hình Gemini Đương Đại**:
  - Gỡ bỏ `thinkingConfig` trên các dòng Lite/Flash, loại bỏ model đã ngừng hỗ trợ trả về 404 (`1.5-flash`, `2.0-flash`, `2.5-flash`), cập nhật danh mục mô hình chuẩn: `gemini-flash-lite-latest`, `gemini-3.5-flash-lite`, `gemini-3.1-flash-lite`, `gemini-3.8-flash`, `gemini-3.6-flash`.
- **Chế Độ Bóc Tách Ép Vision AI & Tùy Chọn Nạp Lại Từ Đầu (Force Reingest / Overwrite)**:
  - Tùy chọn `VISION_AI` trên UI và cờ `forceReingest` dọn sạch các chunk rác cũ trong Supabase (`v_eval_ai."KnowledgeVectorChunks"`).
  - Bổ sung endpoints `GET /chunks/{sourceId}` và `GET /chunks-by-hash/{fileHash}`.
- **Kiến Trúc Checkpointing & Nạp Ngầm Lưu Trực Tiếp CSDL (Per-Page Persistence)**:
  - Lưu checkpoint từng trang vào CSDL Supabase, tự động resume khi ngắt kết nối, tự động kết nối lại tiến trình ngầm qua `GET /api/ai-engine/textbooks/active-job`.
- **Cập Nhật Bộ Tài Liệu Tiến Độ**:
  - Đồng bộ nhật ký vận hành tại [`docs/daily.md`](./docs/daily.md), [`docs/process.md`](./docs/process.md) và [`docs/architecture_acceptance.md`](./docs/architecture_acceptance.md).
