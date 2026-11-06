# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [06/11/2026] - Triển Khai Kiến Trúc 2 Luồng Phân Tách (Decoupled Dual-Track Architecture) & Ma Trận 5 Intent Cho Gia Sư Socratic

- **Kiến Trúc 2 Luồng Phân Tách (Decoupled Dual-Track Architecture) ([`intent_router.py`](./rag-service/socratic/intent_router.py), [`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py))**:
  - **Triệt tiêu hoàn toàn ô nhiễm vector (Semantic Contamination)**: Track 1 (Task Grounding) chỉ embed duy nhất `question_latex + options` vào không gian pgvector của Knowledge Graph, cách ly 100% câu hỏi hội thoại tự do của học sinh (`student_query`), bảo đảm định vị chuẩn xác dạng bài và bẫy tư duy.
  - **Định tuyến ý định động (Dynamic Intent & Tone Router - Track 2)**: Xây dựng module `socratic/intent_router.py` bóc tách tin nhắn học sinh thành Ma trận 5 tình huống sư phạm (C1: Persona/Chit-chat, C2: Thái độ + Bài toán, C3: Nghi ngờ tính hợp lệ đề bài, C4: Thắc mắc chuyên môn trong KG, C5: Đề bài ngoài hệ thống), nhận diện tâm trạng học sinh (`NEUTRAL`, `SKEPTICAL`, `FRUSTRATED`, `CURIOUS`) và mục tiêu dẫn dắt.
  - **Thực thi song song (Concurrent Execution)**: `ask_socratic_json` và `ask_socratic_stream` thực thi song song Track 1 và Track 2 qua `asyncio.gather`. Bỏ qua vector search khi gặp lời chào C1 (0ms KG latency).
- **Hệ Thống Prompt Sư Phạm Modular 2 Khối ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - **Khối 1 (Persona Core)**: Giọng điệu người thầy thông thái, ấm áp, khiêm tốn, sắc sảo; xưng hô thầy - em; nghiêm cấm tự xưng chatbot/mô hình AI; tuân thủ nguyên tắc Socratic co-pilot (cấm giải thay, cấm đưa kết quả số cuối, cấm lộ đáp án đúng).
  - **Khối 2 (Dynamic Case Injection)**: Tiêm chỉ thị chuyên biệt bám sát ma trận C1–C5, tâm trạng học sinh và nguồn tri thức Subgraph.
  - **Safe Fallback Phân Hóa**: `build_fallback_guidance` trả về thông điệp cứu cánh sư phạm phù hợp theo từng Category khi gặp sự cố mạng.
- **Hội Đồng Thẩm Định Thích Ứng (Adaptive LLM-as-a-Judge) ([`validator_judge.py`](./rag-service/socratic/validator_judge.py))**:
  - Tiêu chí thẩm định linh hoạt theo Category: C1 kiểm tra tính thân thiện và kéo về việc học (không đòi hỏi định lý KG); C2/C3/C4 kiểm tra bám sát định lý và tính nhất quán đáp án đúng; C5 kiểm tra tính đúng đắn logic; mọi tình huống bắt buộc không lộ đáp án trực tiếp (`revealed_direct_answer == False`).
- **Phát Sự Kiện Tiến Độ SSE (Status Hydration) ([`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py))**:
  - Bổ sung phát sự kiện `event: status` (`"Gia sư đang phân tích thắc mắc của bạn..."`) và `intent_category` trong `event: metadata` cho giao diện người dùng.
- **Khắc Phục Quota Rate Limit 429 & Tối Ưu Client LLM Siêu Tốc ([`llm_clients.py`](./rag-service/socratic/llm_clients.py), [`.env`](./rag-service/.env))**:
  - Nạp khóa API Google Gemini mới, cấu hình `HttpRetryOptions(attempts=1)` và `max_retries=1`, loại bỏ vòng lặp treo 25s Tenacity retry, ưu tiên `gemini-3.5-flash-lite` phản hồi toàn chu trình trong ~1.5 - 2.5s.
- **Kiểm Thử & Nghiệm Thu**:
  - 50/50 test cases đạt 100% trong `pytest` (`test_intent_router.py`, `test_socratic_engine.py`, `test_diagnostic.py`, `test_academic_graph.py`).
  - Biên dịch .NET 9 Practice Service thành công sạch sẽ (0 Warning, 0 Error).
