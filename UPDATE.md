# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [16/11/2026] - Kiến Trúc Dynamic Federated Curriculum Retrieval (Zero-Hardcode), Tích Hợp Bộ Nhớ Hội Thoại Đa Lượt & Khắc Phục Triệt Để Gán Nhầm Anchor Socratic

- **Kiến Trúc Dynamic Federated Curriculum Retrieval (Zero-Hardcode) ([`hybrid_retriever.py`](./rag-service/graph/hybrid_retriever.py), [`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py))**:
  - **Khắc phục nguyên nhân gốc rễ**: Loại bỏ bộ so khớp từ khóa tĩnh và vòng lặp tách từ đơn (`len(w) >= 3 and w in q_lower`) vốn khiến từ nối tiếng Việt như *"của"* trong *"Giao tuyến của hai mặt phẳng"* kích hoạt nhầm anchor *"Tính đơn điệu của hàm số đa thức và phân thức"* với điểm ảo 0.85.
  - **Truy vấn động liên kết CSDL SGK chuẩn (`v_eval_content."Materials"` & `"Questions"`)**:
    - Khi nhận `question_id`, hệ thống tự động JOIN giữa bảng câu hỏi và bài học giáo khoa SGK chuẩn GDPT 2018 (`v_eval_content."Questions" q JOIN v_eval_content."Materials" m ON m.skill_id = q.skill_id WHERE q.question_id = %(qid)s::uuid`).
    - Nạp tức thì toàn văn bài học giáo khoa (định lý bản lề, phương pháp giải, bài toán mẫu) và lời giải vàng (`golden_solution`) vào Socratic Subgraph với độ tin cậy 0.98 và phạm vi `CURRICULUM_MATERIAL`.
  - **Xóa bỏ hoàn toàn Hardcode & Stopword Matching**:
    - Xóa bỏ danh từ điển từ khóa tĩnh `keywords_map` và vòng lặp tách từ đơn.
    - Với các câu hỏi ngoài chương trình hoặc câu hỏi mở không có tài liệu, hệ thống tự động kiểm tra Cosine Similarity pgvector: nếu $< 0.75$, fail-closed an toàn về `STATUS_LOW_CONFIDENCE` mà tuyệt đối không bịa đặt hoặc gán nhầm sang chuyên đề không liên quan.
- **Tối Ưu Hóa LLM Judge & Bộ Fallback An Toàn Zero-Hardcode ([`validator_judge.py`](./rag-service/socratic/validator_judge.py), [`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Khi `grounded=False` hoặc `LOW_CONFIDENCE`, LLM Judge chỉ thẩm định tính đúng đắn toán học/logic (`CRITERION_LOGIC`) và tính nhất quán (`CRITERION_GROUND_TRUTH`) mà không đòi hỏi đối chiếu định lý ngoài luồng.
  - Xóa bỏ các nhánh `if/elif` hardcode từng môn học trong `build_fallback_guidance` ($P \Rightarrow Q$, cấp số cộng, logarit, chính tả). Khi rơi vào fallback, hệ thống tự động bám sát định lý bài học đã nạp hoặc đưa ra câu hỏi gợi mở sư phạm phản chiếu trung lập, chuẩn mực cho mọi môn học.
- **Tiếp Nhận & Bơm Ngữ Cảnh Lịch Sử Hội Thoại Đa Lượt ([`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py), [`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Mở rộng model `SocraticAskRequest` tiếp nhận trường `conversation_history: list[dict[str, Any]] = Field(default_factory=list)`.
  - Cập nhật hàm `build_socratic_prompts` định dạng các lượt trò chuyện gần nhất thành khối ngữ cảnh `[LỊCH SỬ TRAO ĐỔI VỪA RỒI CỦA 2 THẦY TRÒ]`, bơm trực tiếp vào `user_message` cho Case C4 (Hướng dẫn đào sâu) và Case C2 (Thắc mắc phương án).
  - Giúp mô hình Gemini duy trì sự liên tục của cuộc đối thoại sư phạm, ghi nhớ chính xác bài toán đang thảo luận (ví dụ: hình chóp `S.ABCD`, giao tuyến `SO`) khi học sinh hỏi tiếp nối.
- **Tối Ưu Hóa Heuristic Intent Router Chống Gán Nhầm Vào Case C1 ([`intent_router.py`](./rag-service/socratic/intent_router.py), [`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py))**:
  - Mở rộng danh sách từ khóa tác vụ `_TASK_MARKERS` bổ sung các từ gợi mở/hỏi nối tiếp: `"ví dụ"`, `"vi du"`, `"minh họa"`, `"minh hoa"`, `"làm sao"`, `"như thế nào"`, `"giải thích"`, `"hướng dẫn"`, `"thầy ơi"`, `"thầy chỉ"`, `"tiếp theo"`, `"cụ thể"`, `"chi tiết"`, `"là sao"`.
  - Trong `_resolve_dual_track` và `heuristic_route`, tiếp nhận `conversation_history`: nếu học sinh đang có bối cảnh bài tập hoặc lịch sử trao đổi, hệ thống tuyệt đối không định tuyến nhầm sang Case C1 (Chit-chat / Lời chào xã giao).
- **Nâng Hạn Mức Timeout Sinh Nháp LLM Lên 18.0s ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Tăng ngưỡng thời gian chờ `asyncio.wait_for` từ `8.0s` lên `18.0s` cho các lượt gọi mô hình Gemini, thích ứng với độ trễ mạng thực tế và ngăn ngừa hiện tượng rơi nhầm vào `Resilient Fallback` do hết giờ quá sớm.
- **Kiểm Thử Toàn Diện**:
  - `pytest tests/test_hybrid_retriever.py tests/test_socratic_engine.py tests/test_socratic_mentor.py`: Đạt **26/26 tests pass 100%**.
  - Kiểm thử thực tế câu hỏi Hình học Không gian giao tuyến `SO` (`4e59df20-e11b-4d14-ab8f-0ac4cb734d0b`): Trích xuất chính xác 100% tài liệu `Dạng 2: Xác định giao tuyến của hai mặt phẳng phân biệt cắt nhau` và `core_theorems` SGK tương ứng.
