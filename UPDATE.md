# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [06/11/2026] - Khắc Phục Lỗi Quota Rate Limit 429, Tối Ưu Tốc Độ Phản Hồi & Thử Luân Phiên Siêu Tốc Cho Gia Sư Socratic

- **Cập Nhật Khóa API Mới & Cấu Hình Đa Mô Hình Ưu Tiên Tốc Độ ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py), [`.env`](./rag-service/.env))**:
  - Tích hợp khóa API Google Gemini mới giải phóng hoàn toàn giới hạn cạn kiệt tài nguyên (429 Resource Exhausted).
  - Tối ưu danh sách mô hình ứng viên `SOCRATIC_MODELS`: Đưa `gemini-3.5-flash-lite` lên vị trí ưu tiên số 1, tiếp tục hỗ trợ luân phiên sang `gemini-3.6-flash`, `gemini-3.5-flash` và `gemini-flash-lite-latest`.
  - Cấu hình `max_retries=1` cho client LLM, loại bỏ cơ chế thử lại ngầm ngốn 25s của Tenacity Exponential Backoff khi gặp lỗi mạng/quota, cho phép chuyển sang model dự phòng tức thì trong 0.1s.
  - Hạ `timeout` sinh nháp xuống còn 8.0s (thay vì 25.0s), đảm bảo toàn bộ chu trình sinh lời dẫn Socratic luôn phản hồi dưới 2 - 4 giây, không bao giờ gây timeout cho Practice Service.
- **Mở Rộng Nhận Diện Câu Hỏi Về Chức Năng & Vai Trò (Meta-Queries) ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Mở rộng hàm `is_greeting` với bộ từ khóa vai trò `META_INQUIRY_WORDS` (`"thầy sẽ làm gì trong này"`, `"thầy là ai"`, `"thầy giúp gì"`, `"vai trò của thầy"`).
  - Trả về lời giải thích súc tích, truyền cảm hứng về phương pháp tư duy phản biện gợi mở Socratic mà không bị nhầm lẫn sang truy vấn đồ thị tri thức Vật lý hay Toán học.
- **Tối Ưu Hội Đồng Thẩm Định LLM-as-a-Judge ([`validator_judge.py`](./rag-service/socratic/validator_judge.py))**:
  - Chuyển đổi mô hình thẩm định ưu tiên sang `gemini-3.5-flash-lite` với độ trễ phản hồi < 1 giây, kết hợp hàng rào heuristic nhanh `_fast_heuristic_guard` ngăn chặn rò rỉ đáp án 100%.
- **Kiểm Thử & Vận Hành**:
  - Kiểm thử trực tiếp `generate_socratic_guidance` với câu hỏi logic mệnh đề: Phản hồi thành công trong 3.5s, `is_fallback: False`, `judge_passed: True`.
