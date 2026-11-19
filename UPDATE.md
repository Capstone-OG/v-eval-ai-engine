# UPDATE - V-Eval-Ai_Engine

## [20/11/2026]
- **Nâng Cấp Động Cơ Parse Đề PDF Sang Page-Batched Gemini Vision AI + Tự Giải Đáp Án Đồng Bộ 100% Với SGK (`MultiCloudExamParserService.cs`, `PdfPigLocalExamParser.cs`, `IExamParserService.cs`, `ExamEndpoints.cs`, `PdfPigTextbookParserService.cs`, `PdfPigLocalExamParserTests.cs`)**:
  - **Đồng bộ 100% model & payload SGK**: Dùng đúng `AiSettings:GeminiModels` trên `generativelanguage.googleapis.com/v1beta`, vòng lặp model ngoài / xoay vòng key trong (`GetRotatedKeys`), loại bỏ `thinkingConfig` & nested `responseSchema` gây HTTP 400 trên model Lite, và tự lọc bỏ khối `thought: true` (`ExtractTextPartsIgnoringThoughts`).
  - **Page-Batched Vision 2 Trang/Cụm (115 DPI, 1400px, JPEG 78%) & AI Tự Giải Đáp Án**: Chạy song song 2 cụm kèm báo tiến trình trực tiếp (`onProgress`), đọc chuẩn 100% công thức KaTeX (`\sqrt`, `\frac`, `\begin{cases}`, `\int`, `\lim`, `\log`), bảng Markdown/HTML, bảng trọng số sơ đồ mạng lưới, gạch chân tiếng Anh `<u>...</u> (A)`, **bỏ qua nét khoanh bút học sinh** để tự giải chọn đúng `correct_option` (`A/B/C/D`), viết `explanation` kèm KaTeX và phân bậc Bloom `difficulty_level` (`1–6`).
  - **Ghép chùm liên trang & Dự phòng thuần C# không cần `pymupdf`**: `MergeBatchExamResults` ghép tự động các chùm đọc hiểu trải qua nhiều trang; `PdfPigLocalExamParser` (`UglyToad.PdfPig`) dự phòng riêng theo từng cụm trang nếu Cloud lỗi, chấm dứt 100% lỗi `ModuleNotFoundError: No module named 'pymupdf'`.
- **Triển Khai Endpoint AI Phân Loại Hàng Loạt Câu Hỏi Đề Thi Vào Cây Node Khung Chương Trình (`POST /api/ai-engine/exams/classify-nodes`) (`ExamNodeClassificationDtos.cs`, `ExamEndpoints.cs`, `ExamNodeClassificationTests.cs`)**:
  - Phân lô 30 câu/lượt qua Gemini + fallback từ vựng tiếng Việt `ClassifyQuestionLexically`.
- **Kiểm Thử & Biên Dịch**: `dotnet build --no-restore` thành công (`0 Error`), `dotnet test --no-build` pass `5/5` (`PdfPigLocalExamParserTests` 3/3 + `ExamNodeClassificationTests` 2/2).
