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

### 💡 Kế hoạch tiếp theo (Ngày mai & Tuần tới)
1. Phát triển cấu trúc trích xuất đề thi (Passage & Questions) tự động để đưa vào database `Content Service` theo đúng định dạng `SQL.sql` đã thiết kế.
2. Tích hợp thư viện sinh hình vẽ đồ thị động hoặc xử lý hình học không gian.
3. Triển khai cấu trúc RAG Pipeline (Qdrant Vector Database) và Service gRPC để phục vụ Socratic AI Tutor.
