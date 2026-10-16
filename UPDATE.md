# NHẬT KÝ CẬP NHẬT (UPDATE LOG) - AI ENGINE

## [16/10/2026] - Nâng Cấp Cây Tri Thức Đa Tầng 16 Dạng Bài, Làm Sâu Sắc Lý Thuyết Nền Tảng (Khối 1), Bổ Sung Ngân Hàng 4-5 Câu Vận Dụng (L3/L4) Mỗi Dạng Bài

- **Làm Sâu Sắc Kiến Thức Nền Tảng Khối 1 (`EnrichGeneralKnowledgeTheory`)**:
  - Tự động bổ sung bản chất khoa học chuẩn SGK GDPT 2018 đa đoạn văn, 4 quy tắc vàng xác định số oxi hóa, bảng các trường hợp ngoại lệ đặc biệt ($H_2O_2$, $KO_2$, $OF_2$, $NaH$, hợp chất hữu cơ), các lưu ý thi cử ĐGNL trọng tâm và ví dụ SGK minh họa đầy đủ.
- **Mở Rộng Cây Tri Thức Đa Tầng 16 Dạng Bài & 5 Bài Học (`GenerateHeuristicCurriculum`)**:
  - Mở rộng cấu trúc chương học lên 16 dạng bài chuyên sâu trải dài 5 bài học (Hóa học) và 11 dạng bài qua 4 bài học (Toán học), bao phủ toàn diện từ xác định số oxi hóa, cân bằng ion-electron, bảo toàn electron nâng cao đến bài toán thực tế điện hóa và ăn mòn kim loại.
- **Ngân Hàng Câu Hỏi Sư Phạm Chuyên Sâu 14 Câu/Dạng Bài (Tổng 224 Câu) (`EnrichPatternPedagogyAndQuestions`)**:
  - Thiết lập chuẩn tối thiểu 14 câu hỏi trắc nghiệm/dạng bài: 2 câu L1 (Nhận biết), 2 câu L2 (Thông hiểu), 5 câu L3 (Vận dụng) và 5 câu L4 (Vận dụng cao).
  - Tự động sinh đầy đủ đề bài KaTeX, 4 phương án lựa chọn A-B-C-D, đáp án đúng và lời giải chi tiết KaTeX từng bước cho toàn bộ 16 dạng bài (tổng 224 câu hỏi).
- **Tối Ưu Hóa & Khắc Phục Lỗi Deserialization / Chuỗi C#**:
  - Làm sạch chuỗi JSON trong `CleanJsonFences`, xử lý triệt để ký tự escape LaTeX (`\%` $\to$ `%`, `\$` $\to$ `$`) ngăn ngừa lỗi `JsonException`.
  - Khắc phục lỗi nội suy chuỗi C# (`$` string interpolation) đối với các chuỗi KaTeX chứa `\text{ cm}`.
- **Biên Dịch & Kiểm Thử Vận Hành Thực Tế**:
  - Biên dịch .NET 9 SDK: **0 Error, 2 Warning**.
  - Kiểm thử trích xuất thực tế `python test_ai_extract.py`: Trích xuất thành công 16 dạng bài với **224 câu hỏi mẫu**, mỗi dạng bài chứa đúng 2 L1, 2 L2, 5 L3 và 5 L4 câu hỏi. Dịch vụ AI Engine đang chạy ổn định trên cổng `http://localhost:5104`.
