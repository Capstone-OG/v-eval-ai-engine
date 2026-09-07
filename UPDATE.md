# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [07/09/2026] - Khôi Phục Hiển Thị Chart.js, Hỗ Trợ Bảng Tiêu Đề 2 Tầng & Phân Tuyến Cắt Ảnh
- **Khôi Phục & Tối Ưu Hóa Chart.js & HTML Table Tương Tác**:
  - Khắc phục lỗi biểu thức chính quy (Regex) trong `view-exam.html` đối với `Biểu đồ cột` và `Biểu đồ tròn/hình tròn`: Xử lý triệt để trường hợp nhãn số liệu chứa dấu đóng ngoặc đơn (ví dụ: `Đầu tư (20%)`), cho phép bóc tách trọn vẹn toàn bộ các danh mục và giá trị phần trăm thay vì bị ngắt sớm.
  - Khôi phục cơ chế render biểu đồ tương tác trực quan bằng Canvas Chart.js (với cột dữ liệu, bảng màu Dark-mode và nhãn số liệu trên đỉnh cột/lát bánh) và bảng số liệu định dạng HTML `<table>` viền phát sáng như các bản phát hành chuẩn trước đây.
- **Tự Động Nhận Diện & Tái Tạo Bảng Tiêu Đề 2 Tầng (2-Tier Nested Header with Colspan/Rowspan)**:
  - Nâng cấp thuật toán `convertMarkdownTableToHtml`: Tự động nhận diện cấu trúc tiêu đề cha - con (ví dụ: `Số giờ chiếu sáng vào ban đêm (giờ): 0,5` kèm các cột con `1, 2, 3, 4, 5` trong Chùm câu 109–111) và tự động dựng lại `<thead>` 2 tầng chuẩn mực với `colspan="6"` và `rowspan="2"`.
  - Giữ trọn vẹn tiêu đề cha bao phủ toàn bộ các cột dữ liệu con, sửa triệt để tình trạng các cột số liệu bị trơ trọi mất ngữ cảnh.
  - Cập nhật System Prompt trong `GeminiExamParserService`: Hướng dẫn trích xuất bảng 2 tầng qua thẻ HTML `<table>` chuẩn mực có `rowspan` và `colspan`.
  - Tinh chỉnh CSS `.exam-table`: Kẻ viền ô dạng lưới `1px solid`, căn giữa theo phương dọc (`vertical-align: middle`) và phương ngang, căn trái cột danh mục đầu tiên.
- **Phân Tuyến Thông Minh Bộ Trích Xuất Hình Ảnh (`PdfImageExtractor.cs`)**:
  - Bổ sung bộ lọc `isChartOrTable`: Loại trừ toàn bộ các đoạn văn hoặc câu hỏi dạng biểu đồ thống kê và bảng số liệu khỏi quy trình gắn ảnh cắt từ PDF.
  - **Chỉ cắt ảnh cho đồ thị giải tích & hình ảnh thực tế / thí nghiệm**:
    - Đồ thị tọa độ dao động điều hòa $a-x$ (Câu 75).
    - Hình chụp thiết bị thực tế, dụng cụ gương cong (Câu 78).
    - Chuỗi sơ đồ thí nghiệm đa bước sinh học / hóa học (Chùm câu 106–108).
  - Tránh triệt để việc sinh ra các ảnh cắt thừa cho biểu đồ số liệu, tối ưu hóa giao diện người dùng và bảo đảm độ nét vector 100%.
- **Đồng Bộ Giao Diện Web Viewer (`view-exam.html`)**:
  - Ưu tiên hiển thị Canvas Chart.js và HTML Table khi phát hiện cấu trúc biểu đồ hoặc bảng số liệu.
  - Ẩn khung hiển thị ảnh tĩnh nếu nội dung câu hỏi/chùm bài đã được render dưới dạng biểu đồ hoặc bảng số liệu tương tác.
