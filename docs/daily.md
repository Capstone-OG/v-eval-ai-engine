# NHẬT KÝ KIỂM TRA TIẾN ĐỘ VẬN HÀNH (DAILY CHECK LOG) - AI ENGINE SERVICE

## [27/09/2026] - Chuẩn Hóa IRT 2PL Theo Thang Đo Bloom 6 Mức Độ, AI Exam Studio & Kiến Trúc Chế Độ Kép (Dual Engine: Gemini Cloud vs Siêu Tốc Calibrated)
- **Chuẩn Hóa Ánh Xạ Độ Khó IRT 2PL Theo 6 Mức Độ Tư Duy Bloom (`diagnostic_engine.py`)**:
  - Nâng cấp từ 4 cấp độ cũ lên đầy đủ 6 mức độ tư duy chuẩn Bloom cải tiến (Revised Bloom's Taxonomy):
    - Mức 1: Nhận biết (Remembering) $\to b = -1.8$
    - Mức 2: Thông hiểu (Understanding) $\to b = -1.0$
    - Mức 3: Vận dụng (Applying) $\to b = -0.2$
    - Mức 4: Phân tích (Analyzing) $\to b = +0.6$
    - Mức 5: Đánh giá (Evaluating) $\to b = +1.4$
    - Mức 6: Sáng tạo (Creating) $\to b = +2.2$
  - Cập nhật schema Pydantic `DiagnosticAnswerItem` (`rag-service/schemas.py`) mở rộng `difficulty_level` từ `[1..4]` lên `[1..6]`.
- **Triển Khai AI Exam Studio & Kiến Trúc Chế Độ Kép (Dual Engine Architecture)**:
  - Bổ sung endpoint `POST /api/v1/diagnostic/generate-exam` (FastAPI port 8000) với 2 chế độ sinh đề:
    1. **🧠 Google Gemini Live Cloud (`gemini-flash-lite-latest`)**: Gọi trực tiếp Google Gemini API sử dụng cấu trúc `responseSchema` nghiêm ngặt, cho phép AI suy nghĩ và sáng tác 100% câu hỏi mới toanh bám sát prompt, định hướng môn học và phân bổ Bloom (~3-6s).
    2. **⚡ Siêu Tốc Calibrated Bank (< 0.1s)**: Tổ hợp câu hỏi chuẩn hóa tâm lý học từ bộ nhớ RAM với phân bổ đáp án đều (A/B/C/D 25%), phục vụ tức thì cho nhu cầu kiểm thử nhanh và demo luồng mà không phụ thuộc độ trễ mạng.
  - Tự động cơ chế phục hồi (Graceful Fallback): Khi Gemini Cloud chạm trần quota hoặc timeout, hệ thống tự động fallback sang kho Calibrated Bank và cảnh báo rõ ràng trên UI.
  - **Cơ Chế Nhận Diện Ý Định & Khắc Phục Xung Đột Lĩnh Vực (Strict Domain Conflict Resolution)**:
    - Bổ sung cơ chế phát hiện từ khóa chuyên sâu (cả có dấu và không dấu: `ngữ văn`, `văn học`, `tiếng việt`, `đọc hiểu`, `toán học`, `logic`...) trong câu prompt của giáo viên.
    - Tự động đồng bộ và cưỡng chế `domain_id` chuẩn (ví dụ: `Full Ngữ Văn` -> `dom_lang`), vô hiệu hóa tình trạng rơi vào cấu hình mặc định 5 lĩnh vực (`ALL`).
    - Nâng cấp System Prompt cho Gemini với quy chuẩn sư phạm nghiêm ngặt: Tuyệt đối 100% câu hỏi thuộc đúng môn học được yêu cầu (xóa bỏ hoàn toàn tình trạng đề Văn bị lọt câu hỏi Toán/KHTN); loại bỏ triệt để các lời giải tự nhận sai hoặc giả định sửa đề.
    - **Ưu Tiên Tuyệt Đối Lựa Chọn Dropdown Của Giáo Viên (Dropdown Precedence Fix)**: Chỉ kích hoạt suy luận domain từ prompt khi `domain_id == 'ALL'`. Nếu giáo viên chủ động chọn môn trong dropdown (như Khoa học Xã hội / Địa lý `dom_soc_sci`), hệ thống giữ nguyên 100%, chấm dứt hoàn toàn lỗi tự động nhảy dropdown sang Ngữ văn khi prompt có từ khóa chung như 'đọc hiểu'.
- **Cung Cấp Giao Diện Web Runner Khảo Sát Năng Lực (`/api/ai-engine/view-diagnostic`)**:
  - Bổ sung endpoint `GET /api/ai-engine/view-diagnostic` trong `ExamEndpoints.cs`.
  - Triển khai tệp giao diện `view-diagnostic.html` (`wwwroot/view-diagnostic.html`) phục vụ trực quan hóa toàn diện bài thi chẩn đoán 30 câu, tính toán Psychometrics và biểu đồ Radar Chart.
- **Kiểm Thử Vận Hành & Biên Dịch**: `dotnet build` giải pháp `V-Eval-Ai_Engine.sln` thành công 100% (**0 Error**); Kiểm thử HTTP cả 2 chế độ Gemini Cloud (`ms: 7245`) và Fast Calibrated (`ms: 0`) trả về 200 OK; Kiểm thử với prompt `Full Ngữ Văn` sinh chính xác 100% câu hỏi Ngôn ngữ & Văn học; Kiểm thử với `dom_soc_sci` sinh chính xác 100% câu hỏi Sử - Địa.

---

## [26/09/2026] - Trực Quan Hóa Chunks Tri Thức (Markdown + KaTeX + Tables), Khắc Phục Lỗi Font InDesign/CID & Resumable Ingestion
- **Nâng Cấp Giao Diện Trực Quan Hóa Chunks Tri Thức (`view-textbook.html`)**:
  - **Chuyển Đổi Từ Text Thuần Sang Định Dạng Trực Quan Cao Cấp (Rich Markdown & KaTeX Preview)**:
    - Tích hợp thư viện `marked.js` và `katex` để tự động render toàn bộ văn bản SGK đã cắt: Tiêu đề (`h1` - `h4`), danh sách gạch đầu dòng, khối trích dẫn (`blockquote`), chữ đậm/nghiêng.
    - **Hiển Thị Bảng Biểu Số Liệu Glassmorphic**: Tự động chuyển đổi các bảng Markdown thành bảng HTML hiện đại với hiệu ứng xen kẽ dòng (`striped rows`), bo góc và hover làm nổi bật số liệu.
    - **Render Công Thức Toán/Lý/Hóa Chuẩn KaTeX**: Tự động nhận diện và render sắc nét các công thức inline (``$...$``) và block (``$$...$$``) mà không bị xung đột với parser Markdown.
  - **Thanh Công Cụ Điều Khiển & Tìm Kiếm Chunks Đa Năng**:
    - **Bộ Lọc & Tìm Kiếm Real-time**: Ô input tìm kiếm tức thì theo từ khóa văn bản hoặc số trang (`Trang 5`, `p10`), hiển thị số lượng chunks khớp.
    - **Chế Độ Xem Linh Hoạt (Global & Per-Card View Switcher)**: Cho phép chuyển đổi linh hoạt giữa `👁️ Trực Quan` và `📄 Raw Text` cho toàn bộ danh sách hoặc riêng biệt từng Chunk.
    - **Xuất & Sao Chép Nhanh**: Tích hợp nút `📋 Copy` từng chunk, `📋 Sao Chép Hết` (toàn bộ nội dung cuốn sách) và `💾 Tải File .MD` để lưu toàn bộ sách dưới dạng Markdown chuẩn phục vụ huấn luyện RAG.
- **Tự Động Phát Hiện & Khắc Phục Triệt Để Lỗi Font InDesign / CID Subsetting (Mojibake)**:
  - Phân tích nguyên nhân: Các file SGK (như SGK Lớp 10 Địa Lí, Lịch Sử, Văn...) chứa lớp text số bị lỗi encoding do phần mềm InDesign không nhúng ToUnicode CMap, khiến PdfPig bóc tách ra các chuỗi ký tự rác vô nghĩa (VD: `&IFHPkFVLQK WKKQPQCdo...`, `PNtFKWKtFKWt...`).
  - Triển khai thuật toán kiểm định `IsCorruptedFontEncoding(rawText)` với 3 tầng lọc:
    1. Kiểm tra mật độ ký tự rác / glyph lạ (`{`, `}`, `\`, `^`, `~`, `|`, `¶`, `§`, `©`, `®`, `½`, `¼`, `¾`, `¿`, `±`, `ł`, `Ċ`, `ī`, `Ť`, `Š`, `ś`, v.v.).
    2. Kiểm tra tỷ lệ nguyên âm tiếng Việt/Anh (`< 23%` là dấu hiệu phân mảnh phụ âm do lỗi CMap).
    3. Kiểm tra cụm phụ âm liên tiếp dài (`>= 5` phụ âm) và các từ không chứa nguyên âm.
  - Khi phát hiện lớp chữ số bị lỗi, hệ thống tự động bỏ qua text rác và chuyển hướng bóc tách ảnh bằng Gemini Vision AI (DPI 96) cho ra văn bản tiếng Việt sạch sẽ 100%.
- **Chế Độ Bóc Tách Ép Vision AI & Tùy Chọn Nạp Lại Từ Đầu (Force Reingest / Overwrite)**:
  - Bổ sung tùy chọn `VISION_AI` trên UI và API: Bóc tách 100% bằng Vision AI, bỏ qua hoàn toàn lớp text số nếu nghi ngờ file PDF lỗi font.
  - Bổ sung cờ `forceReingest` và checkbox trên UI: Tự động xóa sạch các chunk cũ bị lỗi font (`DELETE FROM v_eval_ai."KnowledgeVectorChunks" WHERE source_id = @source_id`) và bóc tách lại từ trang 1.
- **Khắc Phục Lỗi 400 Bad Request & Cập Nhật Danh Mục Mô Hình Gemini Đương Đại**:
  - Phát hiện và loại bỏ trường `thinkingConfig: { thinkingBudget: 0 }` trong payload gửi tới `gemini-flash-lite-latest` (nguyên nhân gây lỗi 400 `INVALID_ARGUMENT` do Google API không hỗ trợ trường thinking trên dòng Lite).
  - Loại bỏ các mô hình đã ngừng hỗ trợ trả về mã 404 (`gemini-2.0-flash`, `gemini-1.5-flash`, `gemini-2.5-flash`).
  - Cập nhật danh mục mô hình tối ưu theo khuyến nghị chính thức của Google: `gemini-flash-lite-latest`, `gemini-3.5-flash-lite`, `gemini-3.1-flash-lite`, `gemini-3.8-flash`, `gemini-3.6-flash`.
  - Bổ sung ghi log chi tiết mã HTTP và nội dung lỗi khi gọi API để dễ dàng giám sát vận hành.
- **Tối Ưu Hóa Đầu Vào & Điểm Ảnh Vision AI (Low-DPI Optimization)**:
  - Giảm thiểu token và chi phí Free Tier bằng cách kết xuất trang PDF scan ở độ phân giải vừa vặn `Dpi = 96` (kích thước ~800x1100 px, dung lượng nén JPEG chất lượng 70 chỉ < 80 KB).
  - Tối đa hóa tốc độ xử lý trên Google Gemini Flash (`gemini-flash-lite-latest` / `gemini-1.5-flash`), bóc tách mỗi trang trong ~2.5s mà vẫn giữ nguyên 100% công thức LaTeX (``$x^2 + y^2$``) và bảng biểu Markdown.
- **Kiến Trúc Checkpointing & Nạp Ngầm Lưu Trực Tiếp CSDL (Per-Page Persistence)**:
  - Tạm lưu tệp PDF lên máy chủ (`uploads/textbooks/`) và tính mã băm SHA-256 định danh tệp duy nhất.
  - Tác vụ nạp chạy hoàn toàn ngầm (`Task.Run`), độc lập với vòng đời HTTP Request, trả về `202 Accepted` ngay lập tức.
  - Mỗi trang xử lý xong được lưu ngay thành Chunk vào bảng `v_eval_ai."KnowledgeVectorChunks"` và cập nhật tiến trình vào `v_eval_ai."KnowledgeSources"`.
  - **Khả Năng Khôi Phục (Resume)**: Khi có sự cố ngắt kết nối, tắt trình duyệt hoặc tải lại tệp, hệ thống kiểm tra `MAX(page_number)` trong CSDL và **tự động tiếp tục nạp từ trang tiếp theo (Page X+1)**, không bao giờ phải nạp lại từ đầu.
- **Khôi Phục Trạng Thái Giao Diện Studio (`view-textbook.html`)**:
  - Tích hợp endpoint `GET /api/ai-engine/textbooks/active-job`: Khi người dùng tải lại trang (F5) hoặc quay lại sau nhiều giờ, giao diện tự động kết nối lại tiến trình ngầm đang chạy và tiếp tục cập nhật thanh tiến độ % và live console logs.
- **Bộ Công Cụ Ping & Đo Độ Trễ Vision AI**:
  - Cung cấp endpoint `GET /api/ai-engine/textbooks/ping-vision`, banner kiểm tra trực quan trên Web và script PowerShell [`Scripts/run_local/ping_vision.ps1`](./../../Scripts/run_local/ping_vision.ps1).

---

## [25/09/2026] - Triển Khai Phân Loại OCR Mode, RapidOCR Offline Speed-Up & API Lưu Tri Thức Database (`v_eval_ai`)
- **Giao Diện Trang Web Nạp Tri Thức SGK (`wwwroot/view-textbook.html`)**:
  - Xây dựng giao diện Web UI hiện đại với Glassmorphic design, thanh tab chuyển đổi mượt mà giữa Ngân Hàng Đề Thi (`/api/ai-engine/view-exam`) và Nạp Tri Thức SGK (`/api/ai-engine/view-textbook`).
  - Tích hợp menu tùy chọn **Chế độ xử lý chữ (OCR Mode)**:
    - 📖 **Văn Bản Thuần - Ngữ Văn, Anh Văn, KHXH** *(Gọi Python PyMuPDF + RapidOCR Local Engine)*.
    - 📄 **Tự Động / Text Local** *(Đọc siêu tốc local offline bằng `PdfPig` cho PDF có lớp chữ sẵn)*.
    - 🧪 **Tự Nhiên & Công Thức - Toán, Lý, Hóa** *(Giữ nguyên định dạng LaTeX)*.
  - Tích hợp nút bấm **💾 Lưu Tri Thức Vào Database (`v_eval_ai`)** gửi payload Vector Chunks về CSDL.
  - Bảng điều khiển tiến độ ngầm (Background Job Dashboard) hiển thị phần trăm tiến trình %, live console log real-time và preview các Chunks tri thức.
- **Tối Ưu Hóa & Đồng Bộ Hóa Tuần Tự RapidOCR (`textbook_local_parser.py`)**:
  - Khôi phục luồng xử lý tuần tự (Sequential Order) 100% chuẩn xác theo thứ tự trang từ `1..N`.
  - Sử dụng ánh xạ bộ nhớ trực tiếp (Direct Numpy Buffer) từ `pix.samples`, loại bỏ hoàn toàn chi phí nén/giải nén PNG/JPEG trung gian.
  - Phân tích số trang `[Trang X/Y]` để đồng bộ thanh tiến độ phần trăm `%` tăng dần mượt mà từ 15% đến 80% trên UI.
- **Khởi Tạo Schema `v_eval_ai` Trên Supabase & Tích Hợp Npgsql Repository**:
  - Đã khởi tạo thành công Schema `v_eval_ai` và extension `vector` (pgvector) trên CSDL Supabase Cloud PostgreSQL.
  - Ban hành kịch bản DDL chuẩn tại [`docs/SQL/V_EVAL_AI_SCHEMA.sql`](./docs/SQL/V_EVAL_AI_SCHEMA.sql) bao gồm 2 bảng:
    - `v_eval_ai."KnowledgeSources"`: Quản lý metadata tài liệu SGK, mã Hash SHA-256, số trang, số chunks.
    - `v_eval_ai."KnowledgeVectorChunks"`: Lưu trữ các đoạn phân đoạn tri thức (chunks) kèm trường `embedding vector(768)` và chỉ mục HNSW (`idx_knowledge_vector_hnsw`).
  - Triển khai `TextbookRepository` (`ITextbookRepository`) sử dụng Npgsql kết nối Supabase, ghi trực tiếp các Chunks tri thức vào CSDL khi bấm nút trên UI.
- **REST Endpoints & Database Persistence (`TextbookEndpoints.cs`)**:
  - `POST /api/ai-engine/textbooks/upload-pdf`: Tiếp nhận PDF + `ocrMode`, khởi chạy Background Job async và trả về `202 Accepted` kèm JobId.
  - `GET /api/ai-engine/textbooks/jobs/{jobId}`: Endpoint Polling tiến độ ngầm real-time.
  - `POST /api/ai-engine/textbooks/save-db`: Gọi `ITextbookRepository` lưu thực tế các Vector Chunks tri thức vào Supabase PostgreSQL (Schema `v_eval_ai`).
  - `GET /api/ai-engine/view-textbook`: Phục vụ giao diện HTML UI nạp SGK.

---

## [24/09/2026] - Cấu Hình Supabase Connection & Phân Chi Schema `v_eval_ai`
- **Cấu Hình Chuỗi Kết Nối PostgreSQL (Supabase Cloud)**:
  - Bổ sung `ConnectionStrings:DefaultConnection` trong `appsettings.json` kết nối trực tiếp Supabase Cloud PostgreSQL.
  - Khởi tạo và cập nhật `DATABASE_URL` trong `rag-service/.env` & `.env.example` cấu hình kết nối Python RAG Engine với Supabase (`postgresql+psycopg`).
- **Phân Chi Schema `v_eval_ai` & `v_eval_system`**:
  - Xác nhận tạo Schema `v_eval_ai`, `v_eval_system` và `pgvector` extension trên Supabase SQL Editor.
  - Đảm bảo hạ tầng Vector RAG Store cho `KnowledgeVectorChunks` (`v_eval_ai`) phục vụ bài toán RAG Engine cho Môn/Skill.

---

## [22/09/2026] - Triển Khai Diagnostic Engine Cho Core Flow 1 (IRT 2PL, BKT Prior & Radar Chart)
- **Module Tính Toán Năng Lực IRT & BKT (`rag-service/diagnostic_engine.py`)**:
  - Ước lượng năng lực học sinh `theta_0` bằng mô hình IRT 2-Parameter Logistic (2PL) kết hợp ước lượng hậu nghiệm cực đại (MAP) có hàm phạt Gaussian Prior `N(0, 2.0^2)`.
  - Tối ưu hóa cực trị 1 chiều bằng thuật toán Brent (`scipy.optimize.minimize_scalar`).
  - Xử lý câu trả lời đoán mò thần tốc (< 5 giây): chiết giảm tham số phân biệt `a -> 0.1` để tránh thổi phồng năng lực ảo.
  - Tính toán xác suất thành thạo ban đầu BKT Prior `P(L0) = Sigmoid(theta)` cho từng kỹ năng với cơ chế kẹp an toàn `[0.05, 0.95]`.
  - Xử lý tình huống không hoàn hảo (unhappy case): các kỹ năng chưa có câu hỏi trong bài kiểm tra rút gọn 30 câu tự động kế thừa `P(L0)` suy diễn từ năng lực miền cha (`domain_level`).
  - Phân loại xếp lớp chuẩn mực 3 mức: `FOUNDATION` (`theta < -0.5`), `ACCELERATION` (`-0.5 <= theta <= 0.5`), `BREAKTHROUGH` (`theta > 0.5`).
  - Sinh tọa độ biểu đồ Radar so sánh năng lực học sinh với điểm chuẩn benchmark dựa trên mục tiêu điểm thi (V-ACT target score).
- **Pydantic Schemas (`rag-service/schemas.py`)**:
  - Bổ sung `DiagnosticAnswerItem`, `DiagnosticDomainName`, `DiagnosticAnalyzeRequest`, `DiagnosticSkillPriorDto`, `DiagnosticDomainScoreDto`, `DiagnosticRadarAxisDto`, `DiagnosticAnalyzeResponse`.
- **API Endpoint & LLM Commentary (`rag-service/routers/diagnostic.py`)**:
  - Triển khai `POST /api/v1/diagnostic/analyze` tiếp nhận dữ liệu 30 câu từ Practice Service và trả về phân tích chẩn đoán hoàn chỉnh.
  - Sinh lời nhận xét Socratic sư phạm động bằng Google Gemini (`gemini-3.6-flash`), có cơ chế fallback tự động sinh nhận xét mẫu khi offline hoặc quá tải.
  - Triển khai `GET /api/v1/diagnostic/config` cung cấp cấu hình ngưỡng phân lớp và tham số IRT.
- **Kiểm Thử Đơn Vị, Tích Hợp & End-to-End**:
  - Đạt 12/12 test cases trong `rag-service/tests/test_diagnostic.py`.
  - Kiểm thử End-to-End thực tế với Practice Service qua Swagger: Trả về kết quả phân tích psychometrics, biểu đồ Radar, 12 BKT Priors và lời nhận xét Socratic trong thời gian thực.
- **Tài Liệu Đặc Tả Toán Học ([cong_thuc_psychometrics_irt_bkt.md](./cong_thuc_psychometrics_irt_bkt.md))**:
  - Biên soạn và ban hành tài liệu toán học toàn diện trình bày chi tiết toàn bộ các công thức IRT 2PL, MAP Brent, SEM, BKT Sigmoid, Domain Inference, Radar Benchmark và lý giải bài toán thực tế cho V-ACT.

---

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
