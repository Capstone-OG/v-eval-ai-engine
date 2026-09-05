# V-Eval-Ai_Engine

**V-Eval-Ai_Engine** là phân hệ trí tuệ nhân tạo (AI Engine) phụ trách bóc tách tài liệu quang học độ chính xác cao (High-Precision Verbatim OCR), phân tích đề thi mẫu ĐGNL V-ACT, quản lý cơ sở tri thức định tuyến (RAG) và cung cấp gia sư thông minh Socratic AI Tutor thông qua gRPC Streaming thời gian thực trong hệ sinh thái **V-Eval**.

* **Nhánh theo dõi chính**: `main`
* **Repository Remote**: `https://github.com/Capstone-OG/v-eval-ai-engine.git`

---

## 🏗️ Kiến trúc Công nghệ

* **Framework**: .NET 9 Web API (ASP.NET Core Minimal APIs).
* **Mô hình**: Clean Architecture 4 tầng chuẩn mực (`Domain`, `Application`, `Infrastructure`, `API`).
* **AI Vision & LLM Multimodal**:
  * **Google Gemini API**: `gemini-3.6-flash`, `gemini-3.1-flash-lite`, `gemini-flash-latest`, `gemini-flash-lite-latest`, `gemini-2.5-flash`.
  * **OpenAI API (Dự phòng ưu tiên)**: `gpt-4o`, `gpt-4o-mini`, `o3-mini`, `chatgpt-4o-latest`.
  * Hỗ trợ xoay vòng đa API Key (`GeminiApiKeys`) tự động xử lý khi gặp giới hạn tốc độ HTTP 429 hoặc quá tải 503.
* **Bộ Trích xuất & Kết xuất Hình ảnh Cục bộ (Hybrid PDF Renderer)**:
  * `UglyToad.PdfPig`: Quét nhị phân cấu trúc PDF định vị chính xác Bounding Box trong tích tắc.
  * `PDFtoImage` (Google PDFium Core C++) & `SkiaSharp`: Kết xuất trực tiếp các khối đồ thị/hình ảnh từ trang PDF ra ảnh PNG độ nét cao (150 DPI) với nền trắng tinh khiết, xử lý triệt để các lớp mặt nạ trong suốt `smask` và nhãn chữ Word bên ngoài.
* **Giao tiếp Dịch vụ**: gRPC Server Streaming (`grpc/ai.proto`) cho phép stream từng token thoại AI Tutor về client.
* **Bộ Parser Cứu cánh Cục bộ (Local Fallback Parser)**: `exam_parser.py` (0.3s) dự phòng 100% khi mạng Cloud gián đoạn.

---

## 🚀 Các Tính Năng Cốt Lõi

1. **Bóc Tách Toàn Vẹn 120 Câu Hỏi Đề Thi V-ACT (`POST /api/ai-engine/upload-pdf`)**:
   * Tiếp nhận tệp PDF đề thi 16 trang, chạy tác vụ nền (Background Job) trả về `202 Accepted` tức thì.
   * Cấu hình `maxOutputTokens = 65536` và `temperature = 0.0` (Greedy Deterministic Decoding) đảm bảo trích xuất 100% từ Câu 1 đến Câu 120 không bị cắt cụt và bảo tồn toàn bộ công thức LaTeX.
2. **Xử Lý Toàn Diện Các Dạng Câu Hỏi Đặc Thù (Biểu Đồ, Đồ Thị, Bảng Biểu, Sơ Đồ)**:
   * **Bảng số liệu (Tables)**: Tự động chuyển đổi sang bảng Markdown Table và render HTML `<table>` phong cách Dark-mode, viền phát sáng.
   * **Biểu đồ thống kê (Charts)**: Tích hợp Canvas Chart.js tự động vẽ Biểu đồ cột và Biểu đồ tròn kèm số liệu `${val}%` trực quan trên đỉnh cột/lát bánh.
   * **Đồ thị giải tích & Hình ảnh thực tế (Graphs/Photos)**: Tự động crop từ PDF nguyên bản (như Đồ thị $a-x$ Câu 75, Gương cầu lồi Câu 78).
   * **Chuỗi thí nghiệm đa bước (Multi-step Diagrams)**: Tự động ghép nối các hình vẽ liên hoàn (như Hình A và Hình B chùm 106-108) thành 1 khung hình duy nhất hoàn chỉnh.
3. **Quy Tắc Chống Lộ Đáp Án (Zero-Spoiler Multimodal Prompt)**:
   * Nghiêm cấm AI sử dụng tên thiết bị hoặc từ khóa của các phương án trắc nghiệm trong nội dung mô tả hình ảnh, bảo toàn tính bí mật của đề thi.
4. **Giao Diện Xem Đề Trực Quan (`wwwroot/view-exam.html`)**:
   * Đồng hồ đếm thời gian thực `(mm:ss)`, polling tiến độ bóc tách nền.
   * Trình diễn công thức Toán học qua KaTeX.
   * **Image Lightbox Modal**: Nhấp chuột vào hình ảnh/đồ thị để phóng to toàn màn hình.
   * **Phím tắt Paste nhanh (`Ctrl + V`)**: Dán trực tiếp ảnh chụp từ clipboard vào câu hỏi.
5. **Liên Thông Quản Lý Đề Thi 2 Chiều với Content Service**:
   * Lưu trữ trực tiếp đề thi đã bóc tách vào Supabase PostgreSQL và tải lại đề thi để xem lại bất cứ lúc nào.

---

## 📚 Hệ Thống Tài Liệu Kỹ Thuật (Documentation)

Chi tiết cấu trúc và hướng dẫn kỹ thuật được lưu trữ trong thư mục [`docs/`](./docs):

* 📘 [**Quy trình Bóc tách & Xử lý Hình ảnh PDF** (`docs/ai_data_ingestion.md`)](./docs/ai_data_ingestion.md): Chi tiết cơ chế Vision, PDFium renderer, SkiaSharp page crop và Zero-Spoiler Rule.
* 📅 [**Nhật ký & Kế hoạch Phát triển** (`docs/daily_process_and_planning.md`)](./docs/daily_process_and_planning.md): Báo cáo chi tiết tiến độ theo từng ngày và các milestone phát triển.
* 📝 [**Lịch sử Cập nhật** (`UPDATE.md`)](./UPDATE.md): Bản ghi chi tiết các bản phát hành và cập nhật hệ thống.

---

## ⚙️ Hướng dẫn Khởi chạy Cục bộ (Local Development)

### 1. Yêu cầu Môi trường
* .NET SDK 9.0 trở lên.
* Python 3.10+ (phục vụ bộ bóc tách dự phòng cục bộ khi cần).

### 2. Cấu hình Khóa API
Tạo tệp `V-Eval-Ai_Engine.API/appsettings.Development.json` (đã được cấu hình chặn qua `.gitignore`):

```json
{
  "AiSettings": {
    "GeminiApiKey": "AIzaSy...",
    "GeminiModel": "gemini-3.6-flash",
    "GeminiApiKeys": [
      "AIzaSyKey1...",
      "AIzaSyKey2..."
    ],
    "MaxAttempts": 5
  }
}
```

### 3. Khởi chạy Dịch vụ
```powershell
cd "V-Eval-Ai_Engine.API"
dotnet run
```
Dịch vụ sẽ khởi động tại cổng `http://localhost:5104`.
Truy cập giao diện xem đề trực quan tại: **`http://localhost:5104/view-exam.html`**.
