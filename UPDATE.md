# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [12/11/2026] - Chuẩn Hóa Regex NormalizeCurriculumLatex: Bảo Toàn Dấu Đóng Đô La Trước Dấu Câu & Cô Lập Phương Trình Phản Ứng Độc Lập Theo Dòng

- **Bảo Toàn Dấu Đóng Đô La ($) Trước Dấu Chấm Câu ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs))**:
  - **Phát hiện & Bản chất lỗi**:
    - Khi bóc tách dạng bài Hóa học từ tài liệu SGK, chuỗi văn bản chứa công thức có dấu chấm câu liền kề như `$C_{17}H_{33}$.` hoặc `$-CH = CH_2.$`.
    - Regex cũ `Regex.Replace(s, @"(?<=[.,;])\$|\$(?=[.,;])", "")` đã xóa nhầm dấu `$` đóng trước dấu chấm câu, biến `$C_{17}H_{33}$.` thành `$C_{17}H_{33}.`. Hệ quả là dấu mở `$` không được đóng, khiến toàn bộ câu văn tiếng Việt phía sau (ví dụ: `1 mol triolein phản ứng tối đa 3 mol H_2...`) bị nuốt trọn vào math mode KaTeX, hiển thị thành chuỗi chữ nghiêng dính liền không dấu cách.
  - **Giải pháp xử lý triệt để**:
    - Gỡ bỏ hoàn toàn regex xóa dấu `$` trước/sau dấu chấm câu; thay thế bằng cơ chế kiểm tra và cân bằng dấu đô la an toàn `EnsureBalancedDollars(s)` chỉ thêm `$` ở cuối chuỗi nếu số lượng dấu `$` là lẻ.
    - Gỡ bỏ regex `(?<![\$\w])([A-Z][a-zA-Z0-9_+-]+)\$` từng gây chia tách các nhóm chức phân tử `$-OH$` hay `$-COOH$` thành `$-$OH$`, chấm dứt triệt để hiện tượng đảo lộn tính chẵn lẻ của dấu đô la.

- **Cô Lập Phương Trình Phản Ứng Độc Lập Từng Dòng (Line-by-Line Reaction Isolation)**:
  - Chỉ tự động bọc `$$ ... $$` cho các dòng phương trình hóa học độc lập có chứa mũi tên phản ứng (`\xrightarrow`, `\rightleftharpoons`, `\longrightarrow`) khi dòng đó hoàn toàn không chứa văn bản Tiếng Việt, loại bỏ triệt để hiện tượng kẹp nhầm nhiều dòng văn bản vào khối công thức lớn.

- **Kiểm Thử & Đảm Bảo Vận Hành ([`PromptTemplateServiceTests.cs`](./V-Eval-Ai_Engine.Tests/Infrastructure/PromptTemplateServiceTests.cs))**:
  - Bổ sung unit test `NormalizeCurriculumLatex_WithChemistryFormulas_ShouldNotCorruptVietnameseOrDollarPlacement` kiểm chứng toàn diện các trường hợp: công thức hóa học có dấu chấm phía sau (`$C_{17}H_{33}$.`), nhóm chức (`-OH`), phương trình phản ứng mũi tên và bảo vệ tuyệt đối văn bản tiếng Việt giải thích không bị nuốt vào math mode.
  - Toàn bộ **18/18 unit tests** vượt qua thành công 100% trong 415 ms. Solution biên dịch sạch với 0 Error.
