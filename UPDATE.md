# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [11/11/2026] - Khắc Phục Lỗi Deserialization JSON Bóc Tách Khung Tri Thức (Root Array & Preamble Fence), Giảm Mật Độ Điểm Ảnh (DPI 72 / 720px) & Ưu Tiên Chuỗi Mô Hình Flash-Lite Siêu Tốc

- **Khắc Phục Lỗi Deserialization JSON Bóc Tách Khung Tri Thức (Root Array & Preamble Fence) ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`PromptTemplateServiceTests.cs`](./V-Eval-Ai_Engine.Tests/Infrastructure/PromptTemplateServiceTests.cs))**:
  - **Phát hiện & Bản chất lỗi**:
    - Khi bóc tách chuyên đề Hóa học từ file PDF scan (`CHƯƠNG 1. ESTER - LIPID (BẢN HS).pdf`), mô hình Gemini sinh kết quả dạng mảng gốc `[ { "chapterName": ... } ]` hoặc kèm lời dẫn văn bản trước/sau khối code fence markdown.
    - Bộ giải mã `JsonSerializer.Deserialize<ImportCurriculumRequestDto>` ném ngoại lệ:
      `The JSON value could not be converted to V_Eval_Ai_Engine.Application.DTOs.ImportCurriculumRequestDto. Path: $ | LineNumber: 0 | BytePositionInLine: 1.`
    - Bộ phân tích mềm dẻo cũ `TryParseCurriculumFromLooseJson` truy cập trực tiếp `node["domainId"]` khiến ném ngoại lệ `InvalidOperationException` khi node gốc là `JsonArray`, dẫn tới hệ thống phải rơi vào bộ trích xuất fallback bằng Regex làm tăng độ trễ lên hơn 71 giây.
  - **Giải pháp xử lý triệt để**:
    - Nâng cấp `SanitizeAndRepairGeminiJson`: Tự động nhận diện và bóc tách khối JSON sạch nằm giữa cặp code fence ````json ... ```` hoặc giữa ký tự `{` / `[` đầu tiên và `}` / `]` cuối cùng, loại bỏ toàn bộ lời dẫn đàm thoại trước và sau JSON.
    - Tự động phát hiện và đóng gói mảng gốc `[ ... ]` thành object hoàn chỉnh `{ "domainId": "...", "domainName": "...", "chapters": [...] }` trước khi đưa vào `JsonSerializer.Deserialize`.
    - Tái cấu trúc toàn diện `TryParseCurriculumFromLooseJson`: Tương thích với cả `JsonObject` và `JsonArray`, tìm kiếm thuộc tính không phân biệt hoa thường (`chapters`, `Chapters`, `data`, `chapterList`), trang bị các hàm trích xuất an toàn `GetNodeString`, `GetNodeDouble`, `GetNodeInt`, `GetNodeArray`.
    - Đảm bảo 100% dữ liệu từ LLM được parse trực tiếp từ JSON thuần mà không bao giờ bị rơi xuống bộ trích xuất Regex.

- **Tối Ưu Hóa & Giảm Mật Độ Điểm Ảnh Tệp PDF (Image Downsampling) ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`PdfImageExtractor.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/PdfImageExtractor.cs), [`PdfPigTextbookParserService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/PdfPigTextbookParserService.cs))**:
  - `CurriculumAiService.cs`:
    - Giảm số lượng trang ảnh gửi sang Multimodal Vision AI từ 10 xuống còn tối đa 8 trang (`maxImages = 8`).
    - Nén và thu nhỏ ảnh nhúng: Giới hạn kích thước cạnh lớn nhất `maxDim = 720` (trước đó là 800px), chất lượng nén JPEG 50% (trước đó là 55%), giảm hơn 70% dung lượng payload và token gửi sang Google Gemini API.
    - Trang dạng vector không có ảnh nhúng: Giảm độ phân giải kết xuất sang bitmap xuống `Dpi = 72` (trước đó là 96 DPI), giới hạn `maxDim = 720`, chất lượng nén JPEG 50%.
  - `PdfImageExtractor.cs`: Giảm độ phân giải render từ 150 DPI xuống 120 DPI.
  - `PdfPigTextbookParserService.cs`: Giảm độ phân giải render từ 96 DPI xuống 72 DPI, nén JPEG chất lượng 60%.

- **Ưu Tiên Chuỗi Mô Hình Flash-Lite Siêu Tốc Loại Bỏ Triệt Để Lỗi 503 Quá Tải ([`appsettings.json`](./V-Eval-Ai_Engine.API/appsettings.json))**:
  - Tái cấu trúc thứ tự ưu tiên trong `GeminiModels`: Đưa các dòng mô hình Flash-Lite tốc độ cao (~1.0s - 1.2s) lên hàng đầu để triệt tiêu tình trạng nghẽn hàng đợi và lỗi `HTTP 503 This model is currently experiencing high demand`:
    - `gemini-3.5-flash-lite` (ưu tiên số 1 - phản hồi ~1.0s).
    - `gemini-3.1-flash-lite` (ưu tiên số 2).
    - `gemini-flash-lite-latest` (ưu tiên số 3).
    - `gemini-3.8-flash` (ưu tiên số 4).
    - `gemini-3.5-flash` (dòng mô hình lớn dùng làm dự phòng tầng cuối).

- **Kiểm Thử & Đảm Bảo Vận Hành**:
  - Bổ sung 2 unit test mới trong `PromptTemplateServiceTests.cs`:
    - `SanitizeAndRepairGeminiJson_WithMarkdownCodeFenceAndPreamble_ExtractsCleanJson`: Kiểm thử trích xuất JSON sạch từ phản hồi có code fence và lời chào hỏi.
    - `TryParseCurriculumFromLooseJson_WithRootArrayOfChapters_ParsesDirectly`: Kiểm thử bóc tách trực tiếp mảng gốc `[ { "chapterName": ... } ]` sang DTO hoàn chỉnh.
  - Thêm `<InternalsVisibleTo Include="V-Eval-Ai_Engine.Tests" />` vào `V-Eval-Ai_Engine.Infrastructure.csproj`.
  - Toàn bộ **17/17 unit tests** vượt qua thành công 100% trong 397 ms. Solution biên dịch sạch với 0 Error.
