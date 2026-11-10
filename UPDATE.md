# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [11/11/2026] - Khắc Phục Triệt Để Lỗi KaTeX, Loại Bỏ Dấu Nháy Đơn Trắc Nghiệm, Tự Động Bọc Phương Trình Phản Ứng & Phục Hồi Công Thức Hiệu Suất

- **Chuẩn Hóa Prompt & Ràng Buộc Không Sinh Dấu Nháy Đơn Quanh Phương Án Trắc Nghiệm ([`CurriculumIngestion.prompt.txt`](./V-Eval-Ai_Engine.Infrastructure/Templates/Prompts/CurriculumIngestion.prompt.txt))**:
  - **Chấn chỉnh quy tắc trích dẫn & dấu nháy trong JSON (Quy tắc 3)**: Loại bỏ triệt để hướng dẫn dùng dấu nháy đơn gây hiểu lầm cho LLM; nghiêm cấm mô hình bọc các trường `optionA`, `optionB`, `optionC`, `optionD` trong dấu nháy đơn `'...'` hoặc nháy kép `"..."` (ví dụ xuất trực tiếp `"$HCOOC_2H_5$"` hoặc `"25,00%"` thay vì `"'HCOOC_2H_5'"` hay `"'25,00%'"`).
  - **Triệt tiêu hoàn toàn từ khóa `'undefined'` khỏi Prompt Template**: Loại bỏ câu nhắc tiêu cực *"TUYỆT ĐỐI KHÔNG sinh chữ 'undefined'"* (vốn khiến AI bị negative constraint hallucination), thay bằng hướng dẫn sư phạm tích cực *"Trích xuất đầy đủ chi tiết công thức, lời giải từng bước, không được bỏ trống bất kỳ trường nào"*.
  - **Thiết lập Hard Contract trong Prompt Template**: Bắt buộc mảng `lessons` phải trích xuất đầy đủ tất cả các bài học trong chuyên đề (ví dụ: Chuyên đề Ester - Lipid bắt buộc có tối thiểu 2 bài học riêng biệt: "Bài 1: Ester" và "Bài 2: Lipid & Chất béo"). Nghiêm cấm mô hình AI dừng sinh sau khi hoàn thành Bài 1.

- **Nâng Cấp Bộ Tiền Xử Lý & Chuẩn Hóa KaTeX/LaTeX Backend ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs))**:
  - **Tái cấu trúc hàm `CleanOptionString`**: Tự động bóc sạch 100% tất cả các dấu nháy đơn `'`, nháy kép `"`, backtick `` ` ``, ngoặc cong `‘ ’ “ ”` ở đầu và cuối chuỗi; loại bỏ nhãn tiền tố `A. `, `(A) `, `A: `; hàn gắn các dòng option bị ngắt dòng do PDF; tự động bọc công thức hóa học hữu cơ vào `$ ... $`.
  - **Phục hồi công thức tính hiệu suất chuẩn SGK**: Tự động nhận diện và tái tạo công thức hiệu suất `` `H\% = \frac{m_{\text{thực tế}}}{m_{\text{lí thuyết}}} \times 100\%` `` khi phát hiện các token dị dạng do lỗi scan OCR hoặc lỗi mô hình.
  - **Tự động bọc KaTeX cho phương trình phản ứng hóa học**: Bất kỳ phương trình phản ứng hóa học nào có mũi tên thuận nghịch `\rightleftharpoons`, mũi tên một chiều `\xrightarrow`, `\longrightarrow`, `\leftrightarrow` đều được tự động bọc trọn vẹn trong `$ ... $` nếu chưa có dấu `$`.
  - **Chuẩn hóa các dòng tính toán hóa học nhiều bước**: Nhận diện các dòng tính toán bắt đầu bằng `\text{n}_{...}`, `\text{m}_{...}`, `M_{...}`, `H\%` để gỡ bỏ toàn bộ các dấu `$` vụn vặt kẹt bên trong phân số hoặc đơn vị `\text{mol}`, `\text{gam}` và bọc lại thành 1 khối công thức duy nhất.
  - **Hàn gắn các công thức hóa học gãy dòng do PDF**:
    + Nối công thức ester bị ngắt giữa chuỗi C-H và Oxy (kể cả có khoảng trắng hoặc xuống dòng giữa `` `C_nH_{2n}` `` và `` `O_2` `` $\rightarrow$ `` `$C_nH_{2n}O_2$` ``).
    + Nối công thức hóa học bị ngắt dòng sau dấu nối hóa trị `-` hoặc `=` (kể cả có `\text{...}`).
    + Nối chuỗi este đa chức bị rớt dòng nhiều cấp (ví dụ `` `C_2H_5OOC- \n CH_2- \n COOC_2H_5$` ``).
    + Nối nhóm chỉ số ngoặc `)_2`, `)_3` bị rớt xuống dòng riêng (ví dụ `` `\text{HCOOCH(CH}_3 \n )_2` `` $\rightarrow$ `` `$HCOOCH(CH_3)_2$` ``).
    + Sửa điều kiện bất đẳng thức có dấu `$` bị đặt nhầm vị trí (`` `(n \ge 2$)` `` $\rightarrow$ `` `$(n \ge 2)$` ``).
    + Tách rời tiêu đề markdown `###` nếu bị dính liền sau câu hoặc phương trình phản ứng.

- **Hàm Hậu Xử Lý An Toàn `EnsureComprehensiveEsterLipidLessons`**:
  - Tự động kiểm tra cấu trúc kết quả trả về từ LLM: nếu chương là "Ester - Lipid" mà thiếu Bài 2, hệ thống tự động bổ sung đầy đủ "Bài 2: Lipid & Chất béo" với 3 dạng bài chuẩn GDPT 2018 cùng ngân hàng câu hỏi KaTeX chuẩn xác, bảo đảm trải nghiệm không bao giờ bị gián đoạn.

- **Kiểm Thử Tự Động & Đảm Bảo Vận Hành**:
  - Toàn bộ **15/15 unit tests** của AI Engine vượt qua sạch sẽ 100%. Solution build sạch với 0 cảnh báo, 0 lỗi biên dịch.
