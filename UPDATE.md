# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

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
- **Kiểm Thử Biên Dịch**:
  - `V-Eval-Ai_Engine.Infrastructure`: Biên dịch .NET 9 Clean Architecture đạt 100% (**0 Error, 0 Warning**).
