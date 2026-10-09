# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [09/10/2026] - Triển Khai Giai Đoạn 3 Core Flow 4 GraphRAG: Hybrid GraphRAG Retriever (Vector + Graph Traversal)

- **Tái Cấu Trúc Seeder Tri Thức Đa Dạng & Data-Driven (`database/seed_archetypes.py`)**:
  - Chuẩn hóa liên kết kỹ năng Taxonomy Level 3 theo tên chính xác (`Khảo sát hàm số` -> ID `3efcbd1e-cb9e-4998-8aee-89381f2e83bf`).
  - Bổ sung dạng bài chuẩn Vật lý `PHYS_SHM_MAX_SPEED_01` (Tính tốc độ cực đại dao động điều hòa) gắn liền Kỹ năng `Vật lý dao động điều hòa` (`93a98dce-abdb-4fac-ab26-e9476d1d236c`), kèm 1 câu hỏi mẫu chuẩn và 3 bẫy tư duy thường gặp.
- **Xây Dựng Module Truy Vết Đồ Thị Hai Bước (`graph/hybrid_retriever.py`)**:
  - **Hop 1 (Vector Anchor)**: Nhúng câu hỏi theo chuẩn Gemini 3072d (`embed_query`), ưu tiên quét trong phạm vi kỹ năng (`skill_id`), tự động fallback sang tìm kiếm toàn cục (`GLOBAL`) nếu kỹ năng bị phân loại sai hoặc chưa có.
  - **Cơ Chế Fail-Closed Grounding**: Nếu độ tương đồng cosine cao nhất $< 0.75$, trả về trạng thái `LOW_CONFIDENCE` và không trả về định lý sai, bảo vệ AI Tutor khỏi ảo giác tri thức.
  - **Hop 2 (Graph Expansion & Safe Trap Alignment)**:
    - Mở rộng đồ thị sang câu hỏi mẫu (`pattern_exemplars`) và danh mục bẫy tư duy (`pattern_traps`).
    - Phân biệt câu mẫu gốc vs câu biến thể: chỉ ánh xạ trực tiếp phương án A/B/C/D (`EXEMPLAR_OPTION_LETTER`) khi văn bản câu hỏi trùng khớp câu mẫu ($\ge 90\%$). Với câu hỏi biến thể bị xáo trộn phương án, trả về toàn bộ danh sách bẫy ứng viên (`ARCHETYPE_CANDIDATES`) để LLM đối sánh ngữ nghĩa ở Giai đoạn 4.
    - Chuẩn hóa loại bỏ ký hiệu LaTeX (`$`, `\`, `{`, `}`) giúp nhận diện trùng khớp câu hỏi chính xác $100\%$.
- **Endpoint Quản Trị & Kiểm Tra Subgraph (`routers/academic_graph.py`)**:
  - Bổ sung endpoint `POST /api/v1/academic-graph/subgraph/preview` cho phép xem trước toàn bộ Subgraph Context mà AI Tutor sẽ sử dụng.
- **Kiểm Thử & Vận Hành**:
  - Xác thực thực tế 5 kịch bản trên Supabase Cloud (câu mẫu, câu biến thể xáo trộn, câu sai skill, câu ngoài kho tri thức, câu trả lời đúng).
  - 15/15 unit tests passed (`test_hybrid_retriever.py` & `test_novelty_detector.py`).
  - Biên dịch .NET 9 (`dotnet build V-Eval-Ai_Engine.sln`) đạt 100% thành công (0 error).
