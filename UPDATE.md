# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [07/09/2026] - Sửa Lỗi Hiển Thị Công Thức Vật Lý & Hóa Học, Tái Tạo Delta (\Delta) & Bảng Số Liệu 2 Tầng
- **Khắc Phục Triệt Để Lỗi Công Thức Vật Lý & Hóa Học (Câu 93 & Đoạn Văn Truyền Tải Điện)**:
  - **Khôi phục ký tự thoát JSON cho LaTeX**: Sửa lỗi JSON Deserialization nuốt các ký tự điều khiển ASCII biến `\frac` thành `rac` (form-feed), `\times` thành `	imes` (tab), `\text` thành `	ext`, `\bar` thành `ar`.
  - **Sửa lỗi nhận diện ký hiệu Delta ($\Delta$)**: Khắc phục hiện tượng OCR nhìn nhầm ký hiệu tam giác biến thiên $\Delta$ thành `ar{ ext{A}}` hoặc `\bar{\text{A}}`. Tự động khôi phục về $\Delta[\text{Acetone}]$, $\Delta t$, $R = -\frac{\Delta[\text{Acetone}]}{\Delta t}$.
  - **Tự động bọc dấu `$...$` cho công thức**: Các công thức Vật lý / Hóa học dài chưa có dấu đô-la (như $H = \frac{P_n - P_{hp}}{P_n} \times 100\%$, $P_{hp}$, $P_n$, $k^2$) và các đơn vị có số mũ âm ($2,33 \cdot 10^{-3}\text{ mol}\cdot\text{l}^{-1}\cdot\text{phút}^{-1}$) được tự động bọc thẻ KaTeX để hiển thị đúng chuẩn typographic Toán - Lý - Hóa.
  - **Chống lỗi nhân đôi chữ khi Copy từ KaTeX**: Bổ sung CSS `user-select: none` cho `.katex-mathml`, khắc phục tình trạng copy công thức bị dính 2 lần (như `k 2 k 2` hoặc `P h p P hp`).
  - **Bổ sung Bộ Lọc C# Backend (`GeminiExamParserService.cs`)**: Thêm hàm `SanitizeJsonForLatex` xử lý chuỗi JSON trước khi deserialize và `CleanExamDto` làm sạch toàn bộ DTO trước khi trả về.
- **Tự Động Nhận Diện & Tái Tạo Bảng Tiêu Đề 2 Tầng (2-Tier Nested Header with Colspan/Rowspan)**:
  - Nâng cấp thuật toán `convertMarkdownTableToHtml`: Tự động nhận diện cấu trúc tiêu đề cha - con (như `Số giờ chiếu sáng vào ban đêm (giờ): 0,5` đi cùng các cột `1, 2, 3, 4, 5` trong Chùm câu 109–111) và tự động dựng lại `<thead>` 2 tầng chuẩn mực với `colspan="6"` và `rowspan="2"`.
  - Tinh chỉnh CSS `.exam-table`: Kẻ viền ô dạng lưới `1px solid`, căn giữa theo phương dọc (`vertical-align: middle`) và phương ngang, căn trái cột danh mục đầu tiên.
- **Khôi Phục & Tối Ưu Hóa Chart.js & HTML Table Tương Tác**:
  - Khắc phục lỗi Regex trong `view-exam.html` đối với `Biểu đồ cột` và `Biểu đồ hình tròn`: Xử lý triệt để trường hợp nhãn số liệu chứa dấu đóng ngoặc đơn (ví dụ: `Đầu tư (20%)`), bóc tách trọn vẹn tất cả danh mục và số liệu phần trăm.
- **Phân Tuyến Thông Minh Bộ Trích Xuất Hình Ảnh (`PdfImageExtractor.cs`)**:
  - Bổ sung bộ lọc `isChartOrTable`: Loại trừ toàn bộ các biểu đồ cột/tròn hoặc bảng số liệu khỏi việc gán ảnh tĩnh cắt từ PDF; chỉ cắt ảnh cho đồ thị tọa độ $a-x$ (Câu 75), hình chụp thực tế (Câu 78) và sơ đồ thí nghiệm (Chùm 106–108).
