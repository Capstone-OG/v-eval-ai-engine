# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [27/09/2026] - Chuẩn Hóa IRT 2PL Cho 6 Cấp Độ Bloom & Kiến Trúc Chế Độ Kép (Dual Engine: Gemini Cloud vs Siêu Tốc)

- **Chuẩn Hóa Ánh Xạ Độ Khó IRT 2PL Theo 6 Mức Độ Tư Duy Bloom (`diagnostic_engine.py`)**:
  - Nâng cấp từ 4 cấp độ cũ lên đầy đủ 6 mức độ tư duy chuẩn Bloom cải tiến (Revised Bloom's Taxonomy):
    - Mức 1: Nhận biết (Remembering) `-> b = -1.8`
    - Mức 2: Thông hiểu (Understanding) `-> b = -1.0`
    - Mức 3: Vận dụng (Applying) `-> b = -0.2`
    - Mức 4: Phân tích (Analyzing) `-> b = +0.6`
    - Mức 5: Đánh giá (Evaluating) `-> b = +1.4`
    - Mức 6: Sáng tạo (Creating) `-> b = +2.2`
  - Cập nhật schema Pydantic [`DiagnosticAnswerItem`](./rag-service/schemas.py) mở rộng `difficulty_level` từ `[1..4]` lên `[1..6]`.
- **Triển Khai AI Exam Generator & Kiến Trúc Chế Độ Kép (Dual Engine Architecture)**:
  - Bổ sung endpoint `POST /api/v1/diagnostic/generate-exam` (FastAPI port 8000) với 2 chế độ sinh đề:
    1. **Google Gemini Live Cloud (`gemini-flash-lite-latest`)**: Gọi trực tiếp Google Gemini API sử dụng cấu trúc `responseSchema` nghiêm ngặt, cho phép AI suy nghĩ và sáng tác 100% câu hỏi mới toanh bám sát prompt, định hướng môn học và phân bổ Bloom (~3-6s).
    2. **Siêu Tốc Calibrated Bank (< 0.1s)**: Tổ hợp câu hỏi chuẩn hóa tâm lý học từ bộ nhớ RAM với phân bổ đáp án đều (A/B/C/D 25%), phục vụ tức thì cho nhu cầu kiểm thử nhanh và demo luồng mà không phụ thuộc độ trễ mạng.
  - Tự động cơ chế phục hồi (Graceful Fallback): Khi Gemini Cloud chạm trần quota hoặc timeout, hệ thống tự động fallback sang kho Calibrated Bank và cảnh báo rõ ràng trên UI.
  - **Cơ Chế Nhận Diện Ý Định & Khắc Phục Xung Đột Lĩnh Vực (Strict Domain Conflict Resolution)**:
    - Tự động phát hiện từ khóa chuyên sâu (cả có dấu và không dấu: `ngữ văn`, `văn học`, `tiếng việt`, `đọc hiểu`, `toán học`, `logic`...) trong câu prompt của giáo viên.
    - Cưỡng chế `domain_id` chuẩn (ví dụ: `Full Ngữ Văn` -> `dom_lang`), vô hiệu hóa tình trạng rơi vào cấu hình mặc định 5 lĩnh vực (`ALL`).
    - Nâng cấp System Prompt cho Gemini với quy chuẩn sư phạm: 100% câu hỏi thuộc đúng môn học được yêu cầu.
    - **Ưu Tiên Tuyệt Đối Lựa Chọn Dropdown Của Giáo Viên**: Chỉ kích hoạt suy luận từ prompt khi dropdown là `ALL`. Nếu chọn môn cụ thể trong dropdown, giữ nguyên 100%.
- **Kiểm Thử Vận Hành**:
  - FastAPI uvicorn port 8000: Kiểm thử cả 2 chế độ Gemini Cloud (`ms: 7245`) và Fast Calibrated (`ms: 0`) trả về 200 OK.
