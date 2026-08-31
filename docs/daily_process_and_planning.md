# Nhật ký & Kế hoạch Phát triển AI Engine (Daily Process and Planning)

Tài liệu này dùng để cập nhật tiến độ phát triển thực tế hàng ngày của phân hệ AI Engine & AI Tutor trong dự án V-Eval.

---

## 📅 Cập nhật ngày 30/08/2026

### 🎯 Mục tiêu hiện tại (Milestone 1)
Triển khai tính năng **2 chiều**: đọc tệp đề thi dạng PDF ở Backend (Python) và hiển thị dữ liệu trích xuất được ở API (C# .NET) để phục vụ cho các service khác / frontend hiển thị lên.

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Tạo tài liệu quản lý tiến độ (.md)** | 🟢 Hoàn thành | Tạo file này và file `ai_data_ingestion.md`. |
| **Viết bộ parse tài liệu Python (`document_parser.py`)** | 🟢 Hoàn thành | Hỗ trợ đọc PDF (`pypdf`), DOCX (`python-docx`), DOC cũ (COM), Image metadata (`Pillow`). |
| **Viết script chạy thử nghiệm (`test_ingestion.py`)** | 🟢 Hoàn thành | Đọc file PDF mẫu `docs/Nghiệp Vụ ThinhTT.pdf` để kiểm chứng. |
| **Tạo endpoint API hiển thị (`GET /api/ai-engine/parsed-document`)** | 🟢 Hoàn thành | Trả dữ liệu JSON trích xuất từ C# Web API. |
| **Tích hợp Swagger UI (`/swagger/index.html`)** | 🟢 Hoàn thành | Cấu hình Swashbuckle hiển thị tài liệu API trực quan. |
| **Tạo API trích xuất PDF động (`GET /api/ai-engine/parse-pdf`)** | 🟢 Hoàn thành | Nhận vào filePath và gọi Python parse toàn bộ nội dung PDF. |
| **Tạo API Upload PDF trực tiếp (`POST /api/ai-engine/upload-pdf`)** | 🟢 Hoàn thành | Cho phép upload file PDF từ browser/Swagger và parse toàn bộ nội dung. |
| **Tạo trang UI hiển thị trực quan (`/api/ai-engine/view-exam`)** | 🟢 Hoàn thành | Tách code giao diện ra tệp tĩnh `wwwroot/view-exam.html` riêng biệt, gọi API trích xuất và hiển thị kèm LaTeX. |
| **Kiểm tra liên thông 2 chiều** | 🟢 Hoàn thành | Chạy kiểm chứng toàn bộ luồng. |

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
| **Tự động phân loại dạng bài động** | 🟢 Hoàn thành | Ánh xạ trường `suggested_skill_name` để DB tự động sinh dạng bài tương ứng trên schema `content` Supabase. |

### 💡 Kế hoạch tiếp theo
1. Nghiên cứu và thiết lập môi trường Qdrant Vector Database cho RAG Pipeline phục vụ tính năng Socratic AI Tutor.
2. Xây dựng dịch vụ gRPC Stream để truyền dữ liệu tương tác học tập thời gian thực giữa AI Engine và Practice Service.
