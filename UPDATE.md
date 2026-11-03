# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [03/11/2026] - Nâng Cấp Toàn Diện Socratic AI Engine: Nạp Tri Thức Curated Archetype & Khắc Phục Lỗi 502 Bằng Bộ Tìm Kiếm Từ Khóa Dự Phòng

- **Nạp Tri Thức Dạng Bài Khảo Thí Chuẩn Vào Knowledge Graph ([`seed_curriculum_archetypes.py`](./rag-service/database/seed_archetypes.py))**:
  - Bổ sung và nhúng vector 3072 chiều 5 dạng bài trọng tâm: Toán logic mệnh đề `P \Rightarrow Q` (`ARCH_LOGIC_PROPOSITION_01`), Dãy số & Cấp số cộng (`ARCH_MATH_PROGRESSION_01`), Hàm số Mũ & Logarit (`ARCH_MATH_LOG_EXP_01`), Tính đơn điệu hàm số (`ARCH_MATH_FUNCTION_MONOTONICITY_01`), và Chính tả Tiếng Việt chuẩn (`ARCH_LANG_VIETNAMESE_01`).
- **Khắc Phục Lỗi 502 & Bộ Tìm Kiếm Từ Khóa Dự Phòng ([`hybrid_retriever.py`](./rag-service/graph/hybrid_retriever.py), [`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py))**:
  - Triển khai cơ chế `_anchor_candidates_by_keywords` tự động quét từ khóa trong PostgreSQL `v_eval_ai.archetype_patterns` khi embedding gặp sự cố hoặc offline.
  - Bọc bắt ngoại lệ toàn diện tại `ask_socratic_json` và `ask_socratic_stream`, trả về `STATUS_LOW_CONFIDENCE` thay vì ném ngoại lệ HTTP 502.
- **Tối Ưu Bất Đồng Bộ LLM-as-a-Judge & Bộ Suy Luận Socratic ([`validator_judge.py`](./rag-service/socratic/validator_judge.py), [`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Chuyển đổi gọi thẩm định sang `await client.aio.models.generate_content` giải phóng event loop asyncio.
  - Tích hợp bộ tạo lời giải sư phạm fallback bám sát từng môn học (Logic, Cấp số cộng, Logarit, Chính tả).
- **Kiểm Thử Thực Tế**:
  - Endpoint `/api/v1/socratic/ask` cho câu hỏi logic $P \Rightarrow Q$ đạt `PASSED: True`, `IS_FALLBACK: False`, mã dạng bài `ARCH_LOGIC_PROPOSITION_01`, bẫy `TRAP_LOGIC_UNDETERMINED`.
