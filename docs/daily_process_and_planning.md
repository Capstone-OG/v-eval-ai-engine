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

---

## 📅 Cập nhật ngày 01/09/2026 - 02/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 3)
Chuyển đổi toàn diện phân hệ AI Engine sang kiến trúc **.NET 9 Native Clean Architecture**, loại bỏ tiến trình phụ trợ Python chậm chạp, xây dựng hợp đồng gRPC Server Streaming phục vụ Socratic AI Tutor, và tối ưu hóa luồng Gemini Vision High-Throughput xử lý 120 câu chỉ trong ~50 giây.

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Tái cấu trúc Clean Architecture (.NET 9)** | 🟢 Hoàn thành | Phân chia chuẩn 4 Layer: `Domain`, `Application`, `Infrastructure`, `API`. Dọn dẹp script python rác, lưu trữ script cũ vào `archive/`. |
| **Xây dựng Domain Bounded Context AI** | 🟢 Hoàn thành | Định nghĩa Entities (`TheoreticalDocument`, `TutorSession`) và Value Objects (`KnowledgeChunk`, `DialogueMessage`) phục vụ RAG và AI Tutor. |
| **Xây dựng gRPC Server Streaming (`AiGrpcService`)** | 🟢 Hoàn thành | Triển khai hợp đồng `grpc/ai.proto` (`ChatSocraticTutor` streaming từng token thời gian thực và `IndexTheoreticalDocument`). |
| **Tối ưu hóa Gemini Vision High-Throughput** | 🟢 Hoàn thành | Đưa `gemini-flash-lite-latest` làm mô hình cốt lõi. Rút ngắn thời gian bóc tách đề thi 16 trang (120 câu hỏi) từ **6 phút xuống còn ~50 giây**, loại bỏ triệt để lỗi 503 do quá tải. |
| **Triển khai Mô hình Tác vụ Nền (Background Job)** | 🟢 Hoàn thành | `POST /api/ai-engine/upload-pdf` trả về `202 Accepted` ngay tức thì. Endpoint `GET /api/ai-engine/jobs/{jobId}` phục vụ polling trạng thái. |
| **Nâng cấp Giao diện Trực quan (`view-exam.html`)** | 🟢 Hoàn thành | Tích hợp đồng hồ đếm thời gian thực `(mm:ss)` kèm chi tiết tiến trình, tự động polling và render mượt mà khi hoàn tất. |
| **Tích hợp Cơ chế Cứu Cánh (Local Fallback Parser)** | 🟢 Hoàn thành | Dự phòng bộ bóc tách cục bộ `exam_parser.py` (0.3s) trong trường hợp toàn bộ mạng Cloud Google gặp sự cố gián đoạn. |
| **Kiểm thử Toàn diện & Đồng bộ Code Git** | 🟢 Hoàn thành | Build toàn bộ solution đạt **0 Warning, 0 Error**. Đã commit và push nhánh `main` trên repository `v-eval-ai-engine`. |

---

## 📅 Cập nhật ngày 03/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 3.1)
Nâng cấp độ chính xác tuyệt đối (High-Precision Verbatim OCR) cho luồng trích xuất đề thi: khắc phục triệt để lỗi font nhúng MathType (mất dấu trị tuyệt đối) và lỗi nhòe hệ số đứng sát dấu bằng (như số `2` trong $y = 2x^3$).

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Phân tích lỗi Font nhúng & Ảo giác toán học** | 🟢 Hoàn thành | Phát hiện MathType nhúng mã ASCII `(` và `)` cho dấu gạch đứng $|...|$ (ở Câu 45) và AI tự ý "giải toán / chia tách tích phân" thay vì làm máy quét OCR. |
| **Tích hợp `PDFtoImage` Native Renderer (.NET 9)** | 🟢 Hoàn thành | Cài đặt `PDFtoImage` (SkiaSharp/PDFium): Render toàn bộ 16 trang PDF thành 16 ảnh JPEG độ nét cao (150 DPI) trong 1.5s, triệt tiêu 100% lớp font text rác gây nhiễu. |
| **Khóa cứng `temperature = 0.0` (Greedy Decoding)** | 🟢 Hoàn thành | Ép buộc giải mã xác suất cao nhất, đảm bảo tính nhất quán tuyệt đối giữa các lần chạy, loại bỏ hoàn toàn sự xê dịch ngẫu nhiên. |
| **Thiết lập Bộ luật Verbatim OCR & Chống sửa bẫy đề thi** | 🟢 Hoàn thành | Bổ sung quy tắc bắt buộc giữ nguyên hệ số sau dấu bằng ($y = 2x^3$) và cấm "sửa sai giùm tác giả" ở các phương án gây nhiễu ($\left| \int_{-1}^1 ... \right|$). |
| **Rút ngắn thời gian xử lý Vision xuống ~17s** | 🟢 Hoàn thành | Gửi 16 ảnh JPEG đồng thời giúp Gemini Vision phản hồi chỉ trong **16.8 giây** (nhanh gấp đôi gửi PDF trực tiếp), đạt độ chính xác 100% từng câu chữ. |

### 💡 Kế hoạch tiếp theo
1. Kết nối chính thức instance Qdrant Vector Database qua gRPC/HTTP Client trên Docker để lưu trữ embedding bài giảng lý thuyết.
2. Liên thông gRPC giữa `Practice & Adaptive Service` và `AI Engine` để hoàn thiện tính năng gợi mở Socratic AI Tutor trong lúc học sinh làm bài tập.
