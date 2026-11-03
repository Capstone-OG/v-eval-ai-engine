# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [03/11/2026] - Nâng Cấp Socratic AI Engine: Hỗ Trợ Hội Thoại Đa Lượt (student_query) & Khắc Phục Gán Nhầm Anchor Tri Thức

- **Hỗ Trợ Hội Thoại Đa Lượt `student_query` ([`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py), [`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Mở rộng Pydantic `SocraticAskRequest` và prompt Socratic hỗ trợ câu hỏi trực tiếp của học sinh (`student_query`), chấm dứt tình trạng nhại lại chuỗi `[Ngữ cảnh câu hỏi: ...]`.
- **Ràng Buộc Kiểm Định `is_grounded` Khắc Phục Gán Nhầm Dạng Bài ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Chỉ nạp anchor từ Knowledge Graph khi đạt trạng thái `GROUNDED`. Với câu hỏi `LOW_CONFIDENCE`, hệ thống không ép định lý của dạng bài khác, giúp LLM phản hồi chính xác bản chất từng môn học (như toán logic, ngữ văn, KHTN).
- **Kiểm Thử & Xác Thực**:
  - Xác thực trực tiếp qua Gemini API và endpoint `/api/v1/socratic/ask` đạt `PASSED: True`, `IS_FALLBACK: False`.
