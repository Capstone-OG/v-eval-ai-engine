# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [04/11/2026] - Phân Định Chế Độ Chào Hỏi / Hội Thoại Tự Do & Khắc Phục Hiện Tượng Gán Nhầm Bẫy Bất Phương Trình

- **Nhận Diện Lời Chào & Bỏ Qua Truy Vấn Vector ([`socratic_tutor.py`](./rag-service/routers/socratic_tutor.py), [`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Bổ sung hàm tiện ích `is_greeting` phát hiện các câu chào (`hi`, `hello`, `chào`...).
  - Bỏ qua bước truy vấn pgvector đối với lời chào để không gán ghép sai lệch vào dạng bài tam thức bậc hai (`ARCH_MATH_FUNCTION_MONOTONICITY_01`), trả về subgraph `status="GENERAL_CHAT"`.
- **Prompt Sư Phạm Không Quy Chụp Cho Hội Thoại Mở ([`socratic_engine.py`](./rag-service/socratic/socratic_engine.py))**:
  - Khi học sinh không có phương án trắc nghiệm hoặc gửi `student_selected_option == "NONE"`, prompt hướng dẫn Gemini gợi mở bản chất tổng quát, không quy chụp học sinh chọn sai phương án A.
  - Phản hồi lời chào tự động vượt qua kiểm định LLM-as-a-Judge (`status = "PASSED"`).
- **Kiểm Thử Thực Tế**:
  - `POST /api/practice/ai-tutor/ask` với `questionLatex: "hi"` trả về HTTP 200, phản hồi chào mừng sư phạm tự nhiên, không nhắc đến tam thức bậc hai hay bẫy toán học.
