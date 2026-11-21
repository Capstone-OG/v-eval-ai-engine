# UPDATE - AI ENGINE

## [22/11/2026]
- **Chuẩn Hóa Trọn Bộ Khung Chương Trình Lớp 10 – 11 – 12 (9 Môn GDPT 2018 - Chân Trời Sáng Tạo + Tư Duy Logic & Số Liệu) & Bỏ Đánh Số Cứng `Dạng 1:` (`gdpt2018_dgnl_taxonomy.json`, `PdfPigTextbookParserService.cs`)**:
  - Nâng cấp toàn bộ **339 bài học Lớp 10, 11, 12** trong `gdpt2018_dgnl_taxonomy.json` từ mẫu stub 1:1 thành **750 dạng bài thực tế, không đánh số cứng** (529 dạng Lớp 10–11 + 221 dạng Lớp 12), số lượng dạng bám sát cấu trúc thật từng bài trong bộ SGK Chân trời sáng tạo.
  - Sửa prompt phân tích SGK trong `PdfPigTextbookParserService.cs`: bỏ luật cứng "2 đến 3 dạng bài" và cấm sinh tiền tố `Dạng 1:`, `Dạng 2:` trong dữ liệu gốc (FE tự hiển thị số thứ tự tượng trưng).
  - Đồng bộ FK-safe toàn bộ **750 dạng bài Lớp 10–12** vào `v_eval_content."Skills"` trên Supabase (đổi tên tại chỗ 137 stub, gỡ sạch 192 tiền tố `Dạng X:` trên toàn bảng, thêm mới 612 dạng bài chuẩn).
- **Kiểm Thử**: `dotnet test --no-restore` đạt **65/65 Passed (100%)**.
