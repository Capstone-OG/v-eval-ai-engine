# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [18/10/2026] - API Sinh Câu Hỏi Rèn Luyện Thích Ứng Bám Sát Dạng Bài Đang Phân Tích (Zero-Hardcode & Anti-Duplication) & Domain-Aware Prompting (Loại Bỏ Mẹo Casio Trong Ngữ Văn)

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

- **Kiểm Thử Biên Dịch (Build Verification)**:
  - `V-Eval-Ai_Engine.Infrastructure` & `V-Eval-Ai_Engine.API`: Biên dịch C# .NET 9 Clean Architecture thành công 100% (**0 Error**).
