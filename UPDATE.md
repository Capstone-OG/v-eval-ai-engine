# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [12/11/2026] - Khảo Sát Bể 4 Khóa Gemini, Ưu Tiên Chuỗi Mô Hình Gemini-3.6-Flash Chống 503 & Chuẩn Hóa Phương Trình Hóa Học Backend

- **Khảo Sát Thực Tế Bể 4 Khóa Gemini & Tái Cấu Trúc GeminiModels ([`appsettings.json`](./V-Eval-Ai_Engine.API/appsettings.json))**:
  - **Kiểm thử trực tiếp trên cả 4 API keys (`GEMINI_KEY_1` đến `GEMINI_KEY_4`)**:
    - `gemini-3.6-flash`: **HTTP 200 OK** trên cả 4/4 keys (phản hồi siêu tốc, độ ổn định tuyệt đối).
    - `gemini-3.5-flash-lite`: **HTTP 200 OK** trên cả 4/4 keys (~1.0s).
    - `gemini-3.5-flash`: **HTTP 200 OK** trên cả 4/4 keys.
    - `gemini-3.1-flash-lite`: **HTTP 200 OK** trên cả 4/4 keys.
    - `gemini-flash-lite-latest`: **HTTP 200 OK** trên cả 4/4 keys.
    - `gemini-3.8-flash`: **HTTP 503 Service Unavailable / Timeout** trên cả 4 keys (xác nhận chính xác nguyên nhân gây chậm và lỗi 503 như log của người dùng).
  - **Cập nhật danh sách mô hình ưu tiên**:
    - Đưa `gemini-3.6-flash` lên làm mô hình chủ lực ưu tiên số 1.
    - Tiếp nối bởi `gemini-3.5-flash-lite`, `gemini-3.5-flash`, `gemini-3.1-flash-lite`, `gemini-flash-lite-latest`.
    - Loại bỏ triệt để `gemini-3.8-flash` khỏi danh sách xoay vòng.

- **Nâng Cấp Backend NormalizeCurriculumLatex: Tách Nhãn Phương Trình & Chuẩn Hóa Công Thức Phức ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs))**:
  - Bổ sung cơ chế bóc tách nhãn dẫn `Phương trình: ...` hoặc `PTHH: ...`, đưa phương trình hóa học có mũi tên thành display math `$$...$$` độc lập.
  - Sửa lỗi khoảng trắng sau dấu gạch dưới `_\s+(\d+)` biến `(OH)_ 3` thành `(OH)_3`.
  - Tự động bọc công thức có chỉ số dưới `C_{15}H_{31}COOH` thành `$C_{15}H_{31}COOH$`.
  - Ghép nối công thức ester bị ngắt dòng `$C_nH_{2n}\nO_2` thành `$C_nH_{2n}O_2$`.
  - Cân bằng dấu `$` an toàn từng dòng `EnsureBalancedDollars(s)` và bảo vệ dấu `$` trước dấu chấm câu (`$C_{17}H_{33}$.`).

- **Kiểm Thử & Đảm Bảo Vận Hành ([`PromptTemplateServiceTests.cs`](./V-Eval-Ai_Engine.Tests/Infrastructure/PromptTemplateServiceTests.cs))**:
  - Bổ sung unit test `NormalizeCurriculumLatex_WithLabeledReactionAndSpacedSubscripts_NormalizesCleanly`.
  - Toàn bộ **19/19 unit tests** trong AI Engine vượt qua thành công 100% trong 294 ms.
  - Rebuild và triển khai container `v_eval_ai_engine` trên Docker.
