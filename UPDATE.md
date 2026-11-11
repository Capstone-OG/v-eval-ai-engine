# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE SERVICE

## [12/11/2026] - Khắc Phục Triệt Để Vòng Lặp Retry 503 (Key-First Smart Failover), Tinh Gọn Prompt Bóc Tách Đa Bài Học & Chuẩn Hóa KaTeX Hóa Học

- **Khắc Phục Triệt Để Cơ Chế Vòng Lặp Thử Lại (Smart Key-First & Model-Inner Failover) ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`MultiCloudExamParserService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/MultiCloudExamParserService.cs))**:
  - **Đảo ngược thứ tự vòng lặp**: Vòng lặp ngoài duyệt theo từng Key trong `apiKeys`, vòng lặp trong duyệt qua danh sách các Model trong `geminiModels`.
  - **Phân loại lỗi chính xác**:
    - Khi gặp lỗi Key/Quota (`HTTP 401 Unauthorized`, `HTTP 403 Forbidden`, `HTTP 429 Too Many Requests`): Đánh dấu `keyInvalidOrQuotaExceeded = true` và `break` model loop để chuyển sang API Key tiếp theo.
    - Khi gặp lỗi Model/Server quá tải (`HTTP 503 Service Unavailable`, `HTTP 500`, `HTTP 504`, `HTTP 404`, `HTTP 400`): Giữ nguyên API Key và `continue` model loop để thử ngay Model tiếp theo với cùng key. Triệt tiêu hoàn toàn hiện tượng thử lại 4 lần cùng 1 model bị 503.
  - **Rút ngắn Timeout từng lượt**: Hạ timeout xuống `35 giây` (`TimeSpan.FromSeconds(35)`), bắt ngoại lệ `OperationCanceledException` để chuyển model tức thì.

- **Tinh Gọn Directive Bóc Tách & Prompt Theo Nhánh Main ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs), [`CurriculumIngestion.prompt.txt`](./V-Eval-Ai_Engine.Infrastructure/Templates/Prompts/CurriculumIngestion.prompt.txt))**:
  - Giữ vững nguyên tắc đa bài học (Multi-Lesson Extraction): Chương "Ester - Lipid" bắt buộc tách thành 2 bài học riêng biệt ("Bài 1: Ester" và "Bài 2: Lipid & Chất béo").
  - Cân bằng số lượng dạng bài: Mỗi bài học trích xuất 2-3 dạng bài cốt lõi (tổng 4-6 dạng bài), mỗi dạng có 2-3 câu hỏi trắc nghiệm kèm giải thích chi tiết, đầy đủ 4 khối sư phạm. Loại bỏ việc ép bóc tách 12+ dạng bài gây quá tải token kích hoạt lỗi 503 từ Google AI Studio.

- **Cấu Hình Thứ Tự Ưu Tiên Gemini Chống 503 ([`appsettings.json`](./V-Eval-Ai_Engine.API/appsettings.json), [`Configs/V-Eval-Ai_Engine/appsettings.json`](../../Configs/V-Eval-Ai_Engine/appsettings.json))**:
  - Ưu tiên số 1: `gemini-3.5-flash-lite` (~1.0s latency, chịu tải cao, không bị 503).
  - Tiếp nối: `gemini-3.6-flash`, `gemini-3.5-flash`, `gemini-3.1-flash-lite`, `gemini-flash-lite-latest`.
  - Loại bỏ hoàn toàn `gemini-3.8-flash` (gây 503 liên tục).

- **Chuẩn Hóa KaTeX Backend ([`CurriculumAiService.cs`](./V-Eval-Ai_Engine.Infrastructure/Services/CurriculumAiService.cs))**:
  - Xóa bỏ dấu `$` đặt nhầm cạnh dấu bằng `\$\s*=\s*\$?` trong công thức tính toán.
  - Tự động đóng dấu `$` trước dấu chấm câu khi có văn bản giải thích: `$C_{17}H_{33}$. 1 mol` thay vì nuốt vào math mode.
  - Định dạng phương trình hóa học độc lập theo dòng chuẩn xác.
  - **19/19 unit tests pass 100%** trong 309 ms. Rebuild và triển khai thành công container `v_eval_ai_engine`.
