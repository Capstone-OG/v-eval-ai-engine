# KIẾN TRÚC & BẢNG THEO DÕI TIẾN ĐỘ CHỦ THỂ (PROCESS & PLANNING) - V-EVAL AI ENGINE

---

## PHẦN 1: KIẾN TRÚC DỊCH VỤ & CÁC THÀNH PHẦN CẦN TRIỂN KHAI

### 1. Kiến Trúc AI Engine & RAG Pipeline
- **Cổng Dịch Vụ**: `5104` (HTTP) / Container `v_eval_ai_engine`.
- **Nhiệm Vụ AI**:
  - Trích xuất tự động đề thi từ file PDF bằng mô hình Gemini Vision OCR (`gemini-2.5-flash`).
  - Xử lý và làm sạch công thức Vật lý/Hóa học LaTeX phức tạp, ký hiệu Delta ($\Delta$), và bảng số liệu HTML 2 tầng (`colspan`/`rowspan`).
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
| 4 | **Làm Sạch LaTeX & Delta ($\Delta$)**| `SanitizeJsonForLatex()` | 🟢 Hoàn thành | 100% | Khôi phục `\\frac`, `\\times`, $\Delta$, $H$ formula |
| 5 | **Bảng HTML 2 Tầng (`Colspan`)** | `convertMarkdownTableToHtml` | 🟢 Hoàn thành | 100% | Tái tạo `<thead>` 2 tầng chuẩn mực |
| 6 | **Web Exam Viewer UI** | `wwwroot/view-exam.html` | 🟢 Hoàn thành | 100% | Trang UI hiển thị đề thi kèm KaTeX & nút Lưu |
| 7 | **Liên Thông Content Service** | `view-exam.html` -> Content Service | 🟢 Hoàn thành | 100% | Nhấn "Lưu vào Database" ghi thành công Supabase |
| 8 | **Dockerfile & Compose** | `Dockerfile` | 🟢 Hoàn thành | 100% | Multi-Stage .NET 9 cổng 5104 trên `veval_network` |
| 9 | **Script Push Độc Lập** | `Scripts/push.bat` | 🟢 Hoàn thành | 100% | Hỗ trợ 3 chế độ push kèm kiểm tra lịch sử |
| 10 | **pgvector Multi-Domain RAG** | `rag-service/` | 🟡 Đang làm | 40% | Thiết kế `KnowledgeVectorChunks` phân loại Môn & Skill |
| 11 | **Async AI Exam Generator 30 câu** | `rag-service/routers/` | 🟡 Đang làm | 30% | API sinh đề 30 câu dựa trên RAG Context |
| 12 | **Dynamic Web Search Grounding** | `rag-service/config.py` | 🟡 Đang làm | 20% | Tích hợp Google Search API / Gemini Grounding |
| 13 | **Item-Level Replace API (1-2s)** | `rag-service/routers/chat.py` | 🟡 Đang làm | 20% | Sinh 1 câu thay thế tương đương cùng skill & độ khó |
| 14 | **Diagnostic Engine (IRT 2PL & BKT)** | `rag-service/diagnostic_engine.py`, `routers/diagnostic.py` | 🟢 Hoàn thành | 100% | Ước lượng theta_0 (MAP/Brent), P(L0) Sigmoid, Radar chart, phân lớp |
| 15 | **AI Tutor Personalization** | `Features/Tutor/` | 🟡 Đang chờ | 0% | Lộ trình học tập cá nhân hóa dựa trên kết quả thi |
