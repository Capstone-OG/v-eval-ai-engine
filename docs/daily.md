# NHẬT KÝ KIỂM TRA TIẾN ĐỘ VẬN HÀNH (DAILY CHECK LOG) - AI ENGINE SERVICE

## [21/09/2026] - Thiết Kế Kiến Trúc AI Exam Generation (30 Câu), Multi-Domain RAG & Quy Trình Duyệt Linh Hoạt
- **Lập Kế Hoạch Kiến Trúc ([ai_question_generation_rag_approval_plan.md](./ai_question_generation_rag_approval_plan.md))**:
  - Thiết kế chi tiết luồng sinh đề thi 30 câu bất đồng bộ, lưu DB trạng thái `PENDING_APPROVAL`.
  - Thiết kế mô hình `KnowledgeVectorChunks` (`pgvector`) phân loại theo Môn học (`domain_id`) và Kỹ năng (`skill_id`).
  - Thiết kế cơ chế Dynamic Web Search (Hybrid Search) kết hợp SGK nội bộ và tin tức thời sự mở rộng.
  - Cấu hình API sinh 1 câu thay thế tương tương trong 1-2 giây và cho phép Manager linh hoạt tinh chỉnh độ khó/bước tính toán (`difficulty_level`).

---

## [18/09/2026] - Phát Hành Công Cụ Push Độc Lập `Scripts/push.bat` & Chuẩn Hóa Bộ Docs
- **Khởi Tạo `Scripts/push.bat`**: Đóng gói công cụ push độc lập hỗ trợ 3 chế độ (nhánh hiện tại, danh sách số nhánh có sẵn, tạo nhánh mới).
- **Chuẩn Hóa Bộ Docs Service**: Đồng bộ hệ thống tài liệu theo 3 file chuẩn `daily.md`, `process.md` và `architecture_acceptance.md`.

---

## [15/09/2026] - Dockerize AI Engine (Milestone 5)
- **Tạo `Dockerfile` Multi-stage**: Build và publish .NET 9 API image trên cổng `5104`.
- **Tích hợp Docker Compose**: Khai báo container `v_eval_ai_engine` tham gia mạng `veval_network`.

---

## [14/09/2026] - Production Security & Config (Milestone 4)
- **Khởi Tạo `appsettings.example.json`**: Tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` (OpenAI ApiKey, Gemini ApiKeys, Supported Models, MaxAttempts) và `QdrantSettings` (Host, Port, ApiKey) với label mẫu.
- **Bảo Mật `.gitignore`**: Cập nhật quy tắc `.gitignore` chặn toàn bộ `appsettings.json` và `appsettings.*.json` cá nhân chứa API Key thật.
- **Định Tuyến Qua Gateway YARP**: Cấu hình Route `/api/ai-engine/{**catch-all}` tại Gateway V-Eval trỏ về cổng `:5104`.

---

## [07/09/2026] - Khắc Phục Lỗi Hiển Thị Công Thức Vật Lý & Hóa Học Complex (Milestone 3.5)
- **Khôi phục Ký tự Thoát JSON cho Mã LaTeX**: Khắc phục hiện tượng JSON Deserializer nuốt các ký tự điều khiển ASCII biến `\frac` thành `\x0c` (form-feed), `\times` thành `\t` (tab), `\text` thành `\t`, `\bar` thành `\b`, `\beta` thành `\b`. Khôi phục về dạng hợp lệ `\\frac`, `\\times`, `\\text`, `\\bar`, `\\beta`.
- **Sửa Lỗi OCR Nhìn Nhầm Ký Hiệu Delta ($\Delta$)**: Xử lý triệt để hiện tượng OCR nhìn nhầm ký hiệu tam giác biến thiên $\Delta$ thành `ar{ ext{A}}`, `\bar{\text{A}}` hoặc `\bar{A}`. Tự động chuẩn hóa về $\Delta[\text{Acetone}]$, $\Delta t$, $R = -\frac{\Delta[\text{Acetone}]}{\Delta t}$.
- **Tự Động Bọc Dấu `$...$` Cho Công Thức Dài & Đơn Vị Số Mũ Âm**: Các công thức Vật lý/Hóa học dài chưa có dấu đô-la ($H = \frac{P_n - P_{hp}}{P_n} \times 100\%$, $P_{hp}$, $P_n$, $k^2$) và đơn vị có số mũ âm ($2,33 \cdot 10^{-3}\text{ mol}\cdot\text{l}^{-1}\cdot\text{phút}^{-1}$) được tự động nhận diện và bọc KaTeX chuẩn typographic.
- **Chống Lỗi Nhân Đôi Chữ Khi Copy KaTeX**: Thêm CSS `.katex-mathml { user-select: none; }` trong `view-exam.html`, triệt tiêu hoàn toàn tình trạng học sinh/giáo viên sao chép công thức bị dính 2 lần chữ (ví dụ: `k 2 k 2` hoặc `P h p P hp`).
- **Bổ Sung Bộ Lọc C# Backend (`GeminiExamParserService.cs`)**: Triển khai hai hàm `SanitizeJsonForLatex(string json)` xử lý chuỗi JSON trước khi deserialize và `CleanExamDto(ParsedExamDto exam)` làm sạch toàn bộ DTO trước khi trả về endpoint.
- **Tự Động Tái Tạo Bảng Tiêu Đề 2 Tầng (`Colspan` / `Rowspan`)**: Nâng cấp hàm `convertMarkdownTableToHtml`: Tự động nhận diện cấu trúc tiêu đề cha - con (như `Số giờ chiếu sáng vào ban đêm (giờ): 0,5` đi cùng các cột `1, 2, 3, 4, 5` trong Chùm câu 109–111) và tự động dựng lại `<thead>` 2 tầng chuẩn mực với `colspan="6"` và `rowspan="2"`.
- **Cập nhật Prompt Hướng Dẫn Bảng 2 Tầng & Tinh Chỉnh CSS**: Hướng dẫn Gemini xuất HTML `<table>` khi gặp bảng phức tạp. Tinh chỉnh CSS `.exam-table`: Kẻ viền ô dạng lưới `1px solid #334155`, căn giữa theo phương dọc (`vertical-align: middle`) và phương ngang, căn trái cột danh mục đầu tiên.

---

## [31/08/2026] - Tối Ưu Hóa Ingestion & Gemini Vision OCR Cloud (Milestone 2)
- **Bảo mật API Key (Leak-proof configuration)**: Cấu hình `.gitignore` chặn `appsettings.Development.json` và dùng các placeholder an toàn trong `appsettings.json`.
- **Tối ưu hóa Ingestion (Raw PDF Base64)**: Thay vì chuyển sang ảnh PNG (CPU lag 3 phút), gửi trực tiếp PDF base64 sang Gemini để xử lý tức thì (<15s) bằng mô hình ổn định `gemini-2.5-flash`.
- **Đồng bộ hóa Progress Logging**: Thêm logs thời gian thực trong Python (`parse_single_pdf.py`) báo cáo kích thước file và tiến trình tải lên.
- **Tích hợp Frontend & Content Service**: Nút "Lưu vào Database" trên UI `view-exam.html` gọi sang Minimal API `POST /api/content/exams/import` của Content Service để lưu dữ liệu.

---

## [30/08/2026] - Khởi Tạo Triển Khai Parse PDF & Endpoints (Milestone 1)
- **Tạo tài liệu quản lý tiến độ (.md)**: Tạo các file tài liệu tiến độ và `ai_data_ingestion.md`.
- **Viết bộ parse tài liệu Python (`document_parser.py`)**: Hỗ trợ đọc PDF (`pypdf`), DOCX (`python-docx`), DOC cũ (COM), Image metadata (`Pillow`).
- **Tạo endpoint API hiển thị (`GET /api/ai-engine/parsed-document`)**: Trả dữ liệu JSON trích xuất từ C# Web API.
- **Tạo API Upload PDF trực tiếp (`POST /api/ai-engine/upload-pdf`)**: Cho phép upload file PDF từ browser/Swagger và parse toàn bộ nội dung.
- **Tạo trang UI hiển thị trực quan (`/api/ai-engine/view-exam`)**: Tách code giao diện ra tệp tĩnh `wwwroot/view-exam.html` riêng biệt, gọi API trích xuất và hiển thị kèm LaTeX.
