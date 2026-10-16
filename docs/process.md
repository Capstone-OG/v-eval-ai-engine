# KIẾN TRÚC & BẢNG THEO DÕI TIẾN ĐỘ CHỦ THỂ (PROCESS & PLANNING) - V-EVAL AI ENGINE

---

## PHẦN 1: KIẾN TRÚC DỊCH VỤ & CÁC THÀNH PHẦN CẦN TRIỂN KHAI

### 1. Kiến Trúc AI Engine & RAG Pipeline
- **Cổng Dịch Vụ**: `5104` (HTTP) / Container `v_eval_ai_engine`.
- **Nhiệm Vụ AI**:
  - Trích xuất tự động đề thi từ file PDF bằng mô hình Gemini Vision OCR (`gemini-2.5-flash`).
  - Xử lý và làm sạch công thức Vật lý/Hóa học LaTeX phức tạp, ký hiệu Delta (`\Delta`), và bảng số liệu HTML 2 tầng (`colspan`/`rowspan`).
  - Tự động nhận diện ảnh minh họa cắt tĩnh (đồ thị, hình thí nghiệm) và nhúng Markdown Image.
  - Tích hợp Qdrant Vector Database cho RAG Embeddings & Gợi ý Lộ trình Học tập Cá nhân hóa.

### 2. Thành Phần Hạ Tầng AI & API Endpoints
- Python Ingestion Engine (`document_parser.py`, `parse_single_pdf.py`).
- C# Backend Parser (`GeminiExamParserService.cs`, `SanitizeJsonForLatex`, `CleanExamDto`).
- Frontend Viewer (`wwwroot/view-exam.html`).
- Vector DB: Qdrant (`ports 6333 / 6334`).

---

## PHẦN 2: BẢNG THEO DÕI TIẾN ĐỘ CHI TIẾT THEO TỪNG MỤC (PROGRESS MATRIX)

| STT | Hạng Mục / Chức Năng | Vị Trí Triển Khai trong Code | Trạng Thái | Tiến Độ (%) | Ghi Chú Chi Tiết |
| :---: | :--- | :--- | :---: | :---: | :--- |
| 1 | **Python Ingestion Core** | `document_parser.py` | 🟢 Hoàn thành | 100% | Đọc PDF, DOCX, DOC cũ, Image metadata |
| 2 | **API Upload & Parse PDF** | `POST /api/ai-engine/upload-pdf` | 🟢 Hoàn thành | 100% | Upload PDF trực tiếp từ UI/Swagger |
| 3 | **Gemini Vision OCR Cloud** | `GeminiExamParserService.cs` | 🟢 Hoàn thành | 100% | Ingestion trực tiếp Raw PDF Base64 (<15s) |
| 4 | **Làm Sạch LaTeX & Delta (\Delta)**| `SanitizeJsonForLatex()` | 🟢 Hoàn thành | 100% | Khôi phục `\\frac`, `\\times`, `\Delta`, `H` formula |
| 5 | **Bảng HTML 2 Tầng (`Colspan`)** | `convertMarkdownTableToHtml` | 🟢 Hoàn thành | 100% | Tái tạo `<thead>` 2 tầng chuẩn mực |
| 6 | **Web Exam Viewer UI** | `wwwroot/view-exam.html` | 🟢 Hoàn thành | 100% | Trang UI hiển thị đề thi kèm KaTeX & nút Lưu |
| 7 | **Liên Thông Content Service** | `view-exam.html` -> Content Service | 🟢 Hoàn thành | 100% | Nhấn "Lưu vào Database" ghi thành công Supabase |
| 8 | **Dockerfile & Compose** | `Dockerfile` | 🟢 Hoàn thành | 100% | Multi-Stage .NET 9 cổng 5104 trên `veval_network` |
| 9 | **Script Push Độc Lập** | `Scripts/push.bat` | 🟢 Hoàn thành | 100% | Hỗ trợ 3 chế độ push kèm kiểm tra lịch sử |
| 10 | **pgvector Multi-Domain RAG** | `rag-service/` | 🟡 Đang làm | 40% | Thiết kế `KnowledgeVectorChunks` phân loại Môn & Skill |
| 11 | **Async AI Exam Generator (Dual Engine)** | `rag-service/routers/diagnostic.py` | 🟢 Hoàn thành | 100% | `POST /api/v1/diagnostic/generate-exam` (Gemini Cloud + Calibrated RAM), tối ưu luồng import os an toàn |
| 12 | **Dynamic Web Search Grounding** | `rag-service/config.py` | 🟡 Đang làm | 20% | Tích hợp Google Search API / Gemini Grounding |
| 13 | **Item-Level Replace API (1-2s)** | `rag-service/routers/chat.py` | 🟡 Đang làm | 20% | Sinh 1 câu thay thế tương đương cùng skill & độ khó |
| 14 | **Diagnostic Engine (IRT 2PL & BKT)** | `rag-service/diagnostic_engine.py`, `routers/diagnostic.py` | 🟢 Hoàn thành | 100% | Ước lượng theta_0 (MAP/Brent), P(L0) Sigmoid, Radar chart, phân lớp |
| 15 | **SGK Ingestion & PyMuPDF Local Engine** | `view-textbook.html`, `textbook_local_parser.py`, `PdfPigTextbookParserService.cs` | 🟢 Hoàn thành | 100% | PyMuPDF (1s/170p) + RapidOCR (scan offline) + Vision AI fallback |
| 16 | **Resumable Checkpoint & Low-DPI Vision Ingestion** | `TextbookEndpoints.cs`, `PdfPigTextbookParserService.cs`, `TextbookRepository.cs` | 🟢 Hoàn thành | 100% | Nạp ngầm background, lưu checkpoint từng trang vào CSDL, tự động resume khi ngắt quãng, ảnh 96 DPI tiết kiệm token |
| 17 | **AI Tutor Personalization** | `Features/Tutor/` | 🟡 Đang chờ | 0% | Lộ trình học tập cá nhân hóa dựa trên kết quả thi |
| 18 | **Font Mojibake Auto-Detection & Forced Vision AI** | `PdfPigTextbookParserService.cs`, `TextbookEndpoints.cs`, `view-textbook.html` | 🟢 Hoàn thành | 100% | Tự động phát hiện lỗi mã hóa font InDesign/CID subsetting, tự chuyển sang Vision AI, hỗ trợ Force Reingest / Overwrite và API tra cứu Chunks |
| 19 | **Rich Chunks Preview (Markdown + KaTeX + Tables)** | `view-textbook.html` | 🟢 Hoàn thành | 100% | Render bảng Markdown glassmorphic striped, công thức KaTeX STEM inline/block, lọc real-time, chuyển đổi Trực Quan / Raw và xuất file .MD |
| 20 | **AI Exam Studio & Custom Prompting** | `view-diagnostic.html` & `ExamEndpoints.cs` | 🟢 Hoàn thành | 100% | Web studio tạo và duyệt đề, nhận diện môn prompt, chọn Bloom 6 cấp, lưu CSDL chờ duyệt |
| 21 | **Tối Ưu Luồng Bloom 6 Cấp IRT 2PL** | `rag-service/diagnostic_engine.py`, `schemas.py` | 🟢 Hoàn thành | 100% | Ánh xạ difficulty level 1-6 sang tham số IRT 2PL [-1.8 .. +2.2] |
| 22 | **AI Bóc Tách Khung Chương Trình C# Thuần** | `CurriculumEndpoints.cs`, `CurriculumAiService.cs`, `PdfPig` | 🟢 Hoàn thành | 100% | `POST /api/ai-engine/curriculum/extract` bóc tách file PDF/DOCX/Text sang cấu trúc Miền -> Chương -> Bài -> Dạng bài kèm 4 khối KaTeX chuẩn .NET 9 |
| 23 | **Động Cơ Nhận Diện Dạng Bài Sư Phạm C# Thuần** | `CurriculumEndpoints.cs`, `CurriculumAiService.cs`, `CurriculumAiDtos.cs` | 🟢 Hoàn thành | 100% | `POST /api/ai-engine/curriculum/analyze-pattern` phân tích bản chất dạng, cấp độ Bloom, 4 bước giải, bẫy đề thi, kỹ thuật Casio và bài mẫu KaTeX qua C# Clean Architecture |
| 24 | **Bóc Tách Tài Liệu Khổ Lớn 99+ Trang & Page Slicing** | `CurriculumAiService.cs`, `CurriculumEndpoints.cs` | 🟢 Hoàn thành | 100% | Lọc lát trang `startPage` đến `endPage`, mở rộng trần 120.000 ký tự và tùy chọn độ sâu `extraction_depth` chuyên sâu ĐGNL VNU-HCM |
| 25 | **Bóc Tách Đa Bài Tập Mẫu Chuyên Sâu (Bloom L1-L4) & Auto Enrichment** | `CurriculumAiService.cs`, `CurriculumAiDtos.cs` | 🟢 Hoàn thành | 100% | Bắt buộc trích xuất 3-5 câu/dạng, bảo đảm tối thiểu 4 câu hỏi mẫu/dạng bài (L1 Nhận biết, L2 Thông hiểu, L3 Vận dụng, L4 Vận dụng cao), tự động bổ sung câu hỏi bám sát lý thuyết/phương pháp giải, đồng bộ CSDL PostgreSQL |
| 26 | **Tự Động Trích Xuất & Sinh Ví Dụ SGK & Bài Toán Mẫu (Khối 1 & Khối 2)** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Bóc tách ví dụ minh họa SGK (Khối 1) và bài toán mẫu áp dụng phương pháp giải (Khối 2) từ tài liệu; nếu tài liệu thiếu, AI Engine tự động phân tích và sinh mới đầy đủ kèm công thức KaTeX và phân tích bước giải chuẩn xác 100% |
| 27 | **Cây Tri Thức Đa Tầng 16 Dạng Bài & Ngân Hàng 4-5 Câu Vận Dụng/Vận Dụng Cao (224 Câu)** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Mở rộng cấu trúc phân chương chuyên đề lên 16 dạng bài học thuật chi tiết (5 bài học); trang bị 14 câu hỏi mẫu/dạng (đúng 5 câu L3 Vận dụng và 5 câu L4 Vận dụng cao, tổng 224 câu trắc nghiệm); làm giàu kiến thức nền tảng SGK đa tầng với 4 quy tắc và trường hợp ngoại lệ |

