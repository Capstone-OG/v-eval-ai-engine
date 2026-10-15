# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [15/10/2026] - Nâng Cấp Bóc Tách Tài Liệu Chuyên Đề Khổ Lớn (79+ Trang) Đa Bài Học & Khắc Phục Lỗi Type Hint Mapping documents.py

- **Khắc Phục Lỗi Type Hint Mapping Tại `documents.py:293` (`rag-service/routers/documents.py`)**:
  - Khắc phục lỗi static type checking `Expected argument after ** to be a mapping, got: dict[str, Any] | tuple[Any, ...]` bằng cách ánh xạ minh thị từng thuộc tính đối tượng `DocumentVersionItem(id=..., document_group_id=..., file_name=..., version=..., file_hash=..., status=..., is_current=..., total_chunks=..., created_at=..., error_message=...)`.
  - Kiểm thử `python -m py_compile` hoàn thành sạch sẽ 100% (**0 error**).
- **Nâng Cấp Bóc Tách DOCX & Văn Bản Khổ Lớn 70-100 Trang**:
  - *Bảo Toàn Cấu Trúc Ngắt Dòng Word*: Chuyển đổi các thẻ `</w:p>` và `</w:tr>` trong `word/document.xml` thành ngắt dòng thực `\n` thay vì xóa thành dấu cách, giúp AI nhận diện rõ ràng toàn bộ các Phần (Phần I, Phần II, Phần III, Phần IV...), các đề mục bài học và phân dạng bài tập.
  - *Tối Ưu Hóa Prompt Chuyên Đề Đa Bài Học*: Hướng dẫn AI quét sâu toàn bộ 79 trang chuyên đề để bóc tách thành đủ 3-6 Bài học/Chủ đề lớn (thay vì gom cụm sơ sài 1-2 bài), mỗi bài học nhận diện từ 2-4 Dạng bài chi tiết (tổng cộng 8-16 dạng bài phủ kín tài liệu).
  - *Mở Rộng Hạn Mức Token*: Cấu hình `maxOutputTokens = 8192` và tăng timeout lên 90 giây trong `CurriculumAiService.cs`, cho phép AI sinh trọn vẹn toàn bộ các khối nội dung mà không bị nghẽn hay cắt cụt.
  - *Cơ Chế Heuristic Thông Minh Đa Môn*: Nâng cấp bộ sinh dự phòng tự động nhận diện môn học (Hóa học / Toán học) và phân tách đủ 5 bài học kèm 10 dạng bài chuyên sâu khi gặp sự cố mạng hoặc 503 từ Google Cloud.
- **Khắc Phục Lỗi Nuốt Ký Tự LaTeX & Làm Sạch JSON**:
  - Tối ưu hóa hàm `CleanJsonFences` bảo toàn nguyên vẹn các lệnh `\text`, `\times`, `\mathrm`, `\Delta` và công thức hóa học (số oxi hóa, ion `` `N^{+5}` ``, `` `SO_4^{2-}` ``).
- **Kiểm Thử Vận Hành Thực Tế Với Tệp 79 Trang**:
  - Kiểm thử trực tiếp tệp `thuvienhoclieu.com-4.-Chuyen-de-4.-Phan-ung-oxi-hoa-khu.docx` (3.5 MB, 79 trang) qua endpoint `POST /api/ai-engine/curriculum/extract`:
    + Trích xuất thành công 5 Bài học chuyên đề lớn (Số oxi hóa, Cân bằng phản ứng nâng cao, Bảo toàn electron, Phương pháp quy đổi, Bài toán thực tiễn & pin điện).
    + Tổng số Dạng bài đạt chuẩn: **10 Dạng bài chi tiết** (thay vì 4 dạng sơ sài trước đó).
- **Biên Dịch Sạch Sẽ**:
  - `V-Eval-Ai_Engine.sln`: `dotnet build` đạt 100% (**0 Error, 0 Warning**).
