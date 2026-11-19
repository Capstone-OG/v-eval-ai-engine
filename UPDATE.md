# UPDATE - V-Eval-Ai_Engine

## [19/11/2026]
- **Sửa Lỗi Khớp Sai Khối Lớp (Grade 11 -> Grade 10), Loại Bỏ Stub Placeholder & Hỗ Trợ Dấu `hoá`/`hóa` (`./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumTaxonomyRegistry.cs`, `./V-Eval-Ai_Engine.Tests/Infrastructure/CurriculumTaxonomyRegistryTests.cs`)**:
  - *Sửa lỗi lọc khối lớp trong `GetStandardSkeletonChapters`*: Ưu tiên khớp chính xác `grade` trước khi fallback sang lớp 10/12, khắc phục triệt để lỗi yêu cầu Hóa học Lớp 11 bị trả về 7 chương của Hóa học Lớp 10 (`Tốc độ phản ứng hoá học`, `Halogen`).
  - *Xóa bỏ chuỗi stub mặc định*: Trả về `string.Empty` cho `Description` và `Method` thay vì chèn `"Mục tiêu nhận thức và kỹ năng áp dụng cho ..."` và `"Áp dụng phương pháp sư phạm chuẩn SGK GDPT 2018."`.
  - *Nhận diện dấu thanh tiếng Việt cũ/mới*: Cập nhật `DetectDocumentIdentity` và `DetectGradeNumber` hỗ trợ cả `hoá học` và `hóa học`; bổ sung 2 unit test hồi quy trong `CurriculumTaxonomyRegistryTests.cs`.
- **Nâng Cấp Động Cơ Quét Mục Lục SGK & Khắc Phục Lỗi Sót Bài Học (`./V-Eval-Ai_Engine.Infrastructure/Services/PdfPigTextbookParserService.cs`)**:
  - Cải tiến prompt Gemini Vision ép buộc trích xuất đầy đủ các bài học con trong mỗi Chương kết hợp Lesson Guard C# tự động backfill bài học chuẩn từ `gdpt2018_dgnl_taxonomy.json` khi `totalParsedLessons < 10` hoặc trung bình `` `\le 1.2` `` bài/chương (sách `` `\ge 25` `` trang).
- **Đồng Bộ Ma Trận Tri Thức GDPT 2018 (`./V-Eval-Ai_Engine.Infrastructure/Templates/Taxonomy/gdpt2018_dgnl_taxonomy.json`)**:
  - Bao phủ đầy đủ 139 Chương và 337 Bài học theo chuẩn mục lục 17 SGK Chân trời sáng tạo trên cả 4 miền tri thức.
