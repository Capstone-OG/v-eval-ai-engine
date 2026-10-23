# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [23/10/2026] - Đồng Bộ CSDL Knowledge Graph RAG & Socratic Audit Vào Master SQL.sql Và Capstone.drawio

- **Đồng Bộ CSDL Phân Hệ AI Engine (`v_eval_ai`)**:
  - Hợp nhất toàn bộ 7 bảng thực thể vào `docs/SQL/SQL.sql`: `KnowledgeSources`, `KnowledgeVectorChunks` (768 chiều), `archetype_patterns` (Level 4 Taxonomy Knowledge Graph), `pattern_exemplars`, `pattern_traps`, `novel_pattern_proposals`, `ai_tutor_interaction_logs`.
  - Chuẩn hóa đầy đủ các trường vector embedding, giải thuật Cosine Distance, cấu trúc JSONB cho các bước giải và lịch sử hội thoại Socratic.
- **Đồng Bộ ERD Trang 1 Trong `docs/SQL/Capstone.drawio`**:
  - Phân bổ vào package: `7. AI Tutor & Graph RAG Tri thức` (7 bảng).
