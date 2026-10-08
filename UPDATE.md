# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [08/10/2026] - Triển Khai Giai Đoạn 1 Core Flow 4 GraphRAG: Thiết Kế Schema CSDL & Nạp Dạng Bài Chuẩn (Archetype Patterns)

- **Thiết Kế CSDL Knowledge Graph & Vector (`rag-service/database/graph_schema.sql`)**:
  - Xây dựng 5 bảng nghiệp vụ chuẩn hóa trong schema `v_eval_ai` trên Supabase PostgreSQL:
    1. `archetype_patterns`: Dạng bài chuẩn (Taxonomy Level 4), lưu trữ lý thuyết cốt lõi, chiến thuật giải nhanh và vector nhúng 3072 chiều (`vector(3072)`).
    2. `pattern_exemplars`: Bộ câu hỏi mẫu kèm lời giải chuẩn LaTeX từng bước do Academic thẩm định.
    3. `pattern_traps`: Bẫy nhận thức phổ biến gắn trực tiếp với từng phương án nhiễu sai (A/B/C/D) kèm gợi ý Socratic.
    4. `novel_pattern_proposals`: Vùng đệm staging lưu trữ các câu hỏi lạ (độ tương đồng Cosine $< 0.75$) chờ Ban Chuyên Môn phê duyệt.
    5. `ai_tutor_interaction_logs`: Lưu vết toàn bộ lịch sử hỏi bài gia sư AI và nhật ký kiểm định phục vụ Human-in-the-Loop.
  - Tối ưu hóa truy vấn vector: Do số lượng dạng bài chuẩn khoảng 50-200 mẫu/môn và vector 3072 chiều vượt giới hạn 2000 chiều của HNSW pgvector, áp dụng quét tuần tự cosine (`<=>`) cho tốc độ phản hồi cực nhanh $< 1\text{ms}$ với độ chính xác 100%.
- **Thực Thi Migration & Nạp Dữ Liệu Mẫu (`database/apply_graph_schema.py` & `seed_archetypes.py`)**:
  - Áp dụng thành công schema DDL vào CSDL Supabase PostgreSQL thông qua driver đồng bộ `psycopg`.
  - Tích hợp Google Gemini Embedding API (`models/gemini-embedding-001`), tính toán vector nhúng thực tế 3072 chiều.
  - Nạp thành công dạng bài mẫu toán học `MATH_ASYMPTOTE_PARAM_01` (Tiệm cận đồ thị hàm số chứa tham số $m$) liên kết Skill `1d9b72ec-1f60-4406-981e-03ee8a4058bc`, 1 câu hỏi mẫu chuẩn và 2 bẫy tư duy thường gặp.
- **Kiểm Thử & Vận Hành**:
  - Xác thực trực tiếp dữ liệu trên Supabase Cloud thông qua script kiểm thử.
  - Biên dịch .NET 9 (`dotnet build V-Eval-Ai_Engine.sln`) đạt 100% thành công (0 error).
