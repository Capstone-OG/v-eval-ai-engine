# Nhật ký & Kế hoạch Phát triển AI Engine (Daily Process and Planning)

Tài liệu này dùng để cập nhật tiến độ phát triển thực tế hàng ngày của phân hệ AI Engine & AI Tutor trong dự án V-Eval.

---

## 📅 Cập nhật ngày 15/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 5 - Docker Containerization)
Tạo Dockerfile Multi-stage cho AI Engine (Cổng `:5104`) và tích hợp vào `docker-compose.yml` toàn hệ thống V-Eval.

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Tạo `Dockerfile` Multi-stage** | 🟢 Hoàn thành | Build và publish .NET 9 API image trên cổng `5104`. |
| **Tích hợp Docker Compose** | 🟢 Hoàn thành | Khai báo container `v_eval_ai_engine` tham gia mạng `veval_network`. |

---

## 📅 Cập nhật ngày 14/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 4 - Production Security & Config)
Chuẩn hóa tệp cấu hình mẫu Production (`appsettings.example.json`), cập nhật `.gitignore` ẩn các file cấu hình bí mật local chứa API Key thật, đảm bảo an toàn tuyệt đối khi push git.

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Khởi Tạo `appsettings.example.json`** | 🟢 Hoàn thành | Tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` (OpenAI ApiKey, Gemini ApiKeys, Supported Models, MaxAttempts) và `QdrantSettings` (Host, Port, ApiKey) với label mẫu. |
| **Bảo Mật `.gitignore`** | 🟢 Hoàn thành | Cập nhật quy tắc `.gitignore` chặn toàn bộ `appsettings.json` và `appsettings.*.json` cá nhân chứa API Key thật. |
| **Định Tuyến Qua Gateway YARP** | 🟢 Hoàn thành | Cấu hình Route `/api/ai-engine/{**catch-all}` tại Gateway V-Eval trỏ về cổng `:5104`. |

---

## 📅 Cập nhật ngày 07/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 3.5)
Khắc phục triệt để các lỗi hiển thị công thức Vật lý & Hóa học phức tạp (Câu 93 và bài đọc chùm truyền tải điện năng), tái tạo ký hiệu Delta ($\Delta$), loại bỏ lỗi nuốt ký tự điều khiển ASCII trong JSON deserialize, chống lỗi copy đúp chữ KaTeX và tự động nhận diện tái tạo bảng tiêu đề 2 tầng lồng nhau (`colspan`/`rowspan`).

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Khôi phục Ký tự Thoát JSON cho Mã LaTeX** | 🟢 Hoàn thành | Khắc phục hiện tượng JSON Deserializer nuốt các ký tự điều khiển ASCII biến `\frac` thành `\x0c` (form-feed), `\times` thành `\t` (tab), `\text` thành `\t`, `\bar` thành `\b`, `\beta` thành `\b`. Khôi phục về dạng hợp lệ `\\frac`, `\\times`, `\\text`, `\\bar`, `\\beta`. |
| **Sửa Lỗi OCR Nhìn Nhầm Ký Hiệu Delta ($\Delta$)** | 🟢 Hoàn thành | Xử lý triệt để hiện tượng OCR nhìn nhầm ký hiệu tam giác biến thiên $\Delta$ thành `ar{ ext{A}}`, `\bar{\text{A}}` hoặc `\bar{A}`. Tự động chuẩn hóa về $\Delta[\text{Acetone}]$, $\Delta t$, $R = -\frac{\Delta[\text{Acetone}]}{\Delta t}$. |
| **Tự Động Bọc Dấu `$...$` Cho Công Thức Dài & Đơn Vị Số Mũ Âm** | 🟢 Hoàn thành | Các công thức Vật lý/Hóa học dài chưa có dấu đô-la ($H = \frac{P_n - P_{hp}}{P_n} \times 100\%$, $P_{hp}$, $P_n$, $k^2$) và đơn vị có số mũ âm ($2,33 \cdot 10^{-3}\text{ mol}\cdot\text{l}^{-1}\cdot\text{phút}^{-1}$) được tự động nhận diện và bọc KaTeX chuẩn typographic. |
| **Chống Lỗi Nhân Đôi Chữ Khi Copy KaTeX** | 🟢 Hoàn thành | Thêm CSS `.katex-mathml { user-select: none; }` trong `view-exam.html`, triệt tiêu hoàn toàn tình trạng học sinh/giáo viên sao chép công thức bị dính 2 lần chữ (ví dụ: `k 2 k 2` hoặc `P h p P hp`). |
| **Bổ Sung Bộ Lọc C# Backend (`GeminiExamParserService.cs`)** | 🟢 Hoàn thành | Triển khai hai hàm `SanitizeJsonForLatex(string json)` xử lý chuỗi JSON trước khi deserialize và `CleanExamDto(ParsedExamDto exam)` làm sạch toàn bộ DTO trước khi trả về endpoint. |
| **Tự Động Tái Tạo Bảng Tiêu Đề 2 Tầng (`Colspan` / `Rowspan`)** | 🟢 Hoàn thành | Nâng cấp hàm `convertMarkdownTableToHtml`: Tự động nhận diện cấu trúc tiêu đề cha - con (như `Số giờ chiếu sáng vào ban đêm (giờ): 0,5` đi cùng các cột `1, 2, 3, 4, 5` trong Chùm câu 109–111) và tự động dựng lại `<thead>` 2 tầng chuẩn mực với `colspan="6"` và `rowspan="2"`. |
| **Cập nhật Prompt Hướng Dẫn Bảng 2 Tầng & Tinh Chỉnh CSS** | 🟢 Hoàn thành | Hướng dẫn Gemini xuất HTML `<table>` khi gặp bảng phức tạp. Tinh chỉnh CSS `.exam-table`: Kẻ viền ô dạng lưới `1px solid #334155`, căn giữa theo phương dọc (`vertical-align: middle`) và phương ngang, căn trái cột danh mục đầu tiên. |

---

## 📅 Cập nhật ngày 31/08/2026

### 🎯 Mục tiêu hiện tại (Milestone 2)
Tối ưu hóa luồng trích xuất đề thi PDF với Gemini Vision OCR cloud (bản ổn định), đảm bảo tính an toàn chống lộ API Key khi push git, và liên thông lưu dữ liệu sang Content Service Database.

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Bảo mật API Key (Leak-proof configuration)** | 🟢 Hoàn thành | Cấu hình `.gitignore` chặn `appsettings.Development.json` và dùng các placeholder an toàn trong `appsettings.json`. |
| **Tối ưu hóa Ingestion (Raw PDF Base64)** | 🟢 Hoàn thành | Thay vì chuyển sang ảnh PNG (CPU lag 3 phút), gửi trực tiếp PDF base64 sang Gemini để xử lý tức thì (<15s) bằng mô hình ổn định `gemini-2.5-flash`. |
| **Đồng bộ hóa Progress Logging** | 🟢 Hoàn thành | Thêm logs thời gian thực trong Python (`parse_single_pdf.py`) báo cáo kích thước file và tiến trình tải lên. |
| **Tích hợp Frontend & Content Service** | 🟢 Hoàn thành | Nút "Lưu vào Database" trên UI `view-exam.html` gọi sang Minimal API `POST /api/content/exams/import` của Content Service để lưu dữ liệu. |

---

## 📅 Cập nhật ngày 30/08/2026

### 🎯 Mục tiêu hiện tại (Milestone 1)
Triển khai tính năng **2 chiều**: đọc tệp đề thi dạng PDF ở Backend và hiển thị dữ liệu trích xuất được ở API để phục vụ cho các service khác / frontend hiển thị lên.

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Tạo tài liệu quản lý tiến độ (.md)** | 🟢 Hoàn thành | Tạo file này và file `ai_data_ingestion.md`. |
| **Viết bộ parse tài liệu Python (`document_parser.py`)** | 🟢 Hoàn thành | Hỗ trợ đọc PDF (`pypdf`), DOCX (`python-docx`), DOC cũ (COM), Image metadata (`Pillow`). |
| **Tạo endpoint API hiển thị (`GET /api/ai-engine/parsed-document`)** | 🟢 Hoàn thành | Trả dữ liệu JSON trích xuất từ C# Web API. |
| **Tạo API Upload PDF trực tiếp (`POST /api/ai-engine/upload-pdf`)** | 🟢 Hoàn thành | Cho phép upload file PDF từ browser/Swagger và parse toàn bộ nội dung. |
| **Tạo trang UI hiển thị trực quan (`/api/ai-engine/view-exam`)** | 🟢 Hoàn thành | Tách code giao diện ra tệp tĩnh `wwwroot/view-exam.html` riêng biệt, gọi API trích xuất và hiển thị kèm LaTeX. |
