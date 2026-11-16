# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [16/11/2026] - Tích Hợp Bộ Nhớ Hội Thoại Đa Lượt (Multi-Turn Context), Tối Ưu Heuristic Router & Nâng Hạn Mức Timeout Gemini Cho Gia Sư Socratic

- **Tiếp Nhận & Bơm Ngữ Cảnh Lịch Sử Hội Thoại Đa Lượt ([`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py), [`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Mở rộng model `SocraticAskRequest` tiếp nhận trường `conversation_history: list[dict[str, Any]] = Field(default_factory=list)`.
  - Cập nhật hàm `build_socratic_prompts` định dạng các lượt trò chuyện gần nhất thành khối ngữ cảnh `[LỊCH SỬ TRAO ĐỔI VỪA RỒI CỦA 2 THẦY TRÒ]`, bơm trực tiếp vào `user_message` cho Case C4 (Hướng dẫn đào sâu) và Case C2 (Thắc mắc phương án).
  - Giúp mô hình Gemini duy trì sự liên tục của cuộc đối thoại sư phạm, ghi nhớ chính xác bài toán đang thảo luận (ví dụ: hình chóp `S.ABC`) khi học sinh hỏi tiếp *"thầy đưa em ví dụ minh họa với"* hoặc *"chọn mặt là sao"*.
- **Tối Ưu Hóa Heuristic Intent Router Chống Gán Nhầm Vào Case C1 ([`intent_router.py`](./rag-service/socratic/intent_router.py), [`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py))**:
  - Mở rộng danh sách từ khóa tác vụ `_TASK_MARKERS` bổ sung các từ gợi mở/hỏi nối tiếp: `"ví dụ"`, `"vi du"`, `"minh họa"`, `"minh hoa"`, `"làm sao"`, `"như thế nào"`, `"giải thích"`, `"hướng dẫn"`, `"thầy ơi"`, `"thầy chỉ"`, `"tiếp theo"`, `"cụ thể"`, `"chi tiết"`, `"là sao"`.
  - Trong `_resolve_dual_track` và `heuristic_route`, tiếp nhận `conversation_history`: nếu học sinh đang có bối cảnh bài tập hoặc lịch sử trao đổi, hệ thống tuyệt đối không định tuyến nhầm sang Case C1 (Chit-chat / Lời chào xã giao), ngăn chặn triệt để tình trạng AI quên bối cảnh và hỏi lại *"em muốn bắt đầu với môn nào"*.
- **Nâng Hạn Mức Timeout Sinh Nháp LLM Lên 18.0s ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Tăng ngưỡng thời gian chờ `asyncio.wait_for` từ `8.0s` lên `18.0s` cho các lượt gọi mô hình Gemini, thích ứng với độ trễ mạng thực tế và ngăn ngừa hiện tượng rơi nhầm vào `Resilient Fallback` do hết giờ quá sớm.
- **Kiểm Thử Tự Động**:
  - Chạy `pytest tests -k "socratic"` đạt **18/18 tests pass 100%**.
