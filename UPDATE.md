# Nhật Ký Cập Nhật (Update Log) - AI Engine

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
