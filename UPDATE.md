# UPDATE - V-Eval-Ai_Engine

## [19/11/2026]
- **Nâng Cấp Động Cơ Quét Mục Lục SGK & Khắc Phục Triệt Để Lỗi Sót Bài Học (`./V-Eval-Ai_Engine.Infrastructure/Services/PdfPigTextbookParserService.cs`)**:
  - *Ràng Buộc Đa Bài Học (Multi-Lessons Contract)*: Cải tiến prompt Gemini Vision ép buộc trích xuất đầy đủ 100% tất cả các bài học con (Bài 1, Bài 2, Bài 3, Bài 4...) trong mỗi Chương, nghiêm cấm việc chỉ lấy 1 bài đại diện hoặc gộp bài.
  - *Cung Cấp Schema JSON Mẫu Đa Bài Học*: Bổ sung ví dụ chương chứa nhiều bài học (`Bài 1`, `Bài 2`...) giúp LLM noi theo cấu trúc chuẩn xác và không bị "nghẽn token".
  - *Triển Khai Lesson Guard C#*: Khi AI parse ra tổng số bài học quá thấp (`totalParsedLessons < 10` hoặc trung bình $\le 1.2$ bài/chương đối với sách $\ge 25$ trang), hệ thống tự động đối soát và hòa nhập (backfill/merge) toàn bộ các bài học chuẩn còn thiếu từ `gdpt2018_dgnl_taxonomy.json` vào từng chương, bảo toàn trọn vẹn 10–25 bài học cho toàn bộ cuốn sách.
- **Đồng Bộ Hoàn Chỉnh Ma Trận Tri Thức GDPT 2018 Toàn Diện (`gdpt2018_dgnl_taxonomy.json`)**:
  - Mở rộng toàn diện 139 Chương và 337 Bài học theo chuẩn mục lục 17 SGK Chân trời sáng tạo trên cả 4 miền tri thức (Toán 26 chương, KHTN 50 chương, Ngôn ngữ 33 chương, KHXH 30 chương).
- **Kiểm Thử & Biên Dịch Sạch**:
  - `dotnet build`: Succeeded 100%, 0 Error, 0 Warning.
