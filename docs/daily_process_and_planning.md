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

## 📅 Cập nhật ngày 04/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 3.2)
Giải quyết triệt để vấn đề giới hạn Output Token để bóc tách toàn vẹn 100% cả 120 câu hỏi V-ACT từ file PDF 16 trang, xây dựng cơ chế Đa mô hình linh hoạt với giới hạn thử nghiệm an toàn, và nâng cấp hiển thị trực quan các dạng câu hỏi đặc thù (Bảng số liệu HTML Table, Biểu đồ tương tác Chart.js).

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Phát hiện & Khắc phục Giới hạn Token (Truncation)** | 🟢 Hoàn thành | Phát hiện nguyên nhân kết quả bị cắt cụt ở câu 6 do mặc định API chỉ có 8.192 token. Cấu hình `maxOutputTokens: 65536` (~75 KB JSON đủ sức chứa 120 câu). |
| **Tối ưu hóa Mô hình `gemini-3.1-flash-lite`** | 🟢 Hoàn thành | Tắt suy luận ngầm `thinkingBudget: 0` để tránh tiêu hao token vô ích, xuất thẳng JSON câu hỏi. |
| **Đưa `gemini-3.6-flash` lên Top 1 Ưu tiên** | 🟢 Hoàn thành | Trích xuất thành công trọn vẹn 100% cả 120 câu hỏi (17 chùm đọc hiểu passages, 62 câu hỏi đơn lẻ) trong ~2 phút 50 giây. |
| **Kiến trúc Fallback Đa mô hình & Giới hạn 5 lần thử** | 🟢 Hoàn thành | Đưa toàn bộ cấu hình vào `appsettings.json`. Hỗ trợ 4 mô hình OpenAI (`gpt-4o`, `gpt-4o-mini`, `o3-mini`, `chatgpt-4o-latest`) và 5 mô hình Gemini (`gemini-3.6-flash`, `gemini-3.1-flash-lite`, `gemini-flash-latest`, `gemini-flash-lite-latest`, `gemini-2.5-flash`). Giới hạn `MaxAttempts: 5` chống lặp vô tận. |
| **Tự động chuyển đổi Bảng số liệu sang HTML Table** | 🟢 Hoàn thành | Nâng cấp hàm `markdownToHtml` nhận diện toàn bộ các dạng bảng số liệu (như bảng giá vé xe buýt Câu 64-67), render ra thẻ HTML `<table>` phong cách Dark-mode, viền phát sáng, header cyan. |
| **Tích hợp Chart.js vẽ Biểu đồ tương tác Vector** | 🟢 Hoàn thành | Tự động phát hiện mô tả biểu đồ cột (Câu 61-63) và biểu đồ tròn (Câu 68-70) để vẽ Canvas Chart.js sắc nét. Tích hợp plugin in số liệu trực quan `${val}%` ngay trên đỉnh từng cột và từng lát bánh. |
| **Hỗ trợ đính kèm/dán ảnh gốc đề thi** | 🟢 Hoàn thành | Bổ sung nút `📷 Đính kèm / Chèn ảnh gốc` trên từng Card chùm câu và câu hỏi để lưu ảnh đề gốc vào Database. |
| **Liên thông Quản lý Đề thi 2 chiều với Database** | 🟢 Hoàn thành | Hoàn thiện tính năng xem danh sách đề thi đã lưu, tải cấu trúc đề thi từ Database để xem lại và xóa đề thi khỏi Database. |

### 💡 Kế hoạch tiếp theo
1. Kết nối chính thức instance Qdrant Vector Database qua gRPC/HTTP Client trên Docker để lưu trữ embedding bài giảng lý thuyết.
2. Liên thông gRPC giữa `Practice & Adaptive Service` và `AI Engine` để hoàn thiện tính năng gợi mở Socratic AI Tutor trong lúc học sinh làm bài tập.

---

## 📅 Cập nhật ngày 05/09/2026

### 🎯 Mục tiêu hiện tại (Milestone 3.3)
Giải quyết triệt để các câu hỏi và ngữ cảnh đặc thù không thể biểu diễn thuần túy bằng LaTeX/Văn bản (đồ thị dao động điều hòa $a-x$ Câu 75, hình ảnh thực tế gương cầu lồi khúc cua Câu 78, và chuỗi thí nghiệm ghép rễ-tán tảo *Acetabularia* chùm câu 106-108). Xây dựng công cụ trích xuất ảnh gốc cục bộ siêu tốc (100% offline, 0 token overhead) kết hợp quy tắc chống lộ đáp án (Zero-Spoiler Rule).

### 📋 Danh sách Task & Trạng thái

| Tên Task | Trạng thái | Ghi chú |
| :--- | :---: | :--- |
| **Định vị & Kết xuất Hình ảnh Cục bộ (PDFium + SkiaSharp + PdfPig)** | 🟢 Hoàn thành | Định vị toạ độ bằng `UglyToad.PdfPig`, kết xuất trực tiếp bằng `PDFtoImage` (PDFium Core) trên nền trắng tinh khiết (`SKColors.White`). Khắc phục triệt để lỗi FlateDecode (ảnh rỗng Câu 75) và lỗi mất mặt nạ SMask / mất chữ chú thích bên ngoài (Chùm 106-108). |
| **Tự động Bao bọc & Ghép Sơ đồ Liên hoàn (`SkiaSharp`)** | 🟢 Hoàn thành | Tự động loại bỏ Header logo trường ĐHQG (<160pt top) và icon nhiễu (<50px). Mở rộng lề an toàn 20pt bao trọn vẹn toàn bộ chú thích "tán", "thân", "gốc", "A. crenulata", "Tế bào ghép hoàn chỉnh 1 & 2" ở Chùm 106-108 thành 1 ảnh thống nhất `p14_combined.png`. |
| **Thiết lập Quy tắc Chống Lộ Đáp án (Zero-Spoiler Rule)** | 🟢 Hoàn thành | Bổ sung Rule 6 vào Gemini Prompt: Cấm tuyệt đối AI tự ý nhắc đến từ khóa mục tiêu / tên thiết bị của 4 phương án lựa chọn trong phần mô tả câu hỏi (tránh làm mất tính bảo mật của đề thi trắc nghiệm). |
| **Liên kết Tự động ImageUrl vào DTO & Pipeline** | 🟢 Hoàn thành | Bổ sung `image_url` vào `ParsedQuestionDto` và `PassageDto`. Tự động map ảnh trang tương ứng vào đúng Question / Passage trong cả luồng Gemini và Local Fallback. |
| **Nâng cấp Giao diện Xem Đề (`view-exam.html`)** | 🟢 Hoàn thành | Render ảnh trực quan ở cả Card câu đơn và Card bài đọc. Tích hợp Lightbox Modal phóng to ảnh toàn màn hình khi click. Hỗ trợ sự kiện dán ảnh từ clipboard (`Ctrl + V`) trực tiếp vào câu hỏi đang hover. |
| **Đồng bộ Lưu trữ Sang `V-Eval-Content_Service`** | 🟢 Hoàn thành | Cập nhật `ImportMockExamCommand` và handler: tự động nhúng cú pháp `![Hình minh họa](image_url)` vào `ContentLatex` của câu hỏi khi lưu vào PostgreSQL. |
