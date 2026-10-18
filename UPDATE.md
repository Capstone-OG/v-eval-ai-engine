# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [19/10/2026] - Khắc Phục Triệt Để Lỗi 500 AI Sinh Câu Hỏi (Regex Key Lookbehind & Block Slicing Fallback) & Loại Bỏ Chữ 'text' Thừa Trước Công Thức Hóa Học

- **Khắc Phục Lỗi 500 Khi AI Sinh Câu Hỏi Rèn Luyện (`CurriculumAiService.cs`)**:
  - *Nguyên Nhân Gốc Rễ 1 (Lỗi `':' is invalid after a value`)*:
    + Biểu thức Regex trong `SanitizeAndRepairGeminiJson` trước đây (`@("([a-zA-Z0-9_]+)")\s*,\s*(?="|\[|\{|-?[0-9]|true|false)`) đã vô tình khớp với các cặp giá trị chuỗi như `"correct_option": "B",` và biến đổi thành `"correct_option": "B": "explanation": ...`, khiến bộ giải mã `System.Text.Json` vấp lỗi cú pháp JSON và ném ngoại lệ `Strict JsonNode.Parse failed: ':' is invalid after a value`.
    + *Khắc phục*: Sử dụng Lookbehind dương bắt buộc `(?<=[\{\,]\s*)` để chỉ sửa dấu phẩy khi nó đứng ngay sau tên thuộc tính (thuộc tính luôn bắt đầu sau `{` hoặc `,`), tuyệt đối không đụng vào giá trị (nằm sau `:`).
  - *Nguyên Nhân Gốc Rễ 2 (Bộ Bóc Tách Regex Dự Phòng Bị Tắc Nghẽn Do Dấu Ngoặc Nhọn `{...}` Trong LaTeX)*:
    + Hàm dự phòng `ExtractQuestionsViaRegex` trước đây sử dụng mẫu `\{[^{}]*"(?:contentLatex|content_latex)"[^{}]*\}`. Nhóm ký tự phủ định `[^{}]*` bị ngắt ngay lập tức khi gặp các dấu ngoặc nhọn `{...}` trong công thức LaTeX (ví dụ: `SO_4^{2-}`, `\frac{a}{b}`), dẫn đến số lượng câu hỏi trích xuất trả về 0 và kích hoạt lỗi HTTP 500.
    + *Khắc phục*: Tái cấu trúc cơ chế bóc tách theo thuật toán **Question Block Slicing**: Tìm mốc xuất hiện của khóa `"contentLatex"` / `"content_latex"`, cắt lát các khối JSON theo ranh giới câu hỏi mà không phụ thuộc vào dấu ngoặc nhọn lồng nhau bên trong biểu thức LaTeX.
  - *Bảo Toàn Chuỗi Thoát LaTeX Không Bị Nuốt Thành Ký Tự Tab (`ExtractRegexField`)*:
    + Thay thế việc gọi trực tiếp `.Replace(@"\t", "\t")` bằng Regex bảo vệ `Regex.Replace(val, @"\\t(?![a-zA-Z])", "\t")` và `Regex.Replace(val, @"\\n(?!(?:eq|otin|...)\b)", "\n")`, ngăn chặn việc biến đổi các lệnh LaTeX bắt đầu bằng `\t` (`\text`, `\times`, `\theta`, `\tau`, `\to`) thành ký tự điều khiển ASCII Tab (0x09).
    + Áp dụng `ExtractRegexField` để unescape chuỗi `ContentLatex` trong `ExtractCurriculumViaRegex` thay vì lưu chuỗi raw.

- **Kiểm Thử Biên Dịch (Build Verification)**:
  - `V-Eval-Ai_Engine.Infrastructure` & `V-Eval-Ai_Engine.API`: Biên dịch C# .NET 9 Clean Architecture thành công 100% (**0 Error, 0 Warning**).
