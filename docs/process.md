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
| 28 | **Triệt Tiêu 100% Hardcode & Trích Xuất Dữ Liệu Thật Telemetry** | `CurriculumAiService.cs`, `CurriculumAiDtos.cs` | 🟢 Hoàn thành | 100% | Loại bỏ hoàn toàn 1.780 dòng hardcode/mock data; bóc tách 100% dữ liệu thực tế từ Gemini và tài liệu; đo lường chính xác thời gian xử lý tệp trực tiếp (`ProcessingTimeSeconds`, `Stopwatch`); nâng trần `maxOutputTokens` 32768, tự sửa JSON cắt cụt `RepairTruncatedJson` và double-escape LaTeX RFC 8259 `SanitizeAndRepairGeminiJson` |
| 29 | **Domain-Aware Prompting: Chuẩn Hóa Sư Phạm Phân Định STEM vs Ngữ Văn / KHXH** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Bổ sung `IsStemDomain`, tinh chỉnh prompt Gemini phân định rõ môn STEM (Casio fx-580/880) và môn Ngữ văn/KHXH (Chiến lược hành văn, mở đoạn, dẫn chứng, phân bổ thời gian, tuyệt đối cấm nhắc máy tính Casio) |
| 30 | **API Sinh Câu Hỏi Rèn Luyện Thích Ứng Bám Sát Dạng Bài Đang Phân Tích (Zero-Hardcode & Anti-Duplication)** | `CurriculumEndpoints.cs`, `CurriculumAiService.cs`, `CurriculumAiDtos.cs` | 🟢 Hoàn thành | 100% | Triển khai endpoint `POST /api/ai-engine/curriculum/generate-questions`, xử lý `GeneratePatternQuestionsAsync`, prompt Gemini bám sát 100% tác phẩm văn học, tác giả, phương pháp giải và lý thuyết của dạng bài; hỗ trợ `existing_questions` chống trùng lặp câu hỏi khi bấm nhiều lần, nhiệt độ sáng tạo 0.7 |
| 31 | **Cơ Chế Regex Auto-Repair & Fallback Bóc Tách Câu Hỏi Dự Phòng Khắc Phục Lỗi Dị Dạng JSON Gemini** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Bổ sung regex sửa lỗi dấu phẩy sai vị trí sau tên thuộc tính JSON với Lookbehind `(?<=[\{\,]\s*)` tránh ghi đè giá trị; bổ sung cơ chế bóc tách regex dự phòng `ExtractQuestionsViaRegex` với thuật toán Question Block Slicing khi `JsonNode.Parse` thất bại, đảm bảo zero-crash và 100% phản hồi HTTP 200 OK |
| 32 | **Chuẩn Hóa Tiền Tố Bài Mẫu Tránh Lỗi Font Emoji Windows** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Chuyển đổi tiền tố bài toán mẫu từ `### 📝` sang `### 📄` tương thích với mọi trình duyệt và hệ điều hành |
| 33 | **Bảo Toàn Ký Tự Thoát LaTeX Trong Bóc Tách Regex Thuần** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Bảo vệ các lệnh LaTeX bắt đầu bằng `\t` (`\text`, `\times`, `\theta`, `\tau`) không bị nuốt thành ký tự Tab ASCII 0x09 trong `ExtractRegexField`, giải mã hoàn chỉnh `ContentLatex` cho câu hỏi |
| 34 | **Chuẩn Hóa Prompt Bóc Tách & Sinh Mô Hình Hình Học Không Gian 3D và Đồ Thị 2D (MathLab Tags)** | `GeminiExamParserService.cs`, `CurriculumAiService.cs`, `diagnostic.py` | 🟢 Hoàn thành | 100% | Tích hợp chỉ thị bắt buộc trong System Prompt của Gemini: tự động nhận diện bài toán hình học không gian (khối chóp, lăng trụ, hộp, lập phương, khoảng cách, góc, thể tích, thiết diện) và đồ thị hàm số để nhúng trực tiếp khối thẻ cấu trúc `[MATH_GRAPH_3D]` và `[MATH_GRAPH]` vào câu hỏi/lời giải, đồng bộ với bộ render vector JSXGraph trên Web Client |
| 35 | **Tự Động Dựng Hình Học 3D/2D Từ Đề Bài Bằng Chữ (Text-to-Geometry Reconstruction)** | `CurriculumAiService.cs`, `GeminiExamParserService.cs` | 🟢 Hoàn thành | 100% | Tự động suy luận dạng hình, tọa độ đỉnh, điểm phụ, nét đứt và nhúng vào CẢ 2 NƠI (bài giảng và câu hỏi) khi tài liệu chỉ mô tả bằng chữ không có hình ảnh sẵn |
| 36 | **Khắc Phục Lỗi 503 Google Gemini & Kiến Trúc Multi-Model Failover Fallback** | `CurriculumAiService.cs`, `appsettings.json` | 🟢 Hoàn thành | 100% | Nâng cấp ưu tiên `gemini-3.6-flash` (200 OK ~1.5s), tự động chuyển giao multi-model fallback khi gặp HTTP 503/500/429, bảo đảm 100% luồng bóc tách tri thức thông suốt |
| 37 | **Khắc Phục Lỗi 504 Gateway Timeout, Tối Ưu Hóa Prompt Bóc Tách & Nâng Hạn Mức YARP** | `CurriculumAiService.cs`, Gateway `appsettings.json` | 🟢 Hoàn thành | 100% | Tối ưu prompt `depthDirective` sinh kết quả trong 12-18s (~4.000 tokens), nâng ActivityTimeout YARP lên 10 phút, cts lên 5 phút, HttpClient timeout lên 6 phút, chặn đứt chuỗi fallback vô ích khi client hủy request |
| 38 | **Máy Trạng Thái Lookahead Khắc Phục Lỗi Parse JSON '0xC4' / 'N' is invalid after a value** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Nhận diện ngoặc kép không escape giữa văn bản tiếng Việt qua lookahead cú pháp JSON, tự động chuyển thành nháy đơn và cấm ngoặc kép trong prompt sinh câu hỏi / phân tích dạng bài |
| 39 | **Chuẩn Hóa Prompt Bóc Tách & Tự Động Tái Tạo Bảng Biến Thiên ([VARIATION_TABLE]) Và Đồ Thị Hàm Số ([MATH_GRAPH]) Zero-Hardcode Cho Mọi Tài Liệu Toán Học** | `CurriculumAiService.cs`, `GeminiExamParserService.cs` | 🟢 Hoàn thành | 100% | Đặc tả chuẩn thẻ `[VARIATION_TABLE]` (đa thức, phân thức hữu tỉ 1/1 và 2/1 với vạch kép `\|\|`), mở rộng thẻ `[MATH_GRAPH]` 6 loại hàm số kèm tiệm cận và tâm đối xứng, tự động nhúng vào cả bài giảng lẫn câu hỏi không hardcode |
| 40 | **Bổ Sung Dual-Case Mapping Cho TheoryRecap/SolutionMethod & Triệt Tiêu Null Payload Nạp CSDL** | `CurriculumAiService.cs`, `CurriculumAiDtos.cs` | 🟢 Hoàn thành | 100% | Bóc tách song song cả camelCase lẫn snake_case (`theoryRecap` / `theory_recap`, `solutionMethod` / `solution_method`) và khởi tạo mặc định `string.Empty`, bảo đảm triệt tiêu lỗi 400 Bad Request |
| 41 | **Chuẩn Hóa Đáp Án Đúng `NormalizeCorrectOption` Tương Thích Tuyệt Đối Ingestion CSDL** | `CurriculumAiService.cs` | 🟢 Hoàn thành | 100% | Tự động chuẩn hóa đáp án đúng từ Gemini/OCR (`"A."`, `"(B)"`, `"Đáp án C"`, `0..3`) về ký tự đơn `'A'..'D'`, bảo vệ cả 5 luồng bóc tách tri thức và sinh câu hỏi thích ứng |
| 42 | **GraphRAG Phase 1: CSDL & Archetype Seeder** | `rag-service/database/graph_schema.sql`, `seed_archetypes.py` | 🟢 Hoàn thành | 100% | Thiết kế 5 bảng Knowledge Graph trên schema `v_eval_ai` (Supabase pgvector), seed dạng bài mẫu và vector 3072d |
| 43 | **GraphRAG Phase 2: Novelty Detector & Academic API** | `rag-service/graph/graph_db.py`, `graph/novelty_detector.py`, `routers/academic_graph.py` | 🟢 Hoàn thành | 100% | Cosine threshold 0.75, staging idempotent vào `novel_pattern_proposals`, nhận trực tiếp `ParsedExamDto`, API duyệt APPROVED/MERGED/REJECTED, 7/7 unit tests |
| 44 | **GraphRAG Phase 3: Hybrid GraphRAG Retriever** | `rag-service/graph/hybrid_retriever.py`, `routers/academic_graph.py` | 🟢 Hoàn thành | 100% | Truy vết 2-hop (pgvector Cosine -> Graph Traversal), fail-closed grounding (< 0.75 -> LOW_CONFIDENCE), safe trap alignment (câu mẫu vs câu biến thể), endpoint preview, 15/15 unit tests |
| 45 | **GraphRAG Phase 4: Socratic Engine & LLM-as-a-Judge** | `rag-service/socratic/validator_judge.py`, `socratic_engine.py`, `routers/socratic_tutor.py` | 🟢 Hoàn thành | 100% | Kiểm định 2 lớp (LLM-as-a-Judge chống lộ đáp án + Heuristic guard), fallback an toàn CSDL, stream SSE `text/event-stream`, audit log `ai_tutor_interaction_logs`, 22/22 unit tests |
| 46 | **GraphRAG Phase 6: Human-in-the-Loop & Mentor Studio** | `rag-service/routers/socratic_tutor.py`, `wwwroot/view-tutor-mentor.html`, `ExamEndpoints.cs` | 🟢 Hoàn thành | 100% | API báo cáo học sinh (`student_reported`), override exemplar, add novel trap vào Supabase, Web Mentor Studio KaTeX Glassmorphic, 27/27 unit tests |

