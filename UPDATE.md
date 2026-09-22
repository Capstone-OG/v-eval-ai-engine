# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [22/09/2026] - Triển Khai Diagnostic Engine Cho Core Flow 1 (IRT 2PL, BKT Prior, Placement & Radar Chart)
- **Module Tính Toán Năng Lực IRT 2PL & BKT Prior (`rag-service/diagnostic_engine.py`)**:
  - Triển khai thuật toán ước lượng năng lực học sinh `theta_0` bằng mô hình IRT 2PL và MAP Estimation (Gaussian Prior `N(0, 2.0^2)`), tối ưu hóa bằng Brent's method (`scipy.optimize.minimize_scalar`).
  - Xử lý câu trả lời đoán mò dưới 5 giây: giảm tham số phân biệt `a -> 0.1` để ngăn ngừa lạm phát năng lực do ăn may.
  - Tính toán xác suất thành thạo ban đầu BKT Prior `P(L0) = Sigmoid(theta)` cho từng kỹ năng với cơ chế kẹp an toàn `[0.05, 0.95]`.
  - Tự động suy diễn `P(L0)` từ năng lực miền (`domain_level`) cho các kỹ năng chưa xuất hiện trong 30 câu hỏi chẩn đoán (unhappy case handling).
  - Phân loại xếp lớp chuẩn mực: `FOUNDATION` (`theta < -0.5`), `ACCELERATION` (`-0.5 <= theta <= 0.5`), `BREAKTHROUGH` (`theta > 0.5`).
  - Dựng tọa độ biểu đồ Radar so sánh năng lực học sinh theo từng miền với điểm chuẩn benchmark dựa trên mục tiêu điểm thi (V-ACT target score).
- **Pydantic Schemas & Data Contracts (`rag-service/schemas.py`)**:
  - Khai báo trọn bộ contract REST API: `DiagnosticAnswerItem`, `DiagnosticDomainName`, `DiagnosticAnalyzeRequest`, `DiagnosticSkillPriorDto`, `DiagnosticDomainScoreDto`, `DiagnosticRadarAxisDto`, `DiagnosticAnalyzeResponse`.
- **API Endpoints & Socratic Commentary (`rag-service/routers/diagnostic.py`)**:
  - `POST /api/v1/diagnostic/analyze`: Nhận kết quả bài làm 30 câu, tính toán psychometrics và gọi Google Gemini (`gemini-3.5-flash`) tạo lời nhận xét sư phạm tích cực, có cơ chế fallback tự động.
  - `GET /api/v1/diagnostic/config`: Cung cấp tham số cấu hình ngưỡng và thang đo cho frontend/Practice Service.
- **Kiểm Thử Toàn Diện (`rag-service/tests/test_diagnostic.py`)**:
  - 10/10 test cases đơn vị và tích hợp HTTP endpoint đạt 100% PASS.
- **Cập Nhật Tài Liệu Service**:
  - Cập nhật [`docs/daily.md`](./docs/daily.md), [`docs/process.md`](./docs/process.md) và [`docs/architecture_acceptance.md`](./docs/architecture_acceptance.md).

## [21/09/2026] - Thiết Kế Kiến Trúc AI Exam Generation (30 Câu), Vector RAG Môn/Skill & Duyệt Đề Thi Linh Hoạt
- **Tạo Tài Liệu Thiết Kế Kế Hoạch Kiến Trúc ([ai_question_generation_rag_approval_plan.md](./docs/ai_question_generation_rag_approval_plan.md))**:
  - Đã xây dựng và tổng hợp chi tiết toàn bộ thiết kế hệ thống cho tính năng Sinh đề thi 30 câu tự động bằng AI, Vector RAG theo Môn/Skill và Quy trình Kiểm duyệt Linh hoạt dành cho Academic Manager.
  - Định nghĩa mô hình cơ sở dữ liệu `KnowledgeVectorChunks` (`pgvector`) phân loại tài liệu theo Môn (`domain_id`), Kỹ năng (`skill_id`) và nguồn tri thức (`document_type`).
  - Thiết lập luồng Hybrid Search (SGK Nội bộ + Dynamic Web Search tin tức mới nhất) khi bật flag `enable_web_search = true`.
  - Cấu hình luồng duyệt 2 tầng (`Questions.moderation_status` & `MockExams.approval_status`), cho phép Manager sinh câu tương đương thay thế chỉ trong 1-2 giây hoặc tự tinh chỉnh độ khó/bước tính toán (`difficulty_level`).
- **Cập Nhật Tài Liệu Tiến Độ Service**:
  - Cập nhật nhật ký kiểm tra hàng ngày [`docs/daily.md`](./docs/daily.md) và bảng tiến độ chủ thể [`docs/process.md`](./docs/process.md).

## [19/09/2026] - Gộp Conversational RAG Service (Python) Vào AI Engine
- **Merge `v-act-ai-service` → `rag-service/` (Sidecar Sub-Project)**:
  - Gộp toàn bộ Python Conversational RAG service vào thư mục `rag-service/` bên trong AI Engine.
  - Tech stack: Python 3.12 / FastAPI / LangChain LCEL / Google Gemini / PostgreSQL pgvector.
  - Không thay đổi bất kỳ file C# nào hiện có trong project .NET.
- **10 Commits Incremental**:
  - `config.py` — Config, env vars, model factories (LLM, Embeddings), PGEngine, PGVectorStore.
  - `schemas.py` — Pydantic request/response models (Chat, Document, Health, Versioning).
  - `requirements.txt` + `.env.example` — Python dependencies và environment template.
  - `ingestion.py` — Document ingestion pipeline (PDF/DOCX/Text, SHA-256 hashing, versioning, graceful swap).
  - `rag_engine.py` — Conversational RAG engine (History-Aware Retriever, LCEL, streaming, session store).
  - `routers/chat.py` — Chat endpoints (POST, SSE stream, session management).
  - `routers/documents.py` — Document CRUD (upload, text ingest, versions, rollback, soft delete, purge, stats).
  - `main.py` — FastAPI application entrypoint với CORS, lifespan events, health check.
  - `Dockerfile`, `docker-compose.yml`, `.dockerignore`, `.gitignore` — Docker containerization.
  - `README.md` — Documentation cho RAG sidecar service.
