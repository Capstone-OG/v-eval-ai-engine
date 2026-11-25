# UPDATE LOG — V-Eval AI Engine

## [25/11/2026] Chuẩn hóa Nhúng Vector 768 chiều (Gemini MRL) & Truy vấn Ngữ nghĩa thật trên pgvector HNSW
- **Dịch vụ Embedding đa Key (`IEmbeddingService`, `GeminiEmbeddingService`)**:
  - Hỗ trợ `EmbedTextAsync` (`RETRIEVAL_QUERY`) và `EmbedBatchAsync` (`batchEmbedContents`, `RETRIEVAL_DOCUMENT`) dùng `models/gemini-embedding-001` (`outputDimensionality = 768`, L2-normalize, Multi-Key Rotation từ `AiSettings:GeminiApiKeys`).
- **Chunking gối đầu & Lưu cột `embedding::vector` trong `TextbookRepository` & `PdfPigTextbookParserService`**:
  - Cắt trượt gối đầu theo ngữ nghĩa (`1000` ký tự, overlap `200`, không cắt ngang công thức KaTeX) khi nạp SGK mới.
  - Cập nhật `SavePageChunkAsync` lưu trực tiếp `embedding::vector(768)` vào `v_eval_ai."KnowledgeVectorChunks"`; bổ sung `GetUnembeddedChunksAsync`, `UpdateChunkEmbeddingAsync`, `CountUnembeddedChunksAsync` để backfill không cần parse lại PDF.
- **Thay thế `QdrantVectorDbService` (in-memory) bằng `PgVectorDbService` thật**:
  - Đăng ký `IEmbeddingService` và chuyển `IVectorDbService` sang `PgVectorDbService` tại `DependencyInjection.cs`.
  - Truy vấn Cosine Similarity (`<=>`) trực tiếp trên chỉ mục HNSW `idx_knowledge_vector_hnsw` kèm **Neighbor Chunk Expansion** (`chunk_index - 1`, `+ 1`).
- **Endpoint Backfill & Semantic Search (`TextbookEndpoints.cs`)**:
  - Bổ sung `POST /api/ai-engine/textbooks/backfill-embeddings` (alias `/api/ai/textbooks/backfill-embeddings`) và `POST /api/ai-engine/textbooks/search-semantic` (alias `/api/ai/textbooks/search-semantic`).
  - Thực hiện backfill trực tiếp vector 768 chiều cho toàn bộ 708 chunk SGK hiện có trong Supabase PostgreSQL.
