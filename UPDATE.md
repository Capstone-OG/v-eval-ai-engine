# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [28/09/2026] - Hợp Nhất Toàn Diện Core Flow 1 (Diagnostic Psychometrics & Exam Studio) & Core Flow 2 (Textbook RAG Ingestion & CryptoStream)

- **Hợp Nhất Toàn Diện & Đồng Bộ Kiến Trúc Giữa Hai Nhánh**:
  - Hợp nhất thành công mã nguồn và tài liệu giữa nhánh `origin/develop` và nhánh `ThinhTT/feat-diagnostic-exam-studio-bloom-flow`.
- **Core Flow 1 — Đánh Giá Năng Lực Đầu Vào, Psychometrics IRT & AI Exam Studio**:
  - **Chuẩn Hóa Thang Đo Tư Duy Bloom 6 Cấp Cho IRT 2PL (`diagnostic_engine.py`)**:
    - Nâng cấp ánh xạ độ khó IRT $b \in [-1.8, +2.2]$ tương ứng 6 cấp độ Bloom (Nhận biết $\rightarrow$ Sáng tạo).
    - Tích hợp thuật toán cực tiểu hóa thuần Python Golden Section Search làm fallback tự động khi môi trường thiếu `scipy`.
    - Ước lượng năng lực học sinh $\theta_0$ bằng mô hình IRT 2PL kết hợp MAP Estimation (Gaussian Prior), ngăn lạm phát do đoán mò.
    - Tính BKT Prior $P(L_0) = \text{Sigmoid}(\theta_0)$, xử lý suy diễn domain level cho kỹ năng vắng mặt, phân lớp chuẩn 3 mức: `FOUNDATION`, `ACCELERATION`, `BREAKTHROUGH`.
  - **Triển Khai Động Cơ Sinh Đề AI Chế Độ Kép (Dual-Engine AI Exam Studio) (`routers/diagnostic.py`)**:
    - Endpoint `POST /api/v1/diagnostic/generate-exam` hỗ trợ 2 chế độ: *Gemini Live Cloud* (sinh câu hỏi mới theo Bloom, chuẩn LaTeX, cân đối đáp án A/B/C/D) và *Fast Calibrated Bank* (< 0.1s offline fallback).
    - Tự động nhận diện ý định lĩnh vực (Toán, Văn, Logic, KHTN, KHXH) từ prompt và ưu tiên tuyệt đối lựa chọn Dropdown của giáo viên.
  - **Giao Diện Khảo Sát & Studio Trực Quan Hóa (`view-diagnostic.html` & `ExamEndpoints.cs`)**:
    - Cung cấp Web Runner UI trực quan bài thi 30 câu, vẽ biểu đồ Radar Chart và chạy thử nghiệm AI Exam Studio.
- **Core Flow 2 — Nạp Tri Thức SGK, Streaming CryptoStream & Supabase Persistence**:
  - **Tối Ưu Single-Pass CryptoStream & Khử Trùng Lặp Tệp (`TextbookEndpoints.cs`)**:
    - Tích hợp `CryptoStream` bọc ngoài `FileStream` khi upload SGK, tính mã băm SHA-256 đồng thời trong 1 lượt đọc, cắt giảm 50% Disk I/O cho file lớn (100MB-250MB).
    - Tự động dọn dẹp file tạm trùng lặp (`File.Delete`) khi tệp đang được tiến trình nền xử lý (`PROCESSING`).
    - Đồng bộ danh mục mô hình kiểm tra độ trễ Vision AI `/ping-vision` từ cấu hình `AiSettings:GeminiModels`.
  - **Trực Quan Hóa Chunks Tri Thức & Khắc Phục Lỗi Font InDesign (Mojibake)**:
    - Giao diện `view-textbook.html` render công thức KaTeX, bảng biểu glassmorphism, xuất file `.MD`.
    - Thuật toán `IsCorruptedFontEncoding` với 3 tầng lọc tự động chuyển hướng sang Gemini Vision AI cho văn bản sạch sẽ 100%.
- **Chuẩn Hóa Động Cơ Bóc Tách Đề Thi & Cấu Hình Timeout Linh Hoạt**:
  - Cập nhật danh mục 5 mô hình Gemini chuẩn: `gemini-flash-lite-latest`, `gemini-3.5-flash-lite`, `gemini-3.1-flash-lite`, `gemini-3.8-flash`, `gemini-3.6-flash`.
  - Cấu hình động `ExamParserTimeoutMinutes = 8`, `TextbookParserTimeoutMinutes = 10`, `MinQuestionThreshold`, `OpenAiBaseUrl`, `GeminiBaseUrl` và Python đa nền tảng qua `appsettings.json`.
- **Kiểm Thử & Nghiệm Thu Kiến Trúc**:
  - `dotnet build` giải pháp `V-Eval-Ai_Engine.sln` thành công 100% (**0 Error, 0 Warning** mới).
  - Hoàn tất hồ sơ nghiệm thu kiến trúc chi tiết tại [`docs/architecture_acceptance.md`](./docs/architecture_acceptance.md).
