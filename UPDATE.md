# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [13/11/2026] - Khảo Sát Live Ping Mô Hình Gemini, Tối Ưu Thứ Tự Ưu Tiên 503, Khử Trùng Lặp Dạng Bài & Nâng Cấp Chuẩn Hóa KaTeX/MathText

- **Khảo Sát Thực Nghiệm Live Ping Toàn Diện Trên 4 API Keys**:
  - Live ping 14 mô hình Gemini: `gemini-3.1-flash-lite` đạt **100% 200 OK trên cả 4 keys**, độ trễ siêu nhanh **~808ms - 882ms**, không dính lỗi 503 Spike hay Rate Limit.
  - Tối ưu chuỗi ưu tiên trong `appsettings.json` ([`appsettings.json`](./V-Eval-Ai_Engine.API/appsettings.json)): `gemini-3.1-flash-lite` -> `gemini-flash-lite-latest` -> `gemini-3.5-flash` -> `gemini-3.6-flash`.
  - Kết quả bóc tách PDF thực tế qua Gemini hoàn thành chỉ trong **9.09s**, tổng thời gian cấu trúc C# **12.39s**.

- **Khử Trùng Lặp Dạng Bài (Deduplication) & Tự Động Đánh Số Thứ Tự Tuần Tự ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs))**:
  - Loại bỏ hoàn toàn lỗi duplicate dạng bài (như hai thẻ `Dạng 4: Bài toán đốt cháy ester...` trong cùng bài học).
  - Tự động gộp ngân hàng câu hỏi từ dạng bài trùng và xóa thẻ thừa.
  - Đánh số tuần tự chuẩn xác: `Dạng 1: ...`, `Dạng 2: ...`, `Dạng 3: ...` cho toàn bộ bài học.

- **Nâng Cấp Chuẩn Hóa KaTeX / LaTeX Backend ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs))**:
  - Tự động khôi phục mã lệnh đơn vị rụng gạch chéo: `textmol`, `textgam`, `textg`, `textkg` -> `\text{mol}`, `\text{gam}`.
  - Mở rộng nhận diện phương trình và toán tử liên kết: `\implies`, `>`, `<`, `\ge`, `\le`, `\approx`, `\equiv`, `\rightleftharpoons`, `\xrightarrow`.
  - Khắc phục lỗi rách công thức trước suy luận (như `$n_{...} = 0,2$ textmol \implies $m_{...} = 17,6$ textgam`).
  - Tự động bọc biến số đứng trước đơn vị đo: `([amVnxy])\s+(mol|gam|g|kg|lít|lit|ml)` -> `$1 $2`.
  - Chuẩn hóa chỉ số dưới tiếng Việt cho hóa học: `k_{gốc}` -> `k_{\text{gốc}}`, `m_{dd}` -> `m_{\text{dd}}`.
  - Kiểm thử `CurriculumLatexNormalizationTests` đạt **14/14 tests pass 100%**. Tổng số test AI Engine: **32/32 tests pass 100%**.

- **Vận Hành Trực Tiếp Qua Terminal (Không Dùng Docker)**:
  - Khởi chạy trực tiếp `V-Eval-Ai_Engine.API` bằng `dotnet run` trên cổng `http://localhost:5104`.

- **Khắc Phục Lỗi KaTeX Còn Sót (Mũ `^`, Ngoặc Đơn, Chỉ Số Dưới Tiếng Việt) ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`MathText.jsx`](../V-Eval-Web_Client/src/components/common/MathText.jsx))**:
  - `fullEqPattern` hỗ trợ số mũ (`2^2`, `3^{2}`) và ngoặc đơn (`\times (3 \times 40 - 92)`), không còn tách `2^$2`; ngoặc không cân bằng/bao trọn biểu thức được đưa ra ngoài `$...$`.
  - Không còn can thiệp vào bên trong cặp `$...$` đã có; bước giải phóng văn bản tiếng Việt bỏ qua nội dung `_{...}` / `^{...}` nên `m_{chất béo}` không còn bị gỡ `$`.
  - Thêm test hồi quy `Regression_NoBareOrBrokenMath` (8 ca từ ảnh lỗi thực tế); tổng **42/42 tests pass**.

- **Khắc Phục Lỗi KaTeX Trần Do Web Client Gỡ `$` Nhầm (Lần 2)**:
  - Nguyên nhân gốc: bộ phục hồi trong [`MathText.jsx`](../V-Eval-Web_Client/src/components/common/MathText.jsx) dò từ tiếng Việt (`chất`, `muối`...) ngay cả bên trong `\text{...}` và `_{...}`, nên gỡ `$` của các công thức hợp lệ như `$n_{\text{NaOH}} = 3 \times n_{\text{chất béo}}$`. Nay chỉ dò phần ngoài `\text{}`/`_{}`/`^{}`.
  - Bộ nhận diện phương trình không còn nuốt xuống dòng kế tiếp (chỉ `[ \t]` giữa các vế); chỉ số dưới cho phép ngoặc đơn (`m_{muối (xà phòng)}`).
  - Kiểm chứng JS bằng Node trên các ca ảnh lỗi và 49/49 unit tests C# pass.
