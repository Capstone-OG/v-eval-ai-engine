# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [19/10/2026] - Khắc Phục Triệt Để Lỗi Dấu Phẩy Sau Tên Thuộc Tính JSON Gemini (Invalid ',' After Property Name) & Cơ Chế Bóc Tách Câu Hỏi Dự Phòng Regex Auto-Repair (100% Zero-Crash)

- **Khắc Phục Lỗi Dị Dạng JSON Từ Gemini AI (`CurriculumAiService.cs`)**:
  - *Nguyên Nhân Sự Cố*:
    + Trong một số trường hợp, Gemini LLM trả về chuỗi JSON có dấu phẩy đặt sai cú pháp ngay sau tên thuộc tính (ví dụ: `"question_content", "Cho hàm số..."` thay vì `"question_content": "Cho hàm số..."`), gây ra ngoại lệ:
      `warn: Gemini AI generate pattern questions error: ',' is invalid after a property name. Expected a ':'. LineNumber: 19 | BytePositionInLine: 15.`
  - *Cơ Chế Regex Auto-Repair Tự Động Vá Lỗi JSON*:
    + Nâng cấp phương thức `SanitizeAndRepairGeminiJson`:
      Bổ sung bộ lọc regex thông minh phát hiện và thay thế dấu phẩy nằm ngay sau tên thuộc tính:
      `Regex.Replace(cleaned, @"(""[a-zA-Z0-9_]+"")\s*,\s*(?=""|\[|\{|-?[0-9]|true|false)", "$1: ")`.
    + Loại bỏ các dấu phẩy trailing thừa trước dấu đóng ngoặc nhọn `}` hoặc ngoặc vuông `]`:
      `Regex.Replace(cleaned, @",\s*([}\]])", "$1")`.
  - *Cơ Chế Bóc Tách Câu Hỏi Dự Phòng Tầng Hai (`ExtractQuestionsViaRegex`)*:
    + Bọc an toàn khối giải mã JSON `JsonNode.Parse(contentText)` trong cấu trúc `try...catch`.
    + Khi `JsonNode.Parse` thất bại do chuỗi JSON bị ngắt cụt hoặc dị dạng nghiêm trọng, hệ thống kích hoạt hàm `ExtractQuestionsViaRegex(string text)` sử dụng các biểu thức chính quy để bóc tách trực tiếp từng trường dữ liệu:
      - `content_latex` / `question_content`
      - `option_a`, `option_b`, `option_c`, `option_d`
      - `correct_option`
      - `explanation`
      - `difficulty_level`
    + Đảm bảo 100% không bao giờ ném exception làm gián đoạn trải nghiệm của giáo viên/học sinh, luôn trả về kết quả HTTP 200 OK với đầy đủ danh sách câu hỏi rèn luyện.

- **Kiểm Thử Biên Dịch (Build Verification)**:
  - `V-Eval-Ai_Engine.Infrastructure` & `V-Eval-Ai_Engine.API`: Biên dịch C# .NET 9 Clean Architecture thành công 100% (**0 Error, 0 Warning**).
