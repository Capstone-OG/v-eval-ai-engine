# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [05/11/2026] - Chuyển Đổi Mô Hình Chính Gemini-3.6-Flash, Loại Bỏ Hoàn Toàn Phản Hồi Rập Khuôn & Tối Ưu Phản Hồi Tự Nhiên Socratic

- **Loại Bỏ Lỗi Rập Khuôn / Cứng Nhắc, Khôi Phục AI Sinh Phản Hồi Tự Nhiên ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Chuyển đổi mô hình ưu tiên hàng đầu sang `gemini-3.6-flash` (thay thế cho `gemini-flash-lite-latest` và `gemini-flash-latest` đang bị quá tải 503 từ Google AI).
  - Nâng ngưỡng thời gian sinh bản thảo LLM lên 25.0s kèm cơ chế thử luân phiên đa mô hình (`candidate_models`), ngăn chặn tình trạng cắt ngang LLM sớm dẫn đến rơi vào văn mẫu định lý toán học.
  - Tách bạch hoàn toàn lời chào hỏi hội thoại (`"hi"`, `"hi gia sư"`, `"chào gia sư"`, `"thầy ơi"`) khỏi các bài toán dạng trắc nghiệm, trả về lời chào Socratic ấm áp và gợi mở chủ đề thay vì in công thức tam thức bậc hai hay bất phương trình.
  - Bổ sung nhánh xử lý câu hỏi mở/tự do (`student_selected_option` rỗng hoặc NONE) với lời dẫn dắt sư phạm gợi mở bước tư duy hiện tại của học sinh.
- **Tối Ưu Tốc Độ Thẩm Định LLM-as-a-Judge ([`validator_judge.py`](./rag-service/socratic/validator_judge.py))**:
  - Rút ngắn lượt kiểm tra thẩm định nhanh (timeout 3.0s) và chuyển tiếp ngay sang `_fast_heuristic_guard` khi Google API gặp đợt nghẽn tải, giảm tổng thời gian phản hồi từ >35s xuống ~4-6s.
  - Ngăn ngừa triệt để hiện tượng treo `...` vô tận trên giao diện học sinh.
- **Chuẩn Hóa Bộ Từ Điển Định Tuyến Tri Thức ([`hybrid_retriever.py`](./rag-service/graph/hybrid_retriever.py))**:
  - Làm sạch hoàn toàn lỗi font UTF-8/mojibake, bổ sung nhận diện chuyên đề logic mệnh đề (`p => q`, `q => r`), hàm số logarit và cấp số cộng.
- **Kiểm Thử & Vận Hành**:
  - Toàn bộ 12/12 kiểm thử `pytest` (`test_socratic_engine.py`, `test_socratic_mentor.py`) đạt 100% trong 2.25s.
  - Kiểm thử trực tiếp qua Gateway `localhost:5212` với `"hi gia sư"`: trả về lời chào Socratic tự nhiên 100% sinh bởi LLM (`is_fallback: false`, `subgraph_status: GENERAL_CHAT`).
  - Kiểm thử câu hỏi logic: phản hồi chính xác lý do phương án chọn bị mâu thuẫn tiền đề mà không tiết lộ đáp án cuối cùng.
