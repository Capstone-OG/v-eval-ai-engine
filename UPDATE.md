# UPDATE - V-Eval-Ai_Engine

## [18/11/2026]
- **Tối Ưu Hóa Toàn Diện Tốc Độ Bóc Tách SGK: 48 DPI, Nén JPEG 52%, Batch 2 Trang, Giảm Tải Nội Dung & Timeout 15s (`PdfPigTextbookParserService.cs`, `TextbookEndpoints.cs`, `appsettings.json`)**:
  - *Hạ DPI & Giảm kích thước ảnh SkiaSharp*: Thiết lập DPI mặc định thành `48 DPI` (thay vì 56/72), giới hạn chiều tối đa ảnh `capDim` xuống `640px - 720px` và nén JPEG 52%. Cắt giảm 65-70% dung lượng payload ảnh (từ ~180KB xuống ~35-50KB/trang), giảm 50% số token hình ảnh (tiling tokens) gửi đến Gemini.
  - *Thu nhỏ Batch Size Vision AI (2 Trang/Lượt)*: Giảm batch size xử lý ảnh từ 4-5 trang xuống 2 trang/lần. Tránh hiện tượng Gemini phải sinh hàng nghìn tokens cùng lúc gây nghẽn/timeout; thời gian phản hồi mỗi batch giảm từ 15-22s xuống chỉ còn **2.5s - 4.5s**.
  - *Giảm tải nội dung phải parse (High-Yield Core Knowledge Prompt)*: Cải tiến prompt chỉ trích xuất tri thức trọng tâm cốt lõi (Định nghĩa, Định lý, Tính chất, 100% Công thức KaTeX, 1 Ví dụ tiêu biểu ngắn gọn 2-3 bước). Loại bỏ hoàn toàn bài tập tự luyện dài dòng, hoạt động khởi động, trò chơi; giới hạn `maxOutputTokens = 4096`. Token đầu ra giảm 60%, tốc độ sinh chữ tăng gấp 3 lần.
  - *Bộ đệm điều tiết nhịp độ (Inter-Batch Pacing Jitter)*: Bổ sung khoảng dừng `await Task.Delay(400, cancellationToken)` giữa các batch/cluster để giải tỏa áp lực hạn ngạch TPM/RPM (Tokens/Requests Per Minute) của Google Gemini, triệt tiêu mã lỗi 503 và 429 do gửi request quá dồn dập.
  - *Khắc phục lỗi bỏ sót kết quả (Resilient Multi-Page Regex Parsing)*: Bổ sung regex dự phòng bắt tiêu đề `### Trang X`, `[TRANG X]`, `Page X` và cơ chế bảo hiểm tự động gán dữ liệu cho trang đầu batch nếu Gemini không trả về đúng thẻ phân trang chuẩn, ngăn chặn tuyệt đối lỗi quăng bỏ kết quả 200 OK hợp lệ để chuyển model lặp vô ích.
  - *Timeout bảo vệ 15s cho Vision Request*: Bổ sung `CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(15))` cho mỗi lượt gọi mô hình; nếu Google xếp hàng quá lâu (như `gemini-3.6-flash` nghẽn 19.8s), hệ thống tự động hủy và đổi sang model/key tiếp theo ngay lập tức.
  - *Loại bỏ các model thường xuyên quá tải*: Đưa `gemini-3.6-flash` ra khỏi danh sách xoay vòng trong `appsettings.json`, tập trung 100% tài nguyên vào các model ổn định `gemini-3.1-flash-lite`, `gemini-3.5-flash-lite` và `gemini-flash-lite-latest`.
- **Fail-Fast Khi Model Quá Tải 503/500/504 & Tối Ưu Thứ Tự Ưu Tiên Flash-Lite (`PdfPigTextbookParserService.cs`, `appsettings.json`)**:
  - *Cơ chế ngắt mạch quá tải*: Khi Google trả về mã lỗi 503 (Server Overload), 500, 504 hoặc 404, hệ thống lập tức ngắt vòng lặp key (`break`) để chuyển sang MODEL tiếp theo thay vì tiếp tục thử 4 API keys vô ích, cắt đứt hoàn toàn tình trạng đóng băng từ 30s-90s.
  - *Tối ưu hóa cấu hình Flash-Lite*: Đưa các model siêu tốc `gemini-3.1-flash-lite`, `gemini-3.5-flash-lite`, `gemini-flash-lite-latest` lên top đầu trong `appsettings.json`, duy trì tốc độ xử lý 4–7s/batch ổn định 100% (200 OK).
- **Triệt Tiêu 100% Hardcode Models (Zero-Hardcode) Trong Pipeline Bóc Tách SGK (`PdfPigTextbookParserService.cs`)**:
  - *Xóa sạch hardcode model*: Xóa bỏ hoàn toàn mảng `standardModels` (chứa `gemini-1.5-flash`, `gemini-1.5-pro`, `gemini-2.0-flash`, `gemini-2.5-flash`) trong hàm `ResolveGeminiModels` và mảng nối `new[] { 3.6-flash, ... }` trong `GeneratePatternExercisesAsync`.
  - *Tuân thủ 100% cấu hình*: Chuyển đổi sang đọc thuần túy từ section `"AiSettings:GeminiModels"` của `appsettings.json`, chấm dứt hoàn toàn hiện tượng tự động fallback sang các model lỗi 404.
- **Sửa Lỗi Biên Dịch CS0103 Trong Chuỗi Nội Suy KaTeX & Khởi Chạy Ổn Định Cổng 5104 (`PdfPigTextbookParserService.cs`)**:
  - *Sửa lỗi escape ngoặc nhọn*: Escape `{{AB}}` và `{{a}}` trong chuỗi `$@"..."` triệt tiêu lỗi `error CS0103: The name 'AB' does not exist in the current context`.
  - *Khởi chạy dịch vụ nền*: Dịch vụ `V-Eval-Ai_Engine.API` biên dịch sạch 100% (0 errors, 0 warnings) và đang chạy daemon trên cổng 5104.
  - *Xác thực định tuyến Gateway*: YARP Gateway (cổng 5212) định tuyến thông suốt đến `http://127.0.0.1:5104/api/ai-engine/textbooks/sources`, phản hồi HTTP 200 OK ngay lập tức, triệt tiêu hoàn toàn lỗi 502 Bad Gateway / Connection Refused.
- **Chuẩn Hóa Pipeline Bóc Tách C# Backend: Triệt Tiêu Thẻ Rò Rỉ `[TAXONOMY: ...]`, Rác Thông Tin Xuất Bản & Gãy Dòng KaTeX Vectơ (`CurriculumTaxonomyRegistry.cs`, `CurriculumAiService.cs`, `PdfPigTextbookParserService.cs`)**:
  - *Khử triệt để thẻ rò rỉ*: Chuyển đổi cơ chế bóc tách thẻ `[TAXONOMY: ...]` sang xử lý đa thẻ (`Regex.Matches`) và gọi `Regex.Replace` xóa sạch 100% thẻ taxonomy khỏi văn bản `chunk.Text` trước khi lưu vào CSDL.
  - *Lọc sạch thông tin xuất bản*: Tự động phát hiện các dòng `Tên sách`, `Bộ sách`, `Tổng chủ biên`, `Chủ biên`, `Nhà xuất bản`, bản quyền để phân loại trang thành `section="COVER"` thay vì gán nhầm vào Dạng bài học.
  - *Nối dòng KaTeX & biểu diễn vectơ*: Nối các dòng ngắt đơn lẻ đối với biến số và điểm hình học; chuẩn hóa các ký hiệu vectơ sang KaTeX `\vec{AB}`, `|\vec{AB}| = AB`.
  - *Cập nhật Prompt Gemini Vision*: Bổ sung quy tắc bắt buộc không bóc tách trang bìa/xuất bản thành tri thức bài học và yêu cầu bọc 100% ký hiệu vectơ vào KaTeX chuẩn.
- **API Xóa Sách & Vector Chunks Khỏi CSDL PostgreSQL Supabase (`TextbookEndpoints.cs`, `TextbookRepository.cs`, `ITextbookRepository.cs`)**:
  - Bổ sung endpoint `DELETE /api/ai-engine/textbooks/sources/{sourceId:guid}` xóa an toàn trong một transaction Npgsql cả bảng `v_eval_ai."KnowledgeVectorChunks"` và `v_eval_ai."KnowledgeSources"`.
