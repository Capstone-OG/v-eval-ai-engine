# Conversational RAG System

A production-ready Conversational Retrieval-Augmented Generation system powered by **Google Gemini**, **LangChain (LCEL)**, and **PostgreSQL + pgvector**.

## Architecture

```
User Question + Chat History
        │
        ▼
┌───────────────────────────┐
│  History-Aware Retriever  │  → reformulates question → vector search
└─────────────┬─────────────┘
              │  retrieved docs
              ▼
┌───────────────────────────┐
│  Stuff Documents QA Chain │  → context + history → Gemini LLM
└─────────────┬─────────────┘
              │
              ▼
┌───────────────────────────┐
│ RunnableWithMessageHistory│  → auto-saves turns per session_id
└───────────────────────────┘
```

## Quick Start

### Cách 1: Khởi chạy 1-Click với Docker Compose (Khuyên dùng)

```bash
# 1. Tạo file cấu hình từ template và điền GOOGLE_API_KEY
cp .env.example .env

# 2. Khởi chạy toàn bộ hệ thống (FastAPI + PostgreSQL pgvector)
docker compose up -d --build
```

- **Swagger UI Interactive Docs**: [http://localhost:8000/docs](http://localhost:8000/docs)
- **ReDoc Docs**: [http://localhost:8000/redoc](http://localhost:8000/redoc)

---

### Cách 2: Khởi chạy thủ công cho môi trường Development

```bash
# 1. Khởi động PostgreSQL + pgvector
docker run -d --name pgvector-rag -e POSTGRES_USER=rag_user -e POSTGRES_PASSWORD=rag_password -e POSTGRES_DB=rag_db -p 5433:5432 pgvector/pgvector:pg16

# 2. Tạo virtual environment & cài đặt thư viện
python -m venv .venv
.venv\Scripts\activate   # Trên Windows
pip install -r requirements.txt

# 3. Chạy server FastAPI
python main.py
```

---

## REST API Endpoints

### 1. Conversational Chat
- **`POST /api/v1/chat`**: Ask a question with conversational memory.
  ```json
  {
    "question": "V-Act cung cấp dịch vụ gì?",
    "session_id": "user-123"
  }
  ```
- **`POST /api/v1/chat/stream`**: Stream answer token-by-token using Server-Sent Events (SSE).
- **`GET /api/v1/chat/sessions`**: List all active session IDs.
- **`DELETE /api/v1/chat/sessions/{session_id}`**: Clear history for a session.

### 2. Document Ingestion & Versioning Management
- **`POST /api/v1/documents/upload`**: Upload PDF or DOCX file with SHA-256 duplicate checking & background Graceful Swap.
- **`POST /api/v1/documents/text`**: Ingest plain text content with `title` grouping and SHA-256 hash comparison.
- **`GET /api/v1/documents`**: List all logical documents with their currently active version and total version count.
- **`GET /api/v1/documents/{document_group_id}/versions`**: View full version history for a logical document.
- **`POST /api/v1/documents/{document_group_id}/rollback`**: Rollback to an earlier version without recalculating embeddings:
  ```json
  { "version": 1 }
  ```
- **`DELETE /api/v1/documents/{document_group_id}`**: Soft delete (de-activate all versions from RAG while preserving history).
- **`DELETE /api/v1/documents/{document_group_id}/purge`**: Permanently hard-delete a document and all its vector chunks.
- **`GET /api/v1/documents/stats`**: Get detailed statistics of catalog records and active vector chunks.

### 3. System Health
- **`GET /health`**: Health check, database connection status, and active models.

---

## Project Structure

```
├── Dockerfile          # Production Docker image configuration
├── docker-compose.yml  # Multi-container orchestration (App + Database)
├── .dockerignore       # Excludes local files from Docker image
├── .env.example        # Environment variables template
├── requirements.txt    # Dependencies (FastAPI, LangChain, pgvector, etc.)
├── config.py           # Configuration & model factories (Gemini, PGEngine)
├── schemas.py          # Pydantic models for request & response
├── routers/            # FastAPI router modules
│   ├── chat.py         # Conversational Q&A & streaming endpoints
│   └── documents.py    # Document upload, versioning & management
├── ingestion.py        # Document loading, chunking, hash checking & swap
├── rag_engine.py       # Conversational RAG chain (LCEL) & metadata filter
└── main.py             # FastAPI application entrypoint & Swagger setup
```

