# Nhật Ký Cập Nhật (Update Log) - AI Engine

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
