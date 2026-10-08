# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [08/10/2026] - Core Flow 4 GraphRAG: Giai Đoạn 1 (CSDL Knowledge Graph) & Giai Đoạn 2 (Novelty Detector)

- **Giai Đoạn 1 — Schema & Seeding (`rag-service/database/`)**:
  - 5 bảng trong schema `v_eval_ai` (Supabase): `archetype_patterns` (`vector(3072)`), `pattern_exemplars`, `pattern_traps`, `novel_pattern_proposals`, `ai_tutor_interaction_logs`.
  - Seed dạng bài `MATH_ASYMPTOTE_PARAM_01` kèm 1 câu mẫu và 2 bẫy tư duy; vector nhúng thật từ `models/gemini-embedding-001`.
  - Quét cosine tuần tự thay HNSW (3072 chiều vượt giới hạn 2000 chiều của HNSW, kho dạng bài nhỏ nên vẫn `< 1ms`).
- **Giai Đoạn 2 — Novelty Detector & Academic API**:
  - `graph/graph_db.py`: helper kết nối Supabase (`GRAPH_DATABASE_URL`), serialize pgvector, cache embedding.
  - `graph/novelty_detector.py`: ngưỡng cosine `0.75`, gắn cờ `NOVEL_PATTERN_CANDIDATE`, staging idempotent; nhận trực tiếp JSON `ParsedExamDto` từ `GeminiExamParserService.cs`.
  - `routers/academic_graph.py`: `POST /api/v1/academic-graph/novelty/check`, `GET /proposals`, `PATCH /proposals/{id}/review`.
- **Kiểm Thử**:
  - Live Supabase: câu cùng dạng `0.78-0.79` (MATCHED), câu khác dạng `0.53-0.55` (NOVEL).
  - `tests/test_novelty_detector.py` 7/7 passed; `dotnet build` 0 error.
