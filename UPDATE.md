# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [25/09/2026] - Khởi Tạo Schema `v_eval_ai` Trên Supabase & Kết Nối Npgsql Ghi Vector Chunks
- **Khởi Tạo Schema `v_eval_ai` & Extension `pgvector` Trên Supabase**:
  - Đã thực thi DDL tạo mới schema `v_eval_ai` và extension `vector` trên Supabase PostgreSQL Cloud.
  - Tạo 2 bảng chuẩn RAG tri thức: `v_eval_ai."KnowledgeSources"` và `v_eval_ai."KnowledgeVectorChunks"` kèm HNSW Index (`idx_knowledge_vector_hnsw`).
  - Lưu trữ kịch bản DDL chuẩn mực tại [`docs/SQL/V_EVAL_AI_SCHEMA.sql`](./docs/SQL/V_EVAL_AI_SCHEMA.sql).
- **Tích Hợp Npgsql TextbookRepository (`TextbookRepository.cs`)**:
  - Triển khai `TextbookRepository` (`ITextbookRepository`) ghi nhận trực tiếp tài liệu SGK và các Chunks tri thức vào database Supabase khi người dùng bấm nút **`💾 Lưu Tri Thức Vào Database`**.
- **Cập Nhật Bộ Tài Liệu Tiến Độ**:
  - Đồng bộ nhật ký vận hành tại [`docs/daily.md`](./docs/daily.md), [`docs/process.md`](./docs/process.md) và [`docs/architecture_acceptance.md`](./docs/architecture_acceptance.md).
