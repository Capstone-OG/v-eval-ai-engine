# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [27/09/2026] - Phát Hành Giao Diện Khảo Sát Năng Lực Đầu Vào & Proxy Endpoints Cho Exam Studio

- **Cung Cấp Giao Diện Web Runner Khảo Sát Năng Lực (`/api/ai-engine/view-diagnostic`)**:
  - Bổ sung endpoint `GET /api/ai-engine/view-diagnostic` trong [`ExamEndpoints.cs`](./V-Eval-Ai_Engine.API/Endpoints/ExamEndpoints.cs).
  - Triển khai tệp giao diện [`view-diagnostic.html`](./V-Eval-Ai_Engine.API/wwwroot/view-diagnostic.html) phục vụ trực quan hóa toàn diện bài thi chẩn đoán 30 câu, tính toán Psychometrics và biểu đồ Radar Chart.
  - Tích hợp đầy đủ KaTeX typographic, Chart.js Radar, bộ tạo đề tùy biến AI Studio và bộ lưu trữ LocalStorage.
- **Tối Ưu Luồng Xử Lý & Khắc Phục Lỗi Import `os` (`rag-service/routers/diagnostic.py`)**:
  - Khai báo bổ sung `import os` vào phần thư viện chuẩn tại đầu tệp `routers/diagnostic.py`.
  - Khắc phục triệt để lỗi phân tích cú pháp tĩnh `Could not find name os` trong hàm `_call_gemini_exam_generator` khi đọc biến môi trường Google API Key.
  - Tối ưu luồng kiểm tra biến môi trường và đảm bảo 100% các bài kiểm thử `tests/test_diagnostic.py` vượt qua (`python -m py_compile` biên dịch sạch).
- **Cập Nhật Ma Trận Tiến Độ**:
  - Cập nhật [`docs/process.md`](./docs/process.md) đạt 21 hạng mục theo dõi tiến độ chi tiết.
- **Kiểm Thử Biên Dịch**:
  - `dotnet build` giải pháp `V-Eval-Ai_Engine.sln` thành công 100% (**0 Error**).

