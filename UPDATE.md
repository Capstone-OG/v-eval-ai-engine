# Nhật Ký Cập Nhật (Update Log) - AI Engine

## [28/09/2026] - Hợp Nhất Hoàn Chỉnh Core Flow 1 (Diagnostic Psychometrics) & Core Flow 2 (Textbook RAG Ingestion)

- **Hợp Nhất Toàn Diện Nhánh `develop`**:
  - Giải quyết xung đột tài liệu và đồng bộ hóa toàn bộ mã nguồn giữa Core Flow 1 (Psychometrics IRT & BKT Engine) và Core Flow 2 (Textbook RAG Ingestion, Supabase Schema & Rich Visualization).
- **Core Flow 1 — Đánh Giá Năng Lực Đầu Vào & Phân Lớp (Diagnostic & Placement Engine)**:
  - **Module Tính Toán Năng Lực IRT 2PL & BKT Prior (`rag-service/diagnostic_engine.py`)**:
    - Thuật toán ước lượng năng lực học sinh `theta_0` bằng mô hình IRT 2PL kết hợp MAP Estimation (Gaussian Prior `N(0, 2.0^2)`), tối ưu hóa bằng Brent's method (`scipy.optimize.minimize_scalar`).
    - Ngăn ngừa lạm phát năng lực do đoán mò dưới 5 giây (`a -> 0.1`).
    - Tính toán xác suất thành thạo ban đầu BKT Prior `P(L0) = Sigmoid(theta)` kẹp an toàn `[0.05, 0.95]`.
    - Xử lý unhappy case: tự động suy diễn `P(L0)` từ năng lực miền (`domain_level`) cho các kỹ năng chưa xuất hiện trong 30 câu hỏi chẩn đoán.
    - Phân loại xếp lớp chuẩn mực 3 mức: `FOUNDATION` (`theta < -0.5`), `ACCELERATION` (`-0.5 <= theta <= 0.5`), `BREAKTHROUGH` (`theta > 0.5`).
    - Dựng tọa độ biểu đồ Radar so sánh năng lực học sinh với điểm chuẩn benchmark dựa trên mục tiêu điểm thi (V-ACT target score).
  - **REST Endpoints & Socratic Commentary (`rag-service/routers/diagnostic.py`)**:
    - `POST /api/v1/diagnostic/analyze`: Tiếp nhận kết quả bài chẩn đoán, tính toán psychometrics và gọi Gemini (`gemini-3.6-flash`) tạo nhận xét sư phạm cá nhân hóa.
    - `GET /api/v1/diagnostic/config`: Cung cấp cấu hình ngưỡng phân lớp và tham số IRT.
  - **Tài Liệu Đặc Tả Toán Học**:
    - Biên soạn toàn diện [`docs/cong_thuc_psychometrics_irt_bkt.md`](./docs/cong_thuc_psychometrics_irt_bkt.md) tổng hợp 9 mô hình toán học và lý giải bài toán V-ACT.
- **Core Flow 2 — Nạp Tri Thức SGK, Supabase `v_eval_ai` & Giao Diện Trực Quan Hóa Chunks**:
  - **Giao Diện Trực Quan Hóa Chunks Tri Thức (`view-textbook.html`)**:
    - Tích hợp `marked.js` và `katex` render bảng biểu Glassmorphic, công thức toán/lý/hóa inline (``$...$``) và block (``$$...$$``).
    - Bộ lọc real-time theo từ khóa/số trang, chuyển đổi chế độ `👁️ Trực Quan` và `📄 Raw Text`, xuất file `.MD` và sao chép chunks nhanh.
  - **Tự Động Phát Hiện & Khắc Phục Lỗi Font InDesign / CID Subsetting (Mojibake)**:
    - Thuật toán `IsCorruptedFontEncoding` với 3 tầng lọc tự động chuyển hướng sang Gemini Vision AI (DPI 96) cho văn bản sạch sẽ 100%.
    - Bổ sung tùy chọn `VISION_AI` và cờ `forceReingest` dọn sạch chunk rác cũ trong Supabase (`v_eval_ai."KnowledgeVectorChunks"`).
  - **Resumable Ingestion & Background Job Persistence**:
    - Checkpoint từng trang trực tiếp vào Supabase PostgreSQL, tự động tiếp tục nạp từ trang gián đoạn, tự động khôi phục giao diện Studio khi F5 hoặc quay lại.
- **Kiểm Thử & Nghiệm Thu**:
  - Đồng bộ và hoàn tất kiểm thử đơn vị, kiểm thử tích hợp 100% PASS, nghiệm thu kiến trúc tại [`docs/architecture_acceptance.md`](./docs/architecture_acceptance.md).
