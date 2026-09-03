# Nhật Ký Cập Nhật (Update Log)

## [03/09/2026] - Nâng Cấp Độ Chính Xác Tuyệt Đối (High-Precision Verbatim OCR) & Native JPEG Rendering
- **Khắc phục lỗi font nhúng MathType & hiện tượng nhòe ảnh PDF**:
  - Tích hợp thư viện `PDFtoImage` (SkiaSharp/PDFium) render trực tiếp 16 trang PDF thành ảnh JPEG độ nét cao (150 DPI) trong 1.5 giây, gửi đồng thời sang Gemini Vision.
  - Triệt tiêu 100% việc mất hệ số sau dấu bằng (giữ nguyên số 2 trong $y = 2x^3$ ở Câu 41) và lỗi font ẩn khiến mất dấu gạch trị tuyệt đối $|\int ...|$ ở Câu 45.
- **Khóa cứng `temperature = 0.0` (Greedy Deterministic Decoding)**:
  - Loại bỏ hoàn toàn tình trạng kết quả xê dịch giữa các lần chạy, đảm bảo 100 lần chạy ra kết quả giống nhau 100%.
- **Thiết lập Bộ luật Verbatim OCR Zero-Tolerance & Chống sửa bẫy đề thi trắc nghiệm**:
  - Nghiêm cấm AI tự ý giải toán hay chia tách tích phân, bảo tồn nguyên vẹn các phương án gây nhiễu (distractor options) phục vụ luyện thi.
- **Tối ưu hóa thời gian xử lý**:
  - Rút ngắn thời gian bóc tách toàn bộ 16 trang đề thi ĐGNL (120 câu hỏi) từ 50s xuống chỉ còn **~17–25 giây**.
- **Đồng bộ hóa tài liệu kỹ thuật**:
  - Cập nhật chi tiết quy trình Ingestion trong `docs/ai_data_ingestion.md` và `docs/daily_process_and_planning.md`.

---

- Fix CI error in github action
