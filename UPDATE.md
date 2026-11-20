# UPDATE - V-Eval-Ai_Engine

## [21/11/2026]
- **Thuật Toán Line-Following Chống Xé Chữ & Hỗ Trợ Đầy Đủ Câu Hỏi Số Trần (`PdfPigLocalExamParser.cs`, `PdfPigLocalExamParserTests.cs`)**:
  - `ExtractOrderedPageText`: Chuyển sang thuật toán **Line-Following (Nearest-Neighbor Horizontal Chain)** ghép từ trái sang phải dựa vào lân cận gần nhất (khoảng cách ngang `dx <= 65pt`, độ lệch trục `dy <= yTol`). Triệt tiêu hoàn toàn hiện tượng xé lẻ chữ cái đơn (`o`, `g`, `n`, `u`, `V`, `i`, `đ`) và chống trộn dòng scan nghiêng.
  - `QuestionPattern` & `PassagePattern`: Mở rộng nhận diện câu hỏi dạng số trần (vd: `91.`, `92.`, `118.`, `119.`, `120.` không có tiền tố "Câu"), chặn chùm đọc hiểu (`Passage`) nuốt câu hỏi vào thân đoạn văn (giải quyết lỗi Passage 118–120 hiển thị "0 câu").
  - `CleanPuaSymbols`: Tự động chuẩn hóa `\circC` thành `^\circ\text{C}` và phục hồi ký hiệu độ C.
  - Thêm unit test `ParseExamText_ShouldHandleBareNumberedQuestionsAndPassagesWithoutSwallowing` kiểm chứng trích xuất 100% câu 91–92 và chùm 118–120.
- **Kiểm Thử**: `dotnet build --no-restore` `0 Error(s)`, `dotnet test --no-build` `65/65 Passed` (chưa deploy Docker theo yêu cầu).
