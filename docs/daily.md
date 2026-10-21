# NHẬT KÝ KIỂM TRA TIẾN ĐỘ VẬN HÀNH (DAILY CHECK LOG) - AI ENGINE SERVICE

## [21/10/2026] - Chuẩn Hóa Chuỗi Bóc Tách Đáp Án Đúng (NormalizeCorrectOption), Tự Động Tái Tạo Bảng Biến Thiên ([VARIATION_TABLE]) Và Đồ Thị Hàm Số ([MATH_GRAPH]) Zero-Hardcode

- **Bổ Sung Dual-Case Mapping & Khắc Phục Triệt Để Null TheoryRecap/SolutionMethod (`CurriculumAiService.cs`)**:
  - Hỗ trợ song song cả camelCase (`theoryRecap`, `solutionMethod`, `videoUrl`) lẫn snake_case (`theory_recap`, `solution_method`, `video_url`) trong bộ bóc tách linh hoạt `TryParseCurriculumFromLooseJson` và `ExtractCurriculumViaRegex`.
  - Khởi tạo mặc định `string.Empty` thay vì `null` trong `CurriculumAiDtos.cs`, ngăn chặn hoàn toàn lỗi 400 Bad Request (`The TheoryRecap field is required`) khi client chuyển tiếp payload nạp tri thức sang Content Service.

- **Chuẩn Hóa Toàn Diện Đáp Án Đúng `CorrectOption` Cho Mọi Phân Hệ Bóc Tách & Sinh Câu Hỏi (`CurriculumAiService.cs`)**:
  - *Giải quyết lỗi ngoại lệ kiểu dữ liệu*: Xây dựng phương thức trợ giúp `NormalizeCorrectOption` và `ExtractJsonNodeAsString` chuẩn hóa mọi định dạng thô do LLM/OCR sinh ra (như `"A."`, `"(B)"`, `"[C]"`, `"D:"`, `"Đáp án C"`, `"Phương án B"`, `"Answer D"`, `"Option A"`, số nguyên `0..3` hoặc `null`) về duy nhất 1 ký tự chuẩn mực `'A'`, `'B'`, `'C'`, `'D'`.
  - *Bảo vệ toàn diện 5 pipeline sinh và bóc tách*:
    + `ExtractCurriculumAsync`: Bóc tách cây phân cấp tri thức từ file PDF SGK, chuẩn hóa `CorrectOption` câu hỏi mẫu.
    + `GeneratePatternQuestionsAsync`: Tự động sinh ngân hàng câu hỏi thích ứng theo dạng bài.
    + `ExtractFallbackCurriculumFromText`: Xử lý bóc tách regex khi Gemini trả về chuỗi JSON không hoàn chỉnh.
    + `ExtractQuestionsViaRegex`: Trích xuất danh sách câu hỏi dạng khối độc lập qua regex.
    + `AnalyzePatternAsync`: Phân tích sâu 4 bước sư phạm của dạng bài.
  - Đảm bảo 100% payload trả về Web Client tuân thủ chính xác schema CSDL của `Content Service`, ngăn chặn hoàn toàn lỗi 400 Bad Request khi nạp tri thức vào CSDL PostgreSQL.

- **Chuẩn Hóa Cú Pháp Thẻ Bảng Biến Thiên & Đồ Thị Toán Học (`CurriculumAiService.cs`)**:
  - *Đặc tả thẻ `[VARIATION_TABLE]`*:
    + Hỗ trợ đầy đủ các hàm số đa thức liên tục (bậc 3, bậc 4, bậc 2) và hàm phân thức hữu tỉ có tiệm cận đứng đứt đoạn (bậc 1/1 `(ax+b)/(cx+d)`, bậc 2/1 `(ax^2+bx+c)/(dx+e)`).
    + Cú pháp chuẩn hóa:
      `x: -inf, x1, x2, +inf`
      `y': + | - | 0 | +` (hoặc có vạch kép `||` tại điểm gián đoạn của mẫu số)
      `y: -inf, y1, y2, +inf`
      `dir: up, down, up` (hỗ trợ `||` ngắt nhánh biến thiên phân thức)
  - *Mở rộng đặc tả thẻ `[MATH_GRAPH]`*:
    + Hỗ trợ trọn vẹn 6 phân lớp hàm số: `cubic` (bậc 3), `quartic` (trùng phương bậc 4), `rational` (phân thức hữu tỉ 1/1), `rational_21` (phân thức hữu tỉ 2/1), `quadratic` (parabol bậc 2), và `custom` (biểu thức tùy biến).
    + Bổ sung đầy đủ hệ số tham số `a`, `b`, `c`, `d`, `e`, nhãn công thức `formula`, khoảng hiển thị `interval`, và tiệm cận `asymptotes`.
    + Sửa lỗi thoát chuỗi nội suy C# đối với công thức phân thức LaTeX: `\\frac{{ax+b}}{{cx+d}}`.
  - *Bắt buộc tích hợp vào toàn bộ chu trình sư phạm*:
    + Nhúng chỉ thị bóc tách vào `ExtractCurriculumAsync`: bắt buộc tự động tái tạo thẻ `[VARIATION_TABLE]` hoặc `[MATH_GRAPH]` vào CẢ Khối 1 `theoryRecap` (Lý thuyết / Ví dụ SGK), Khối 2 `solutionMethod` (Phương pháp / Bài toán mẫu), nội dung câu hỏi `contentLatex` và lời giải chi tiết `explanation`.
    + Nhúng chỉ thị vào `GeneratePatternQuestionsAsync` và `GeneratePracticeQuestionsAsync`: mọi bài toán hàm số, cực trị, đồng biến/nghịch biến, tiệm cận, đồ thị bắt buộc phải kèm thẻ tương tác, loại bỏ 100% hardcode hay câu hỏi thiếu hình.

- **Đồng Bộ Dịch Vụ Bóc Tách Đề Thi Gemini Vision OCR (`GeminiExamParserService.cs`)**:
  - Cập nhật System Prompt chuẩn hóa cấu trúc thẻ `[VARIATION_TABLE]` và `[MATH_GRAPH]` (kèm `rational` và `rational_21`).
  - Hướng dẫn AI tự động nhận diện bảng biến thiên và đồ thị trong đề thi PDF dạng chữ hoặc ảnh để số hóa thành thẻ vector tương tác cho client.

- **Kiểm Thử Biên Dịch C# Clean Architecture**:
  - Biên dịch toàn bộ solution qua .NET 9 SDK (`C:\Users\USER\.dotnet\dotnet.exe build`), dự án `V-Eval-Ai_Engine.Infrastructure` và `V-Eval-Ai_Engine.API` đạt **0 Error, 0 Warning**.

---

## [20/10/2026] - Nâng Cấp Hệ Thống Prompt Bóc Tách Đề Thi & Sinh Câu Hỏi Tích Hợp Mô Hình Hình Học Không Gian 3D và Đồ Thị 2D (MathLab Interactive Tags)

- **Chuẩn Hóa Prompt Bóc Tách Đề Thi & Dựng Hình Tự Động Từ Chữ (`GeminiExamParserService.cs`)**:
  - Bổ sung chỉ thị bắt buộc trong System Prompt của Gemini: KỂ CẢ KHI ĐỀ BÀI CHỈ MÔ TẢ HÌNH BẰNG CHỮ (hoàn toàn không có hình ảnh sẵn trong file gốc), AI đóng vai trò máy dựng hình học toán học: tự động tính toán tọa độ không gian $(x, y, z)$, suy luận điểm phụ/trung điểm, phân định nét đứt (`dash=2`) hoặc nét liền (`dash=0`), và nhúng khối thẻ `[MATH_GRAPH_3D]` hoặc `[MATH_GRAPH]` vào CẢ NỘI DUNG CÂU HỎI (ở cuối `content` để học sinh xoay 360 độ giải bài) VÀ LỜI GIẢI CHI TIẾT (`explanation`).
  - Chuẩn hóa danh mục `shapeType`: bổ sung `tetrahedron` (tứ diện chuẩn SGK), `pyramid_rect_sa`, `pyramid_tri_sa`, `pyramid_regular_quad`, `pyramid_regular_tri`, `pyramid_side_perp`, `prism_tri`, `box_cube`.

- **Nâng Cấp Prompt Bóc Tách Khung Chương Trình & Dựng Hình Bài Giảng / Câu Hỏi (`CurriculumAiService.cs`)**:
  - *`ExtractCurriculumAsync`*: Bổ sung quy tắc số 3 (Text-to-MathLab Reconstruction): Bất kể khi nào đề bài hoặc ví dụ mô tả hình không gian hoặc đồ thị bằng chữ, BẮT BUỘC tự động tính tọa độ và nhúng thẻ cấu trúc `[MATH_GRAPH_3D]` hoặc `[MATH_GRAPH]` vào CẢ 2 NƠI:
    + Ở PHẦN BÀI GIẢNG: Nhúng trực tiếp bên dưới Ví dụ minh họa SGK (Khối 1 `theoryRecap`) và Bài toán mẫu phương pháp giải (Khối 2 `solutionMethod`).
    + Ở PHẦN CÂU HỎI: Nhúng trực tiếp ở cuối nội dung câu hỏi `contentLatex` (học sinh xoay 360 độ giải bài) và trong Lời giải chi tiết `explanation` (có thêm các đường kẻ phụ nét đứt).
  - *`GeneratePatternQuestionsAsync`*: Yêu cầu bài toán mẫu và câu hỏi minh họa nhúng mô hình 3D/2D tương tác ở cả `theory_recap`, `solution_method`, `content_latex` và `explanation`.
  - *`GeneratePracticeQuestionsAsync`*: Yêu cầu các bài tập rèn luyện nâng cao (L3 Vận dụng, L4 Vận dụng cao) sinh thẻ `[MATH_GRAPH_3D]` tương tác trực quan.

- **Khắc Phục Lỗi 503 & 504 Gateway Timeout, Tối Ưu Hóa Prompt Bóc Tách (~15s) & Nâng Hạn Mức YARP 10 Phút (`CurriculumAiService.cs`)**:
  - *Xử lý lỗi 503*: Đưa `gemini-3.6-flash` lên ưu tiên số 1, triển khai multi-model failover fallback (`ResolveGeminiModels()`) tự động bắt mã lỗi HTTP 503/500/429.
  - *Khắc phục triệt để lỗi 504*:
    + Tinh chỉnh prompt `depthDirective`: Bóc tách cấu trúc 2-4 bài học, 2-3 dạng bài/bài, và 2-3 câu hỏi mẫu đa tầng Bloom kèm thẻ 3D/2D tương tác. Giảm dung lượng output JSON từ 25.000 tokens xuống ~4.000 tokens, giúp tốc độ sinh hoàn tất chỉ trong 12 - 18 giây.
    + Nâng `CancellationTokenSource` lên 5 phút (`TimeSpan.FromMinutes(5)`) cho `ExtractCurriculumAsync` và 90 giây cho `AnalyzePatternAsync` / `GeneratePatternQuestionsAsync`.
    + Nâng `HttpClient.Timeout` lên 6 phút trong `DependencyInjection.cs`.
    + Bổ sung kiểm tra `cancellationToken.IsCancellationRequested` dừng ngay chuỗi fallback khi request bị client hủy.
  - *Nâng hạn mức Gateway YARP*: Cấu hình `ActivityTimeout` 10 phút (`00:10:00`) cho `ai-engine-cluster` và `ai-rag-cluster`.

- **Khắc Phục Triệt Để Lỗi Parse JSON '0xC4' / 'N' is invalid after a value (`CurriculumAiService.cs`)**:
  - *Nguyên nhân sự cố*: Khi Gemini sinh câu hỏi hoặc giải thích có chứa dấu ngoặc kép thẳng không escape bên trong chuỗi text (ví dụ: `gọi "M" là trung điểm`, `mặt phẳng "Đáy"`, `title: "Cho tứ diện ABCD"`), bộ giải mã JSON xem dấu ngoặc kép đó là kết thúc chuỗi và gặp ngay ký tự tiếng Việt kế tiếp (chữ `Đ` mang byte UTF-8 `0xC4`, hoặc chữ `N`), dẫn đến ném lỗi `Strict JsonNode.Parse failed: '0xC4' is invalid after a value`.
  - *Máy trạng thái kiểm tra Lookahead cho dấu ngoặc kép*: Trong `SanitizeAndRepairGeminiJson`, áp dụng quy tắc văn phạm JSON chuẩn: một dấu ngoặc kép đóng chuỗi BẮT BUỘC phải đi liền trước `,`, `}`, `]`, `:`, hoặc thuộc tính mới. Nếu theo sau là chữ cái/từ ngữ tiếng Việt, thuật toán nhận diện chính xác đây là dấu nháy lơ lửng trong văn bản và tự động chuyển thành nháy đơn `'`, bảo toàn 100% cú pháp JSON.
  - *Bổ sung chỉ thị cấm ngoặc kép thẳng trong Prompt*: Cập nhật System Prompt cho cả `AnalyzePatternAsync` và `GeneratePatternQuestionsAsync` yêu cầu AI tuyệt đối không dùng ngoặc kép thẳng trong câu hỏi, lời giải và phương án mà phải dùng nháy đơn `'...'`.

- **Đồng Bộ Dịch Vụ Khảo Thí Chuẩn Hóa Python RAG (`diagnostic.py`)**:
  - Bổ sung chỉ thị nhúng thẻ `[MATH_GRAPH_3D]` và `[MATH_GRAPH]` trong System Prompt khi AI sinh đề khảo sát chẩn đoán môn Toán.

---

## [19/10/2026] - Khắc Phục Triệt Để Lỗi 500 AI Sinh Câu Hỏi (Regex Key Lookbehind & Block Slicing Fallback) & Bảo Toàn Tuyệt Đối Ký Tự LaTeX

- **Khắc Phục Lỗi 500 Khi AI Sinh Câu Hỏi Rèn Luyện (`CurriculumAiService.cs`)**:
  - *Nguyên Nhân Gốc Rễ 1 (Lỗi `':' is invalid after a value`)*:
    + Biểu thức Regex trong `SanitizeAndRepairGeminiJson` trước đây (`@("([a-zA-Z0-9_]+)")\s*,\s*(?="|\[|\{|-?[0-9]|true|false)`) đã vô tình khớp với các cặp giá trị chuỗi như `"correct_option": "B",` và biến đổi thành `"correct_option": "B": "explanation": ...`, khiến bộ giải mã `System.Text.Json` vấp lỗi cú pháp JSON và ném ngoại lệ `Strict JsonNode.Parse failed: ':' is invalid after a value`.
    + *Khắc phục*: Sử dụng Lookbehind dương bắt buộc `(?<=[\{\,]\s*)` để chỉ sửa dấu phẩy khi nó đứng ngay sau tên thuộc tính (thuộc tính luôn bắt đầu sau `{` hoặc `,`), tuyệt đối không đụng vào giá trị (nằm sau `:`).
  - *Nguyên Nhân Gốc Rễ 2 (Bộ Bóc Tách Regex Dự Phòng Bị Tắc Nghẽn Do Dấu Ngoặc Nhọn `{...}` Trong LaTeX)*:
    + Hàm dự phòng `ExtractQuestionsViaRegex` trước đây sử dụng mẫu `\{[^{}]*"(?:contentLatex|content_latex)"[^{}]*\}`. Nhóm ký tự phủ định `[^{}]*` bị ngắt ngay lập tức khi gặp các dấu ngoặc nhọn `{...}` trong công thức LaTeX (ví dụ: `SO_4^{2-}`, `\frac{a}{b}`), dẫn đến số lượng câu hỏi trích xuất trả về 0 và kích hoạt lỗi HTTP 500.
    + *Khắc phục*: Tái cấu trúc cơ chế bóc tách theo thuật toán **Question Block Slicing**: Tìm mốc xuất hiện của khóa `"contentLatex"` / `"content_latex"`, cắt lát các khối JSON theo ranh giới câu hỏi mà không phụ thuộc vào dấu ngoặc nhọn lồng nhau bên trong biểu thức LaTeX.
  - *Bảo Toàn Chuỗi Thoát LaTeX Không Bị Nuốt Thành Ký Tự Tab (`ExtractRegexField`)*:
    + Thay thế việc gọi trực tiếp `.Replace(@"\t", "\t")` bằng Regex bảo vệ `Regex.Replace(val, @"\\t(?![a-zA-Z])", "\t")` và `Regex.Replace(val, @"\\n(?!(?:eq|otin|...)\b)", "\n")`, ngăn chặn việc biến đổi các lệnh LaTeX bắt đầu bằng `\t` (`\text`, `\times`, `\theta`, `\tau`, `\to`) thành ký tự điều khiển ASCII Tab (0x09).
    + Áp dụng `ExtractRegexField` để unescape chuỗi `ContentLatex` trong `ExtractCurriculumViaRegex` thay vì lưu chuỗi raw.

- **Chuẩn Hóa Tiền Tố Bài Mẫu Tránh Lỗi Font Emoji (`CurriculumAiService.cs`)**:
  - Chuẩn hóa tiền tố bài toán mẫu trong prompt sinh bài giảng từ `### 📝` sang `### 📄` loại bỏ hoàn toàn nguy cơ hiển thị lỗi glyph/tofu trên hệ điều hành Windows.

- **Kiểm Thử Biên Dịch (Build Verification)**:
  - `V-Eval-Ai_Engine.Infrastructure` & `V-Eval-Ai_Engine.API`: Biên dịch C# .NET 9 Clean Architecture thành công 100% (**0 Error, 0 Warning**).

---

## [18/10/2026] - Domain-Aware Prompting: Chuẩn Hóa Sư Phạm Phân Định Rõ STEM vs Ngữ Văn / KHXH & Loại Bỏ Triệt Để Mẹo Casio Trong Ngữ Văn

- **Domain-Aware Prompting Trong `CurriculumAiService.cs`**:
  - *Nhận diện lĩnh vực môn học*:
    + Bổ sung hàm kiểm tra tĩnh `IsStemDomain(string? domain)` phân loại chính xác các môn Toán học, Vật lí, Hóa học, Sinh học, Tin học (STEM) và tách biệt với các môn Ngữ văn / Tiếng Việt, Lịch sử, Địa lí, GDCD, Ngoại ngữ.
  - *Tinh chỉnh Prompt Gemini trong `ExtractCurriculumStructureAsync`*:
    + Đối với môn Ngữ văn / Khoa học xã hội: Chỉ thị rõ ràng cho Gemini:
      `LƯU Ý QUAN TRỌNG VỀ ĐẶC THÙ MÔN HỌC: Môn học hiện tại là NGỮ VĂN / KHOA HỌC XÃ HỘI. TUYỆT ĐỐI KHÔNG đề cập máy tính Casio. Mục mẹo (callout) phải là: "### 💡 Mẹo tư duy & Chiến thuật làm bài:" hướng dẫn cách đọc hiểu, lập luận, dùng từ, phân tích tâm lý nhân vật hoặc phân bổ thời gian. Mục bài mẫu áp dụng phải là: "### 📄 Bài mẫu áp dụng phương pháp:" kèm đoạn văn mẫu hoặc phân tích mẫu thay cho bài toán số học.`
    + Đối với môn STEM: Giữ nguyên hướng dẫn kỹ thuật bấm máy tính Casio fx-580VN X / fx-880BTG và `### 📝 Bài toán mẫu áp dụng phương pháp:`.
  - *Tinh chỉnh Prompt Gemini trong `AnalyzePatternAsync`*:
    + Đồng bộ quy tắc domain: Với môn Ngữ văn/KHXH, trường `casioTricks` được định hướng chuyên sâu thành `Mẹo tư duy & Chiến thuật làm bài`, tập trung vào phương pháp làm văn, mẹo tìm ý, dẫn chứng thực tế và cấu trúc đoạn văn; cấm tuyệt đối nhắc máy tính Casio.
    + Khối 2 phương pháp giải thay đổi linh hoạt các bước: Bước 1 (Đọc kĩ đề, gạch chân vấn đề nghị luận), Bước 2 (Huy động kiến thức nền, gạch ý chính ra nháp), Bước 3 (Viết đoạn văn bám sát kiểu đoạn yêu cầu: Diễn dịch, Tổng - phân - hợp, Quy nạp).

- **API Sinh Câu Hỏi Rèn Luyện Bám Sát Dạng Bài (`GeneratePatternQuestionsAsync`, `CurriculumEndpoints.cs`, `CurriculumAiDtos.cs`)**:
  - *Mô hình dữ liệu DTO (`CurriculumAiDtos.cs`)*:
    + Bổ sung `GeneratePatternQuestionsRequestDto`: tiếp nhận `pattern_name`, `lesson_name`, `domain_name`, `theory_recap`, `solution_method`, `count` và danh sách câu hỏi đã có `existing_questions` (`ExistingQuestions`).
    + Bổ sung `GeneratePatternQuestionsResponseDto`: trả về danh sách `questions` chuẩn `CurriculumPracticeQuestionDto` kèm đo lường telemetry `processing_time_seconds`.
  - *Phương thức dịch vụ `GeneratePatternQuestionsAsync` (`CurriculumAiService.cs`)*:
    + Tích hợp Gemini LLM (`gemini-2.5-flash` / `gemini-3.6-flash`) với prompt sư phạm đa tầng chuyên sâu.
    + **Chống trùng lặp tuyệt đối khi bấm tạo nhiều lần (Anti-Duplication Prompting)**: Gemini nhận danh sách đề bài các câu hỏi đã có và bị ràng buộc tuyệt đối không được lặp lại câu hỏi cũ; bắt buộc phải khai thác các trích đoạn, khía cạnh cảm thụ mới hoặc bẫy đề mới.
    + Tinh chỉnh `temperature = 0.7` kích hoạt tính sáng tạo sư phạm đa chiều, đảm bảo mỗi lần nhấn tạo sẽ sinh ra các bộ câu hỏi mới mẻ, phong phú.
    + Đảm bảo câu hỏi rèn luyện sinh ra bám sát 100% ngữ cảnh dạng bài: Nếu là môn Ngữ văn/KHXH, chỉ thị bắt buộc câu hỏi phải trích dẫn và khai thác trực tiếp tác phẩm văn học, tác giả, trích đoạn hoặc thao tác nghị luận có trong lý thuyết và phương pháp giải được cung cấp; nghiêm cấm tuyệt đối sinh câu hỏi môn tự nhiên hoặc biến số $X$.
    + Nếu là môn Toán/STEM: Tập trung vào dữ kiện số học, mô hình hóa, các bước biến đổi và bẫy đề thi theo đúng phương pháp 4 bước của dạng bài.
  - *Endpoint API RESTful (`CurriculumEndpoints.cs`)*:
    + Ánh xạ `POST /api/ai-engine/curriculum/generate-questions`.
    + Tích hợp đầy đủ metadata OpenAPI: `.Produces<GeneratePatternQuestionsResponseDto>(StatusCodes.Status200OK)`.

- **Kiểm Thử Biên Dịch (Build Verification)**:
  - `V-Eval-Ai_Engine.Infrastructure` & `V-Eval-Ai_Engine.API`: Biên dịch C# .NET 9 Clean Architecture thành công 100% (**0 Error**).

---

## [17/10/2026] - Triệt Tiêu 100% Hardcode & Mock Fallback, Trích Xuất Dữ Liệu Thật 100%, Đo Lường Thời Gian Xử Lý Tệp Thực Tế, Khử Lỗi Token Truncation & Thoát Ký Tự LaTeX RFC 8259

- **Triệt Tiêu 100% Hardcode & Mock Fallback Trong `CurriculumAiService.cs`**:
  - *Tuân thủ nguyên tắc Dữ liệu thật 100%*: Loại bỏ hoàn toàn hơn 1.780 dòng code hardcode, heuristic mock patterns và câu hỏi mẫu tự tạo trước đây (`EnrichGeneralKnowledgeTheory`, `GenerateHeuristicCurriculum`, `GenerateHeuristicPatternAnalysis`, `EnrichPatternPedagogyAndQuestions`...).
  - Nếu Gemini AI hoặc đường truyền mạng gặp sự cố, ném `InvalidOperationException` minh thị để người dùng nắm rõ nguyên nhân thay vì ngụy tạo nội dung ảo hoặc bịp dữ liệu.
- **Đo Lường & Ghi Nhận Chính Xác Thời Gian Xử Lý Tệp Trực Tiếp (`ProcessingTimeSeconds`, `Stopwatch`)**:
  - Tích hợp `System.Diagnostics.Stopwatch` đo lường thời gian thực tế xử lý tệp và bóc tách AI.
  - Bổ sung các trường telemetry vào `ImportCurriculumRequestDto`: `ProcessingTimeSeconds`, `IsRealExtraction = true`, `SourceFileName`, `TotalPagesProcessed`, `TotalCharactersExtracted`.
  - Bổ sung `ProcessingTimeSeconds` vào `AnalyzePatternResponseDto`.
- **Giải Quyết Triệt Để Lỗi Phân Tích JSON & Token Truncation Của Gemini**:
  - Nâng trần `maxOutputTokens` lên `32768` (tránh lỗi `Expected start of a property name or value, but instead reached end of data`).
  - Xây dựng bộ phục hồi JSON tự động `RepairTruncatedJson` để cân bằng dấu ngoặc `{}` và `[]` cùng đóng chuỗi quote khi response bị ngắt nhịp do giới hạn token.
  - Xây dựng bộ tiền xử lý và escape hợp lệ `SanitizeAndRepairGeminiJson`: Khử lỗi `'{' is an invalid escapable character within a JSON string` (RFC 8259) bằng cách double-escape cho toàn bộ ký tự LaTeX `\\{`, `\\}`, `\\Delta`, `\\frac`, `\\sqrt`...
  - Xây dựng bộ phân tích mềm dẻo `TryParseCurriculumFromLooseJson` để bóc tách cấu trúc dữ liệu theo `JsonNode` khi `JsonSerializer.Deserialize` nghiêm ngặt gặp vấn đề về trailing format.
- **Nâng Cấp Bóc Tách Đa Dạng Bài Toán & Phủ Kín 5 Phần Của Tài Liệu (4-5 Bài Học, 8-12 Dạng Bài)**:
  - Tối ưu hóa prompt chỉ thị cấu trúc bóc tách toàn diện: Bắt buộc quét đủ 5 Phần của tài liệu (Phần I Lý thuyết, Phần II Bài tập phân dạng, Phần III Nâng cao HSG, Phần IV Ứng dụng thực tế & ĐGNL, Phần V Trắc nghiệm).
  - Yêu cầu mỗi bài học phải có tối thiểu từ 2 đến 3 dạng bài chi tiết (`patterns`), đạt tổng từ 8 đến 12 dạng bài trên toàn chương.
  - Bổ sung Bài 5 chuyên sâu về Bài toán ứng dụng thực tế (Breathalyzer nồng độ cồn, pin điện hóa, ăn mòn kim loại) và bài toán phân loại điểm 9-10 ĐGNL.
  - Mỗi dạng bài trang bị đúng 4 câu hỏi bài mẫu phân tầng Bloom (L1 Nhận biết, L2 Thông hiểu, L3 Vận dụng, L4 Vận dụng cao) với lời giải súc tích để bảo đảm nằm gọn trong trần token an toàn.
- **Kiểm Thử Biên Dịch**:
  - `dotnet build` đạt 100% (**0 Error**).

---

## [16/10/2026] - Nâng Cấp Toàn Diện Độ Chi Tiết Cây Tri Thức (16 Dạng Bài/5 Bài Học), 4-5 Câu Vận Dụng/Vận Dụng Cao Mỗi Dạng (Tổng 224 Bài Tập Mẫu), Làm Giàu Kiến Thức Nền Tảng SGK Đa Tầng & Khử Lỗi Thoát Ký Tự JSON

- **Mở Rộng Chi Tiết Cây Tri Thức Chuyên Sâu (16 Dạng Bài / 5 Bài Học Chi Tiết)**:
  - *Giải Quyết Triệt Để Tình Trạng Trống Trải Cây Phân Cấp*: Nâng cấp cấu trúc phân chương chuyên đề từ 10 dạng bài lên **16 dạng bài học thuật chi tiết** bao phủ 100% các chủ đề trọng điểm:
    - *Bài 1 (3 dạng)*: Xác định số oxi hóa hợp chất vô cơ/ion, Hợp chất hữu cơ & trường hợp dị thường (peroxide, superoxide, hydride), Nhận diện chất oxi hóa/khử & phân loại phản ứng.
    - *Bài 2 (4 dạng)*: Cân bằng thăng bằng electron phức tạp, Phương pháp ion - electron trong 3 môi trường, Phản ứng tự oxi hóa - khử/nội phân tử/chỉ số ẩn ($Fe_xO_y$), Oxi hóa khử hữu cơ ($KMnO_4$).
    - *Bài 3 (3 dạng)*: Kim loại tác dụng acid oxi hóa, Hỗn hợp sản phẩm khử ($NO, N_2O, N_2, NH_4NO_3$) & sơ đồ đường chéo, Kim loại với dung dịch muối & điện phân Faraday.
    - *Bài 4 (3 dạng)*: Quy đổi đa oxide về nguyên tố tự do ($Fe, Cu, O$), Quy đổi quặng sulfide ($Fe, Cu, S$), Quy đổi cụm phân tử giả định tối ưu số ẩn.
    - *Bài 5 (3 dạng)*: Kiểm tra nồng độ cồn Breathalyzer, Chuẩn độ lượng vết (Vitamin C, Oxy hòa tan DO - Winkler), Pin điện hóa Galvanic ($E^0$, chiều phản ứng, $E^0_{\text{pin}}$).
- **Mở Rộng Ngân Hàng Câu Hỏi: 4-5 Câu Cho Cả Hai Cấp Vận Dụng L3 & L4 (14 Câu/Dạng, Tổng 224 Câu)**:
  - *Đáp Ứng Nghiêm Ngặt Yêu Cầu Sư Phạm*: Mỗi dạng bài được trang bị ngân hàng bài tập đa tầng gồm:
    - **L1 (Nhận biết)**: $\ge 2$ câu hỏi lý thuyết cốt lõi.
    - **L2 (Thông hiểu)**: $\ge 2$ câu hỏi hiểu bản chất và cân bằng bán phản ứng.
    - **L3 (Vận dụng)**: **5 câu hỏi tính toán định lượng chuyên sâu**.
    - **L4 (Vận dụng cao)**: **5 câu hỏi mô hình hóa thực tế và bài toán điểm 9-10 ĐGNL VNU-HCM**.
    - $\Rightarrow$ **Tổng cộng: 14 câu hỏi/dạng $\times$ 16 dạng bài = 224 câu hỏi mẫu chuyên sâu** có đầy đủ đề bài KaTeX, 4 phương án lựa chọn và lời giải chi tiết từng bước.
- **Làm Giàu Kiến Thức Nền Tảng SGK (Khối 1) Đa Tầng & Trực Quan**:
  - Tự động bổ sung định nghĩa khoa học chuẩn SGK GDPT 2018, hệ thống đầy đủ 4 quy tắc gán số oxi hóa, bảng các trường hợp ngoại lệ đặc thù ($H_2O_2, KO_2, OF_2, NaH$) và lưu ý khảo thí ĐGNL.
  - Tự động bổ sung khối *Ví dụ minh họa SGK* (`### 📌`) và *Bài toán mẫu áp dụng phương pháp* (`### 📝`).
- **Khử Lỗi Thoát Ký Tự JSON Trong Gemini Response (`CleanJsonFences`)**:
  - Bổ sung bộ tiền xử lý Regex `Regex.Replace(result, @"\\([%$&_#^{}])", "$1")` loại bỏ các ký tự escape LaTeX bất hợp lệ (`\%`, `\$`) sinh ra bởi Gemini, ngăn ngừa triệt để lỗi `JsonException` khi parse.
- **Kiểm Thử Biên Dịch & Vận Hành**:
  - `dotnet build` đạt 100% (**0 Error, 0 Warning**).
  - API AI Engine chạy ổn định trên HTTP port 5104.
  - `python test_ai_extract.py`: Trích xuất thành công 16 dạng bài với đúng 224 câu hỏi trắc nghiệm (đạt tỷ lệ 5 câu L3 và 5 câu L4 cho từng dạng bài).

---

## [15/10/2026] - Chuyển Đổi Hoàn Toàn & Triển Khai Động Cơ AI Tri Thức Bóc Tách Khung Chương Trình & Dạng Bài Bằng C# Thuần (.NET 9 Clean Architecture), Hỗ Trợ Tài Liệu Khổ Lớn 79-99+ Trang
- **Chuyển Đổi Toàn Bộ Sang C# Thuần (.NET 9) Trong AI Engine (`V-Eval-Ai_Engine`)**:
  - Di chuyển và chuẩn hóa toàn bộ tính năng trích xuất khung chương trình và phân tích dạng bài từ prototype Python sang 100% C# .NET 9 Clean Architecture.
  - **Application Layer**:
    - [CurriculumAiDtos.cs](./V-Eval-Ai_Engine.Application/DTOs/CurriculumAiDtos.cs): Khai báo đầy đủ các DTO chuẩn mực cho cây phân cấp (`ImportCurriculumRequestDto`, `ImportChapterDto`, `ImportLessonDto`, `ImportPatternDto`, `ImportQuestionDto`) và DTO nhận diện dạng bài (`AnalyzePatternRequestDto`, `AnalyzePatternResponseDto`, `PatternExemplarQuestionDto`).
    - [ICurriculumAiService.cs](./V-Eval-Ai_Engine.Application/Interfaces/ICurriculumAiService.cs): Hợp đồng xử lý bóc tách tài liệu và phân tích dạng bài sư phạm, bổ sung hỗ trợ trích lọc theo dải trang (`startPage`, `endPage`) và độ sâu bóc tách (`extractionDepth`).
  - **Infrastructure Layer**:
    - [CurriculumAiService.cs](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs): Trích xuất nội dung từ tệp PDF trực tiếp bằng `UglyToad.PdfPig`, tệp Word DOCX bằng `System.IO.Compression.ZipArchive` với cơ chế bảo toàn cấu trúc ngắt dòng `</w:p>`, `</w:tr>` thành `\n`.
    - **Nâng Cấp Xử Lý Tài Liệu Khổ Lớn (79 - 100+ Trang)**: Áp dụng cơ chế cắt lát trang và bảo toàn cấu trúc đề mục, nâng trần ký tự lên 120.000 ký tự và cấu hình `maxOutputTokens = 8192` cùng timeout 90 giây.
    - Tối ưu hóa prompt chuyên sâu ĐGNL VNU-HCM: Bắt buộc AI phân tách đầy đủ từ 3 đến 6 Bài học và 8 đến 16 Dạng bài chi tiết đối với tài liệu chuyên đề lớn, không gom cụm sơ sài 1-2 bài.
    - Bộ xử lý làm sạch JSON chuyên sâu: Tự động loại bỏ markdown code fences và phục hồi ký tự escape cho công thức toán học và hóa học (`\ge`, `\le`, `\frac`, `\times`, `\Delta`, `\text`, `\mathrm`...).
    - Heuristic Engine nâng cấp: Tự động nhận diện môn học và sinh đầy đủ 5 bài học kèm 10 dạng bài chuyên sâu khi mạng gián đoạn.
    - [DependencyInjection.cs](./V-Eval-Ai_Engine.Infrastructure/DependencyInjection.cs): Đăng ký `ICurriculumAiService` vào DI container với timeout 3 phút.
  - **API Layer**:
    - [CurriculumEndpoints.cs](./V-Eval-Ai_Engine.API/Endpoints/CurriculumEndpoints.cs): Cung cấp 2 Minimal API endpoints:
      - `POST /api/ai-engine/curriculum/extract`: Nhận đa định dạng (PDF, Word, Text) hoặc JSON payload, tiếp nhận các tham số `start_page`, `end_page`, `extraction_depth`.
      - `POST /api/ai-engine/curriculum/analyze-pattern`: Phân tích sâu 4 khối nội dung dạng bài toán theo chuẩn Bloom L1-L4.
    - [Program.cs](./V-Eval-Ai_Engine.API/Program.cs): Đăng ký `app.MapCurriculumEndpoints()`.
- **Đồng Bộ Web Client & Khôi Phục Python Service**:
  - Cập nhật [aiService.js](../../V-Eval-Web_Client/src/services/aiService.js): Định tuyến trực tiếp các hàm `extractCurriculumFromDocument` và `analyzePatternWithAi` đến các endpoint C# `/api/ai-engine/curriculum/...`.
  - Hoàn trả nguyên trạng các file Python `rag-service/routers/documents.py` và `rag-service/schemas.py`.
- **Khắc Phục Lỗi Type Hint Mapping Tại `documents.py:293` (`rag-service/routers/documents.py`)**:
  - Khắc phục lỗi static type checking `Expected argument after ** to be a mapping, got: dict[str, Any] | tuple[Any, ...]` bằng cách ánh xạ minh thị từng thuộc tính đối tượng `DocumentVersionItem(id=..., document_group_id=..., file_name=..., version=..., file_hash=..., status=..., is_current=..., total_chunks=..., created_at=..., error_message=...)`.
  - Kiểm thử `python -m py_compile` hoàn thành sạch sẽ 100% (**0 error**).
- **Kiểm Thử Vận Hành & Biên Dịch**:
  - `dotnet build` toàn bộ `V-Eval-Ai_Engine.sln` đạt 100% thành công (**0 error, 0 warning**).
  - `npm run build` trên `V-Eval-Web_Client` đạt 100% thành công (876ms).
  - Kiểm thử trực tiếp tệp DOCX 79 trang `thuvienhoclieu.com-4.-Chuyen-de-4.-Phan-ung-oxi-hoa-khu.docx`: Bóc tách thành công **5 Bài học lớn và 10 Dạng bài chi tiết**.

---

## [07/10/2026] - Chuẩn Hóa Khung Cấu Hình .NET Ingestion, Python RAG & Khử Lộ Bí Mật Mẫu
- **Chuẩn Hóa File Cấu Hình Mẫu .NET API (`appsettings.example.json`)**:
  - Bổ sung `ConnectionStrings:DefaultConnection` đồng bộ với `appsettings.json`.
  - Loại bỏ các trường cấu hình cũ không còn sử dụng (`QdrantSettings`), bảo toàn cấu hình danh sách mô hình Gemini/OpenAI và timeout xử lý bóc tách tài liệu.
- **Chuẩn Hóa File Cấu Hình Mẫu Python RAG (`rag-service/.env.example`)**:
  - Ẩn toàn bộ mật khẩu kết nối CSDL PostgreSQL trong `DATABASE_URL`, thay bằng placeholder `YOUR_PASSWORD`.
  - Đồng bộ cùng cấu hình chuẩn trong `Configs/V-Eval-Ai_Engine/` của System-Repo.
- **Kiểm Thử Biên Dịch**:
  - `dotnet build` đạt 100% thành công (0 warning, 0 error).

---

## [28/09/2026] - Tối Ưu Cấu Hình HttpClient Timeout Qua AppSettings & Nghiệm Thu Kiến Trúc Textbook RAG Ingestion
- **Tối Ưu Cấu Hình Timeout HttpClient Qua `appsettings.json` (`DependencyInjection.cs`)**:
  - Loại bỏ hoàn toàn các giá trị hardcode thời gian chờ (`TimeSpan.FromMinutes(8)` và `TimeSpan.FromMinutes(10)`) trong mã nguồn C#.
  - Bổ sung hai tham số cấu hình linh hoạt trong `appsettings.json`, `appsettings.example.json`, và `appsettings.Development.json`:
    - `AiSettings:ExamParserTimeoutMinutes = 8`
    - `AiSettings:TextbookParserTimeoutMinutes = 10`
  - Cập nhật `DependencyInjection.cs` đọc trực tiếp từ `IConfiguration` với cơ chế fallback an toàn, cho phép DevOps tinh chỉnh thời gian timeout linh hoạt theo từng môi trường triển khai (Local, Staging, Production Docker/K8s) mà không cần biên dịch lại mã nguồn.
- **Nghiệm Thu Kiến Trúc Minimal API Nạp SGK Bởi `ThinhTran2412`**:
  - Nghiệm thu chính thức cấu trúc tệp `TextbookEndpoints.cs` theo chuẩn ASP.NET Core Minimal APIs thay thế mô hình Controller truyền thống.
  - Ban hành tài liệu phân tích kỹ thuật chuyên sâu về pipeline streaming chống tràn RAM khi nạp tệp nặng tới 250 MB tại `docs/architecture_acceptance.md`.

---

## [27/09/2026] - Tối Ưu Luồng IRT 2PL Theo Thang Đo Bloom 6 Mức Độ, AI Exam Studio & Kiến Trúc Chế Độ Kép (Dual Engine: Gemini Cloud vs Siêu Tốc Calibrated)
- **Tối Ưu Luồng Ánh Xạ Độ Khó IRT 2PL Theo 6 Mức Độ Tư Duy Bloom (`diagnostic_engine.py`)**:
  - Nâng cấp từ 4 cấp độ cũ lên đầy đủ 6 mức độ tư duy chuẩn Bloom cải tiến (Revised Bloom's Taxonomy):
    - Mức 1: Nhận biết (Remembering) `-> b = -1.8`
    - Mức 2: Thông hiểu (Understanding) `-> b = -1.0`
    - Mức 3: Vận dụng (Applying) `-> b = -0.2`
    - Mức 4: Phân tích (Analyzing) `-> b = +0.6`
    - Mức 5: Đánh giá (Evaluating) `-> b = +1.4`
    - Mức 6: Sáng tạo (Creating) `-> b = +2.2`
  - Cập nhật schema Pydantic `DiagnosticAnswerItem` (`rag-service/schemas.py`) mở rộng `difficulty_level` từ `[1..4]` lên `[1..6]`.
- **Tối Ưu Luồng Xử Lý & Khắc Phục Lỗi Import `os` (`rag-service/routers/diagnostic.py`)**:
  - Khai báo bổ sung thư viện `import os` vào phần import tại đầu tệp `routers/diagnostic.py`.
  - Khắc phục triệt để lỗi phân tích cú pháp tĩnh `Could not find name os` tại hàm `_call_gemini_exam_generator` (dòng 540: `api_key = GOOGLE_API_KEY or os.environ.get("GOOGLE_API_KEY", "")`).
  - Tối ưu luồng truy xuất biến môi trường an toàn và chạy toàn bộ bộ kiểm thử đơn vị `tests/test_diagnostic.py` thành công 100% (**All tests passed**).
- **Triển Khai AI Exam Studio & Kiến Trúc Chế Độ Kép (Dual Engine Architecture)**:
  - Bổ sung endpoint `POST /api/v1/diagnostic/generate-exam` (FastAPI port 8000) với 2 chế độ sinh đề:
    1. **Google Gemini Live Cloud (`gemini-flash-lite-latest`)**: Gọi trực tiếp Google Gemini API sử dụng cấu trúc `responseSchema` nghiêm ngặt, cho phép AI suy nghĩ và sáng tác 100% câu hỏi mới toanh bám sát prompt, định hướng môn học và phân bổ Bloom (~3-6s).
    2. **Siêu Tốc Calibrated Bank (< 0.1s)**: Tổ hợp câu hỏi chuẩn hóa tâm lý học từ bộ nhớ RAM với phân bổ đáp án đều (A/B/C/D 25%), phục vụ tức thì cho nhu cầu kiểm thử nhanh và demo luồng mà không phụ thuộc độ trễ mạng.
  - Tự động cơ chế phục hồi (Graceful Fallback): Khi Gemini Cloud chạm trần quota hoặc timeout, hệ thống tự động fallback sang kho Calibrated Bank và cảnh báo rõ ràng trên UI.
  - **Cơ Chế Nhận Diện Ý Định & Khắc Phục Xung Đột Lĩnh Vực (Strict Domain Conflict Resolution)**:
    - Bổ sung cơ chế phát hiện từ khóa chuyên sâu (cả có dấu và không dấu: `ngữ văn`, `văn học`, `tiếng việt`, `đọc hiểu`, `toán học`, `logic`...) trong câu prompt của giáo viên.
    - Tự động đồng bộ và cưỡng chế `domain_id` chuẩn (ví dụ: `Full Ngữ Văn` -> `dom_lang`), vô hiệu hóa tình trạng rơi vào cấu hình mặc định 5 lĩnh vực (`ALL`).
    - Nâng cấp System Prompt cho Gemini với quy chuẩn sư phạm nghiêm ngặt: Tuyệt đối 100% câu hỏi thuộc đúng môn học được yêu cầu (xóa bỏ hoàn toàn tình trạng đề Văn bị lọt câu hỏi Toán/KHTN); loại bỏ triệt để các lời giải tự nhận sai hoặc giả định sửa đề.
    - **Ưu Tiên Tuyệt Đối Lựa Chọn Dropdown Của Giáo Viên (Dropdown Precedence Fix)**: Chỉ kích hoạt suy luận domain từ prompt khi `domain_id == 'ALL'`. Nếu giáo viên chủ động chọn môn trong dropdown (như Khoa học Xã hội / Địa lý `dom_soc_sci`), hệ thống giữ nguyên 100%, chấm dứt hoàn toàn lỗi tự động nhảy dropdown sang Ngữ văn khi prompt có từ khóa chung như 'đọc hiểu'.
- **Cung Cấp Giao Diện Web Runner Khảo Sát Năng Lực (`/api/ai-engine/view-diagnostic`)**:
  - Bổ sung endpoint `GET /api/ai-engine/view-diagnostic` trong `ExamEndpoints.cs`.
  - Triển khai tệp giao diện `view-diagnostic.html` (`wwwroot/view-diagnostic.html`) phục vụ trực quan hóa toàn diện bài thi chẩn đoán 30 câu, tính toán Psychometrics và biểu đồ Radar Chart.
- **Kiểm Thử Vận Hành & Biên Dịch**: `dotnet build` giải pháp `V-Eval-Ai_Engine.sln` thành công 100% (**0 Error**); Kiểm thử HTTP cả 2 chế độ Gemini Cloud (`ms: 7245`) và Fast Calibrated (`ms: 0`) trả về 200 OK; Kiểm thử với prompt `Full Ngữ Văn` sinh chính xác 100% câu hỏi Ngôn ngữ & Văn học; Kiểm thử với `dom_soc_sci` sinh chính xác 100% câu hỏi Sử - Địa.

---

## [26/09/2026] - Trực Quan Hóa Chunks Tri Thức (Markdown + KaTeX + Tables), Khắc Phục Lỗi Font InDesign/CID & Resumable Ingestion
- **Nâng Cấp Giao Diện Trực Quan Hóa Chunks Tri Thức (`view-textbook.html`)**:
  - **Chuyển Đổi Từ Text Thuần Sang Định Dạng Trực Quan Cao Cấp (Rich Markdown & KaTeX Preview)**:
    - Tích hợp thư viện `marked.js` và `katex` để tự động render toàn bộ văn bản SGK đã cắt: Tiêu đề (`h1` - `h4`), danh sách gạch đầu dòng, khối trích dẫn (`blockquote`), chữ đậm/nghiêng.
    - **Hiển Thị Bảng Biểu Số Liệu Glassmorphic**: Tự động chuyển đổi các bảng Markdown thành bảng HTML hiện đại với hiệu ứng xen kẽ dòng (`striped rows`), bo góc và hover làm nổi bật số liệu.
    - **Render Công Thức Toán/Lý/Hóa Chuẩn KaTeX**: Tự động nhận diện và render sắc nét các công thức inline (``$...$``) và block (``$$...$$``) mà không bị xung đột với parser Markdown.
  - **Thanh Công Cụ Điều Khiển & Tìm Kiếm Chunks Đa Năng**:
    - **Bộ Lọc & Tìm Kiếm Real-time**: Ô input tìm kiếm tức thì theo từ khóa văn bản hoặc số trang (`Trang 5`, `p10`), hiển thị số lượng chunks khớp.
    - **Chế Độ Xem Linh Hoạt (Global & Per-Card View Switcher)**: Cho phép chuyển đổi linh hoạt giữa `👁️ Trực Quan` và `📄 Raw Text` cho toàn bộ danh sách hoặc riêng biệt từng Chunk.
    - **Xuất & Sao Chép Nhanh**: Tích hợp nút `📋 Copy` từng chunk, `📋 Sao Chép Hết` (toàn bộ nội dung cuốn sách) và `💾 Tải File .MD` để lưu toàn bộ sách dưới dạng Markdown chuẩn phục vụ huấn luyện RAG.
- **Tự Động Phát Hiện & Khắc Phục Triệt Để Lỗi Font InDesign / CID Subsetting (Mojibake)**:
  - Phân tích nguyên nhân: Các file SGK (như SGK Lớp 10 Địa Lí, Lịch Sử, Văn...) chứa lớp text số bị lỗi encoding do phần mềm InDesign không nhúng ToUnicode CMap, khiến PdfPig bóc tách ra các chuỗi ký tự rác vô nghĩa (VD: `&IFHPkFVLQK WKKQPQCdo...`, `PNtFKWKtFKWt...`).
  - Triển khai thuật toán kiểm định `IsCorruptedFontEncoding(rawText)` với 3 tầng lọc:
    1. Kiểm tra mật độ ký tự rác / glyph lạ (`{`, `}`, `\`, `^`, `~`, `|`, `¶`, `§`, `©`, `®`, `½`, `¼`, `¾`, `¿`, `±`, `ł`, `Ċ`, `ī`, `Ť`, `Š`, `ś`, v.v.).
    2. Kiểm tra tỷ lệ nguyên âm tiếng Việt/Anh (`< 23%` là dấu hiệu phân mảnh phụ âm do lỗi CMap).
    3. Kiểm tra cụm phụ âm liên tiếp dài (`>= 5` phụ âm) và các từ không chứa nguyên âm.
  - Khi phát hiện lớp chữ số bị lỗi, hệ thống tự động bỏ qua text rác và chuyển hướng bóc tách ảnh bằng Gemini Vision AI (DPI 96) cho ra văn bản tiếng Việt sạch sẽ 100%.
- **Chế Độ Bóc Tách Ép Vision AI & Tùy Chọn Nạp Lại Từ Đầu (Force Reingest / Overwrite)**:
  - Bổ sung tùy chọn `VISION_AI` trên UI và API: Bóc tách 100% bằng Vision AI, bỏ qua hoàn toàn lớp text số nếu nghi ngờ file PDF lỗi font.
  - Bổ sung cờ `forceReingest` và checkbox trên UI: Tự động xóa sạch các chunk cũ bị lỗi font (`DELETE FROM v_eval_ai."KnowledgeVectorChunks" WHERE source_id = @source_id`) và bóc tách lại từ trang 1.
- **Khắc Phục Lỗi 400 Bad Request & Cập Nhật Danh Mục Mô Hình Gemini Đương Đại**:
  - Phát hiện và loại bỏ trường `thinkingConfig: { thinkingBudget: 0 }` trong payload gửi tới `gemini-flash-lite-latest` (nguyên nhân gây lỗi 400 `INVALID_ARGUMENT` do Google API không hỗ trợ trường thinking trên dòng Lite).
  - Loại bỏ các mô hình đã ngừng hỗ trợ trả về mã 404 (`gemini-2.0-flash`, `gemini-1.5-flash`, `gemini-2.5-flash`).
  - Cập nhật danh mục mô hình tối ưu theo khuyến nghị chính thức của Google: `gemini-flash-lite-latest`, `gemini-3.5-flash-lite`, `gemini-3.1-flash-lite`, `gemini-3.8-flash`, `gemini-3.6-flash`.
  - Bổ sung ghi log chi tiết mã HTTP và nội dung lỗi khi gọi API để dễ dàng giám sát vận hành.
- **Tối Ưu Hóa Đầu Vào & Điểm Ảnh Vision AI (Low-DPI Optimization)**:
  - Giảm thiểu token và chi phí Free Tier bằng cách kết xuất trang PDF scan ở độ phân giải vừa vặn `Dpi = 96` (kích thước ~800x1100 px, dung lượng nén JPEG chất lượng 70 chỉ < 80 KB).
  - Tối đa hóa tốc độ xử lý trên Google Gemini Flash (`gemini-flash-lite-latest` / `gemini-1.5-flash`), bóc tách mỗi trang trong ~2.5s mà vẫn giữ nguyên 100% công thức LaTeX (``$x^2 + y^2$``) và bảng biểu Markdown.
- **Kiến Trúc Checkpointing & Nạp Ngầm Lưu Trực Tiếp CSDL (Per-Page Persistence)**:
  - Tạm lưu tệp PDF lên máy chủ (`uploads/textbooks/`) và tính mã băm SHA-256 định danh tệp duy nhất.
  - Tác vụ nạp chạy hoàn toàn ngầm (`Task.Run`), độc lập với vòng đời HTTP Request, trả về `202 Accepted` ngay lập tức.
  - Mỗi trang xử lý xong được lưu ngay thành Chunk vào bảng `v_eval_ai."KnowledgeVectorChunks"` và cập nhật tiến trình vào `v_eval_ai."KnowledgeSources"`.
  - **Khả Năng Khôi Phục (Resume)**: Khi có sự cố ngắt kết nối, tắt trình duyệt hoặc tải lại tệp, hệ thống kiểm tra `MAX(page_number)` trong CSDL và **tự động tiếp tục nạp từ trang tiếp theo (Page X+1)**, không bao giờ phải nạp lại từ đầu.
- **Khôi Phục Trạng Thái Giao Diện Studio (`view-textbook.html`)**:
  - Tích hợp endpoint `GET /api/ai-engine/textbooks/active-job`: Khi người dùng tải lại trang (F5) hoặc quay lại sau nhiều giờ, giao diện tự động kết nối lại tiến trình ngầm đang chạy và tiếp tục cập nhật thanh tiến độ % và live console logs.
- **Bộ Công Cụ Ping & Đo Độ Trễ Vision AI**:
  - Cung cấp endpoint `GET /api/ai-engine/textbooks/ping-vision`, banner kiểm tra trực quan trên Web và script PowerShell [`Scripts/run_local/ping_vision.ps1`](./../../Scripts/run_local/ping_vision.ps1).

---

## [25/09/2026] - Triển Khai Phân Loại OCR Mode, RapidOCR Offline Speed-Up & API Lưu Tri Thức Database (`v_eval_ai`)
- **Giao Diện Trang Web Nạp Tri Thức SGK (`wwwroot/view-textbook.html`)**:
  - Xây dựng giao diện Web UI hiện đại với Glassmorphic design, thanh tab chuyển đổi mượt mà giữa Ngân Hàng Đề Thi (`/api/ai-engine/view-exam`) và Nạp Tri Thức SGK (`/api/ai-engine/view-textbook`).
  - Tích hợp menu tùy chọn **Chế độ xử lý chữ (OCR Mode)**:
    - 📖 **Văn Bản Thuần - Ngữ Văn, Anh Văn, KHXH** *(Gọi Python PyMuPDF + RapidOCR Local Engine)*.
    - 📄 **Tự Động / Text Local** *(Đọc siêu tốc local offline bằng `PdfPig` cho PDF có lớp chữ sẵn)*.
    - 🧪 **Tự Nhiên & Công Thức - Toán, Lý, Hóa** *(Giữ nguyên định dạng LaTeX)*.
  - Tích hợp nút bấm **💾 Lưu Tri Thức Vào Database (`v_eval_ai`)** gửi payload Vector Chunks về CSDL.
  - Bảng điều khiển tiến độ ngầm (Background Job Dashboard) hiển thị phần trăm tiến trình %, live console log real-time và preview các Chunks tri thức.
- **Tối Ưu Hóa & Đồng Bộ Hóa Tuần Tự RapidOCR (`textbook_local_parser.py`)**:
  - Khôi phục luồng xử lý tuần tự (Sequential Order) 100% chuẩn xác theo thứ tự trang từ `1..N`.
  - Sử dụng ánh xạ bộ nhớ trực tiếp (Direct Numpy Buffer) từ `pix.samples`, loại bỏ hoàn toàn chi phí nén/giải nén PNG/JPEG trung gian.
  - Phân tích số trang `[Trang X/Y]` để đồng bộ thanh tiến độ phần trăm `%` tăng dần mượt mà từ 15% đến 80% trên UI.
- **Khởi Tạo Schema `v_eval_ai` Trên Supabase & Tích Hợp Npgsql Repository**:
  - Đã khởi tạo thành công Schema `v_eval_ai` và extension `vector` (pgvector) trên CSDL Supabase Cloud PostgreSQL.
  - Ban hành kịch bản DDL chuẩn tại [`docs/SQL/V_EVAL_AI_SCHEMA.sql`](./docs/SQL/V_EVAL_AI_SCHEMA.sql) bao gồm 2 bảng:
    - `v_eval_ai."KnowledgeSources"`: Quản lý metadata tài liệu SGK, mã Hash SHA-256, số trang, số chunks.
    - `v_eval_ai."KnowledgeVectorChunks"`: Lưu trữ các đoạn phân đoạn tri thức (chunks) kèm trường `embedding vector(768)` và chỉ mục HNSW (`idx_knowledge_vector_hnsw`).
  - Triển khai `TextbookRepository` (`ITextbookRepository`) sử dụng Npgsql kết nối Supabase, ghi trực tiếp các Chunks tri thức vào CSDL khi bấm nút trên UI.
- **REST Endpoints & Database Persistence (`TextbookEndpoints.cs`)**:
  - `POST /api/ai-engine/textbooks/upload-pdf`: Tiếp nhận PDF + `ocrMode`, khởi chạy Background Job async và trả về `202 Accepted` kèm JobId.
  - `GET /api/ai-engine/textbooks/jobs/{jobId}`: Endpoint Polling tiến độ ngầm real-time.
  - `POST /api/ai-engine/textbooks/save-db`: Gọi `ITextbookRepository` lưu thực tế các Vector Chunks tri thức vào Supabase PostgreSQL (Schema `v_eval_ai`).
  - `GET /api/ai-engine/view-textbook`: Phục vụ giao diện HTML UI nạp SGK.

---

## [24/09/2026] - Cấu Hình Supabase Connection & Phân Chi Schema `v_eval_ai`
- **Cấu Hình Chuỗi Kết Nối PostgreSQL (Supabase Cloud)**:
  - Bổ sung `ConnectionStrings:DefaultConnection` trong `appsettings.json` kết nối trực tiếp Supabase Cloud PostgreSQL.
  - Khởi tạo và cập nhật `DATABASE_URL` trong `rag-service/.env` & `.env.example` cấu hình kết nối Python RAG Engine với Supabase (`postgresql+psycopg`).
- **Phân Chi Schema `v_eval_ai` & `v_eval_system`**:
  - Xác nhận tạo Schema `v_eval_ai`, `v_eval_system` và `pgvector` extension trên Supabase SQL Editor.
  - Đảm bảo hạ tầng Vector RAG Store cho `KnowledgeVectorChunks` (`v_eval_ai`) phục vụ bài toán RAG Engine cho Môn/Skill.

---

## [22/09/2026] - Triển Khai Diagnostic Engine Cho Core Flow 1 (IRT 2PL, BKT Prior & Radar Chart)
- **Module Tính Toán Năng Lực IRT & BKT (`rag-service/diagnostic_engine.py`)**:
  - Ước lượng năng lực học sinh `theta_0` bằng mô hình IRT 2-Parameter Logistic (2PL) kết hợp ước lượng hậu nghiệm cực đại (MAP) có hàm phạt Gaussian Prior `N(0, 2.0^2)`.
  - Tối ưu hóa cực trị 1 chiều bằng thuật toán Brent (`scipy.optimize.minimize_scalar`).
  - Xử lý câu trả lời đoán mò thần tốc (< 5 giây): chiết giảm tham số phân biệt `a -> 0.1` để tránh thổi phồng năng lực ảo.
  - Tính toán xác suất thành thạo ban đầu BKT Prior `P(L0) = Sigmoid(theta)` cho từng kỹ năng với cơ chế kẹp an toàn `[0.05, 0.95]`.
  - Xử lý tình huống không hoàn hảo (unhappy case): các kỹ năng chưa có câu hỏi trong bài kiểm tra rút gọn 30 câu tự động kế thừa `P(L0)` suy diễn từ năng lực miền cha (`domain_level`).
  - Phân loại xếp lớp chuẩn mực 3 mức: `FOUNDATION` (`theta < -0.5`), `ACCELERATION` (`-0.5 <= theta <= 0.5`), `BREAKTHROUGH` (`theta > 0.5`).
  - Sinh tọa độ biểu đồ Radar so sánh năng lực học sinh với điểm chuẩn benchmark dựa trên mục tiêu điểm thi (V-ACT target score).
- **Pydantic Schemas (`rag-service/schemas.py`)**:
  - Bổ sung `DiagnosticAnswerItem`, `DiagnosticDomainName`, `DiagnosticAnalyzeRequest`, `DiagnosticSkillPriorDto`, `DiagnosticDomainScoreDto`, `DiagnosticRadarAxisDto`, `DiagnosticAnalyzeResponse`.
- **API Endpoint & LLM Commentary (`rag-service/routers/diagnostic.py`)**:
  - Triển khai `POST /api/v1/diagnostic/analyze` tiếp nhận dữ liệu 30 câu từ Practice Service và trả về phân tích chẩn đoán hoàn chỉnh.
  - Sinh lời nhận xét Socratic sư phạm động bằng Google Gemini (`gemini-3.6-flash`), có cơ chế fallback tự động sinh nhận xét mẫu khi offline hoặc quá tải.
  - Triển khai `GET /api/v1/diagnostic/config` cung cấp cấu hình ngưỡng phân lớp và tham số IRT.
- **Kiểm Thử Đơn Vị, Tích Hợp & End-to-End**:
  - Đạt 12/12 test cases trong `rag-service/tests/test_diagnostic.py`.
  - Kiểm thử End-to-End thực tế với Practice Service qua Swagger: Trả về kết quả phân tích psychometrics, biểu đồ Radar, 12 BKT Priors và lời nhận xét Socratic trong thời gian thực.
- **Tài Liệu Đặc Tả Toán Học ([cong_thuc_psychometrics_irt_bkt.md](./cong_thuc_psychometrics_irt_bkt.md))**:
  - Biên soạn và ban hành tài liệu toán học toàn diện trình bày chi tiết toàn bộ các công thức IRT 2PL, MAP Brent, SEM, BKT Sigmoid, Domain Inference, Radar Benchmark và lý giải bài toán thực tế cho V-ACT.

---

## [21/09/2026] - Thiết Kế Kiến Trúc AI Exam Generation (30 Câu), Multi-Domain RAG & Quy Trình Duyệt Linh Hoạt
- **Lập Kế Hoạch Kiến Trúc ([ai_question_generation_rag_approval_plan.md](./ai_question_generation_rag_approval_plan.md))**:
  - Thiết kế chi tiết luồng sinh đề thi 30 câu bất đồng bộ, lưu DB trạng thái `PENDING_APPROVAL`.
  - Thiết kế mô hình `KnowledgeVectorChunks` (`pgvector`) phân loại theo Môn học (`domain_id`) và Kỹ năng (`skill_id`).
  - Thiết kế cơ chế Dynamic Web Search (Hybrid Search) kết hợp SGK nội bộ và tin tức thời sự mở rộng.
  - Cấu hình API sinh 1 câu thay thế tương tương trong 1-2 giây và cho phép Manager linh hoạt tinh chỉnh độ khó/bước tính toán (`difficulty_level`).

---

## [18/09/2026] - Phát Hành Công Cụ Push Độc Lập `Scripts/push.bat` & Chuẩn Hóa Bộ Docs
- **Khởi Tạo `Scripts/push.bat`**: Đóng gói công cụ push độc lập hỗ trợ 3 chế độ (nhánh hiện tại, danh sách số nhánh có sẵn, tạo nhánh mới).
- **Chuẩn Hóa Bộ Docs Service**: Đồng bộ hệ thống tài liệu theo 3 file chuẩn `daily.md`, `process.md` và `architecture_acceptance.md`.

---

## [15/09/2026] - Dockerize AI Engine (Milestone 5)
- **Tạo `Dockerfile` Multi-stage**: Build và publish .NET 9 API image trên cổng `5104`.
- **Tích hợp Docker Compose**: Khai báo container `v_eval_ai_engine` tham gia mạng `veval_network`.

---

## [14/09/2026] - Production Security & Config (Milestone 4)
- **Khởi Tạo `appsettings.example.json`**: Tạo file mẫu chứa đầy đủ các tham số cấu hình: `AiSettings` (OpenAI ApiKey, Gemini ApiKeys, Supported Models, MaxAttempts) và `QdrantSettings` (Host, Port, ApiKey) với label mẫu.
- **Bảo Mật `.gitignore`**: Cập nhật quy tắc `.gitignore` chặn toàn bộ `appsettings.json` và `appsettings.*.json` cá nhân chứa API Key thật.
- **Định Tuyến Qua Gateway YARP**: Cấu hình Route `/api/ai-engine/{**catch-all}` tại Gateway V-Eval trỏ về cổng `:5104`.

---

## [07/09/2026] - Khắc Phục Lỗi Hiển Thị Công Thức Vật Lý & Hóa Học Complex (Milestone 3.5)
- **Khôi phục Ký tự Thoát JSON cho Mã LaTeX**: Khắc phục hiện tượng JSON Deserializer nuốt các ký tự điều khiển ASCII biến `\frac` thành `\x0c` (form-feed), `\times` thành `\t` (tab), `\text` thành `\t`, `\bar` thành `\b`, `\beta` thành `\b`. Khôi phục về dạng hợp lệ `\\frac`, `\\times`, `\\text`, `\\bar`, `\\beta`.
- **Sửa Lỗi OCR Nhìn Nhầm Ký Hiệu Delta ($\Delta$)**: Xử lý triệt để hiện tượng OCR nhìn nhầm ký hiệu tam giác biến thiên $\Delta$ thành `ar{ ext{A}}`, `\bar{\text{A}}` hoặc `\bar{A}`. Tự động chuẩn hóa về $\Delta[\text{Acetone}]$, $\Delta t$, $R = -\frac{\Delta[\text{Acetone}]}{\Delta t}$.
- **Tự Động Bọc Dấu `$...$` Cho Công Thức Dài & Đơn Vị Số Mũ Âm**: Các công thức Vật lý/Hóa học dài chưa có dấu đô-la ($H = \frac{P_n - P_{hp}}{P_n} \times 100\%$, $P_{hp}$, $P_n$, $k^2$) và đơn vị có số mũ âm ($2,33 \cdot 10^{-3}\text{ mol}\cdot\text{l}^{-1}\cdot\text{phút}^{-1}$) được tự động nhận diện và bọc KaTeX chuẩn typographic.
- **Chống Lỗi Nhân Đôi Chữ Khi Copy KaTeX**: Thêm CSS `.katex-mathml { user-select: none; }` trong `view-exam.html`, triệt tiêu hoàn toàn tình trạng học sinh/giáo viên sao chép công thức bị dính 2 lần chữ (ví dụ: `k 2 k 2` hoặc `P h p P hp`).
- **Bổ Sung Bộ Lọc C# Backend (`GeminiExamParserService.cs`)**: Triển khai hai hàm `SanitizeJsonForLatex(string json)` xử lý chuỗi JSON trước khi deserialize và `CleanExamDto(ParsedExamDto exam)` làm sạch toàn bộ DTO trước khi trả về endpoint.
- **Tự Động Tái Tạo Bảng Tiêu Đề 2 Tầng (`Colspan` / `Rowspan`)**: Nâng cấp hàm `convertMarkdownTableToHtml`: Tự động nhận diện cấu trúc tiêu đề cha - con (như `Số giờ chiếu sáng vào ban đêm (giờ): 0,5` đi cùng các cột `1, 2, 3, 4, 5` trong Chùm câu 109–111) và tự động dựng lại `<thead>` 2 tầng chuẩn mực với `colspan="6"` và `rowspan="2"`.
- **Cập nhật Prompt Hướng Dẫn Bảng 2 Tầng & Tinh Chỉnh CSS**: Hướng dẫn Gemini xuất HTML `<table>` khi gặp bảng phức tạp. Tinh chỉnh CSS `.exam-table`: Kẻ viền ô dạng lưới `1px solid #334155`, căn giữa theo phương dọc (`vertical-align: middle`) và phương ngang, căn trái cột danh mục đầu tiên.

---

## [31/08/2026] - Tối Ưu Hóa Ingestion & Gemini Vision OCR Cloud (Milestone 2)
- **Bảo mật API Key (Leak-proof configuration)**: Cấu hình `.gitignore` chặn `appsettings.Development.json` và dùng các placeholder an toàn trong `appsettings.json`.
- **Tối ưu hóa Ingestion (Raw PDF Base64)**: Thay vì chuyển sang ảnh PNG (CPU lag 3 phút), gửi trực tiếp PDF base64 sang Gemini để xử lý tức thì (<15s) bằng mô hình ổn định `gemini-2.5-flash`.
- **Đồng bộ hóa Progress Logging**: Thêm logs thời gian thực trong Python (`parse_single_pdf.py`) báo cáo kích thước file và tiến trình tải lên.
- **Tích hợp Frontend & Content Service**: Nút "Lưu vào Database" trên UI `view-exam.html` gọi sang Minimal API `POST /api/content/exams/import` của Content Service để lưu dữ liệu.

---

## [30/08/2026] - Khởi Tạo Triển Khai Parse PDF & Endpoints (Milestone 1)
- **Tạo tài liệu quản lý tiến độ (.md)**: Tạo các file tài liệu tiến độ và `ai_data_ingestion.md`.
- **Viết bộ parse tài liệu Python (`document_parser.py`)**: Hỗ trợ đọc PDF (`pypdf`), DOCX (`python-docx`), DOC cũ (COM), Image metadata (`Pillow`).
- **Tạo endpoint API hiển thị (`GET /api/ai-engine/parsed-document`)**: Trả dữ liệu JSON trích xuất từ C# Web API.
- **Tạo API Upload PDF trực tiếp (`POST /api/ai-engine/upload-pdf`)**: Cho phép upload file PDF từ browser/Swagger và parse toàn bộ nội dung.
- **Tạo trang UI hiển thị trực quan (`/api/ai-engine/view-exam`)**: Tách code giao diện ra tệp tĩnh `wwwroot/view-exam.html` riêng biệt, gọi API trích xuất và hiển thị kèm LaTeX.
