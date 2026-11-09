# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [10/11/2026] - Sửa Lỗi JSON Deserialization Bóc Tách Khung Tri Thức (HF-AI-05), Nâng Cấp SanitizeAndRepairGeminiJson & Ưu Tiên Gemini Flash-Lite

- **Khắc Phục Lỗi Deserialization JSON Bóc Tách Khung Tri Thức Hóa Học (Thẻ [`HF-AI-05`](../../docs_noi_bo/hotfix/AI_Engine_Hotfix.md)) ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`CurriculumIngestion.prompt.txt`](./V-Eval-Ai_Engine.Infrastructure/Templates/Prompts/CurriculumIngestion.prompt.txt))**:
  - **Phát hiện & Bản chất lỗi**:
    - Khi kiểm thử thực tế với tệp PDF scan chuyên sâu môn Hóa học 55 trang (`Bài 1 - ĐA.pdf`), Multimodal Vision trích xuất 18 ảnh scan và gửi sang Gemini thành công.
    - Tuy nhiên bộ giải mã `JsonSerializer.Deserialize<ImportCurriculumRequestDto>` gặp lỗi cú pháp nghiêm trọng:
      ``'l' is an invalid start of a property name. Expected a '"'. Path: $.chapters[0].lessons[0].patterns[0] | LineNumber: 16 | BytePositionInLine: 64.``
    - Nguyên nhân: Trong văn bản Hóa học, LLM trích xuất danh pháp hoặc tên chất có dấu ngoặc kép không escape (ví dụ: `"isoamyl axetat", là este...`), dấu ngoặc kép sau tên chất đi liền với dấu phẩy khiến bộ làm sạch hiểu nhầm là đóng thuộc tính JSON; từ tiếng Việt bắt đầu bằng chữ `'l'` (`là este...`, `lời giải...`) bị đưa ra ngoài chuỗi và parse thành tên thuộc tính không hợp lệ. Đồng thời schema trong template có chứa chú thích kiểu C-style (`/* LƯU Ý... */`) khiến AI sinh kèm comment vào JSON.
    - Dù Regex Fallback Engine đã cứu được dữ liệu thật nhưng tổng thời gian xử lý bị kéo dài lên tới 150.9 giây (150898 ms).
  - **Giải pháp xử lý triệt để**:
    - **Làm sạch Prompt Template**: Dời toàn bộ chú thích ra ngoài schema JSON trong `CurriculumIngestion.prompt.txt`; thêm chỉ thị nghiêm cấm sinh comment `/*...*/`, `//...` và bắt buộc dùng nháy đơn `'...'` cho tên chất, thuật ngữ.
    - **Nâng cấp `SanitizeAndRepairGeminiJson`**:
      + Tự động xóa bỏ toàn bộ chú thích C-style `/\*[\s\S]*?\*/` và single-line `//.*` trước khi quét token.
      + Loại bỏ trailing commas trước dấu đóng ngoặc `}` và `]`.
      + Bộ phân tích ngữ cảnh thông minh: Phân biệt chính xác giữa dấu ngoặc kép đóng thuộc tính (phải đi trước `:`, hoặc `,` mà sau đó là tên thuộc tính kế tiếp `"[a-zA-Z0-9_]+":` hoặc dấu đóng ngoặc) với dấu ngoặc kép trích dẫn trong văn bản (chuyển đổi an toàn thành nháy đơn `'...'`).
      + Bật cấu hình `ReadCommentHandling = JsonCommentHandling.Skip` và `CommentHandling = JsonCommentHandling.Skip` cho toàn bộ các lần gọi `JsonSerializer.Deserialize` và `JsonNode.Parse`.
  - **Tối ưu hóa Độ trễ & Triệt tiêu HTTP 503**:
    - Đảo thứ tự danh sách mô hình AI trong `appsettings.json` và hàm `ResolveGeminiModels()`: Đưa `gemini-3.5-flash-lite` lên vị trí số 1, tiếp theo là `gemini-3.8-flash` và `gemini-3.6-flash`.
    - Giúp giảm thời gian trích xuất tài liệu scan 18 ảnh từ ~150s xuống <15s và tránh tình trạng quá tải HTTP 503 của model 3.6-flash.
  - **Kiểm thử tự động & Nghiệm thu**:
    - Bổ sung 2 unit tests trong [`PromptTemplateServiceTests.cs`](./V-Eval-Ai_Engine.Tests/Infrastructure/PromptTemplateServiceTests.cs):
      + `SanitizeAndRepairGeminiJson_WithUnescapedQuotesInChemistryProse_RepairsToValidJson`: Kiểm thử sửa ngoặc kép trích dẫn trong văn bản Hóa học.
      + `SanitizeAndRepairGeminiJson_WithCStyleAndLineComments_StripsCommentsAndParsesSuccessfully`: Kiểm thử xóa comment C-style và line comment.
    - Toàn bộ **15/15 unit tests** của AI Engine vượt qua sạch sẽ 100%.
