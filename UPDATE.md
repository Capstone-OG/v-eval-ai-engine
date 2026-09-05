# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [05/09/2026] - Trích Xuất & Kết Xuất Hình Ảnh PDFium Cục Bộ, Xử Lý Sơ Đồ Đa Bước & Quy Tắc Chống Lộ Đáp Án
- **Kết xuất & Trích xuất Hình ảnh Cục bộ Siêu tốc (PDFium + SkiaSharp + PdfPig)**:
  - Định vị chính xác vùng Bounding Box của hình ảnh bằng `UglyToad.PdfPig`.
  - Sử dụng `PDFtoImage` (Google PDFium Core C++) kết xuất trực tiếp vùng đồ thị/hình ảnh từ trang PDF ở độ phân giải cao 150 DPI trên nền trắng tinh khiết (`SKColors.White`).
  - **Khắc phục triệt để lỗi mất hình (FlateDecode) ở Câu 75**: Xuất ra tệp PNG hoàn chỉnh 100%, bảo toàn nguyên vẹn hệ trục tọa độ $a-x$, các mốc số liệu ($40, -40, 1, -1$) và gốc tọa độ $O$.
  - **Khắc phục triệt để lỗi mảng đen & mất chữ (Soft Mask / SMask) ở Chùm 106–108**: Tự động nhận diện chuỗi thí nghiệm liên hoàn (Hình A và Hình B), bao bọc toàn bộ lề an toàn 20pt, loại bỏ 100% các khối đen xì và thu trọn vẹn các nhãn chữ Word bên lề ("tán", "thân", "gốc", "A. crenulata", "A. mediterranea", "Tế bào ghép hoàn chỉnh 1 & 2") thành một tệp hình ảnh duy nhất (`p14_combined.png`).
  - Tự động lọc bỏ Logo tiêu đề trường ĐHQG-HCM ở Trang 1 và các pixel icon rác (<50px).
  - Tốc độ xử lý: **0.15 giây**, chạy 100% Offline cục bộ, 0 token overhead, không phụ thuộc mạng Cloud.
- **Quy tắc Chống Lộ Đáp Án (Zero-Spoiler Multimodal Prompt Rule 6)**:
  - Thiết lập quy tắc bảo mật đề thi trong System Prompt: Cấm tuyệt đối AI sử dụng từ khóa hoặc tên gọi trùng khớp với đáp án đúng trắc nghiệm trong phần dẫn câu hỏi.
  - Sửa dứt điểm lỗi Câu 78 tự ghi lộ *"Gương cầu lồi"*; thay bằng mô tả trung tính: *"([Hình vẽ]: Thiết bị dạng mặt gương gắn tại khúc cua đường đèo)"*.
- **Nâng cấp Giao diện Web Viewer (`view-exam.html`)**:
  - Tự động hiển thị hình ảnh minh họa cho câu hỏi và chùm bài đọc với giao diện Dark-mode sang trọng, bo góc và đổ bóng mượt mà.
  - **Image Lightbox Modal**: Tích hợp modal phóng to hình ảnh toàn màn hình khi nhấp chuột để soi rõ từng chi tiết tọa độ.
  - **Phím tắt Dán ảnh Nhanh (`Ctrl + V`)**: Hỗ trợ giáo viên dán trực tiếp ảnh chụp màn hình từ Clipboard vào câu hỏi đang chọn.
