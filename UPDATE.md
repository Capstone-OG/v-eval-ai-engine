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

## [18/09/2026] - Phát Hành Công Cụ Push Độc Lập `Scripts/push.bat` Cho AI Engine
- **Tích Hợp `Scripts/push.bat` Độc Lập**:
  - Khởi tạo script [`Scripts/push.bat`](file:///e:/CapStone/All%20Services/V-Eval-Ai_Engine/Scripts/push.bat) độc lập cho AI Engine.
  - Hỗ trợ Push nhanh trên nhánh hiện tại, chọn nhánh đã có qua Menu đánh số, hoặc tạo nhánh mới tự động.
  - Tích hợp tự động kiểm tra đồng bộ lịch sử Git với Remote, tự động pull code khi bi cham (behind) và đưa ra **Cảnh báo Đỏ (Red Warning)** ngắt quy trình khi bị xung đột lịch sử (Conflict/Diverged).

## [15/09/2026] - Dockerize AI Engine & Chuẩn Hóa Docker Compose
- **Dockerfile Multi-Stage .NET 9**:
  - Khởi tạo `Dockerfile` chuẩn cho AI Engine (`V-Eval-Ai_Engine.API`) với cổng `5104`.
  - Kết nối chung mạng nội bộ `veval_network` trong `docker-compose.yml`.

## [14/09/2026] - Chuẩn Hóa Cấu Hình Production & Git Security
- **Tạo File Cấu Hình Mẫu Production (`appsettings.example.json`)**:
  - Khởi tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` và `QdrantSettings`.
- **Cập Nhật `.gitignore` & Bảo Mật Mã Nguồn**:
  - Cập nhật quy tắc `.gitignore` ẩn toàn bộ các file `appsettings.json` cá nhân chứa API key thật.
