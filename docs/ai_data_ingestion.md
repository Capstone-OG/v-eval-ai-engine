# Tài liệu Kỹ thuật Trích xuất Dữ liệu (AI Data Ingestion)

Tài liệu này đặc tả phương pháp đọc, xử lý và cấu trúc hóa dữ liệu từ các tài nguyên học tập đầu vào (PDF, DOCX, DOC, hình ảnh).

---

## 1. Phương pháp trích xuất theo loại định dạng

*   **PDF:** Sử dụng thư viện `pypdf` để phân tích các trang, lấy văn bản thô. Tương lai sẽ mở rộng thêm OCR hoặc Vision LLM cho các trang chứa nhiều công thức và hình vẽ phức tạp.
*   **DOCX:** Sử dụng thư viện `python-docx` để đọc cấu trúc các đoạn văn (Paragraphs) và bảng biểu (Tables).
*   **DOC cũ:** Dùng COM Automation (`win32com.client`) gọi trực tiếp Microsoft Word trên Windows để chuyển đổi sang `.docx` trước khi xử lý (hoặc cảnh báo yêu cầu người dùng lưu lại thành `.docx`).
*   **Hình ảnh:** Sử dụng `Pillow (PIL)` để kiểm tra các thông số hình ảnh cơ bản (kích thước, độ phân giải, định dạng) phục vụ hiển thị.

---

## 2. Cấu trúc dữ liệu đầu ra mẫu (JSON Schema)

Dữ liệu đề thi sau khi trích xuất sẽ được định dạng JSON để nạp vào DB:

```json
{
  "document_name": "Nghiệp Vụ ThinhTT.pdf",
  "extracted_at": "2026-08-30T16:00:00Z",
  "pages_count": 12,
  "content_summary": "Tóm tắt văn bản thô trích xuất từ tài liệu...",
  "questions": [
    {
      "question_index": 1,
      "content_latex": "Tìm giá trị cực đại của...",
      "options": {
        "A": "Đáp án A",
        "B": "Đáp án B",
        "C": "Đáp án C",
        "D": "Đáp án D"
      },
      "correct_option": "A",
      "explanation": "Lời giải chi tiết..."
    }
  ]
}
```

---

## 3. Quy trình Trích xuất qua Cloud Gemini API (Bản Ổn định)

Để giải quyết triệt để vấn đề nhận diện các công thức toán học LaTeX phức tạp, cấu trúc chùm câu hỏi đọc hiểu (passages), và các định dạng chữ in đậm/gạch chân trong đề thi ĐGNL, hệ thống đã tích hợp API của Google Gemini:
* **Mô hình sử dụng**: `gemini-2.5-flash` (Stable production).
* **Phương thức truyền dữ liệu**: Đọc trực tiếp byte tệp tin PDF và mã hóa sang chuỗi base64 với MIME type `application/pdf`. Dữ liệu được gửi lên Cloud để tận dụng bộ máy phân tích tài liệu đa phương thức (multimodal) gốc của Google, giúp loại bỏ hoàn toàn việc render ảnh cục bộ (giảm độ trễ CPU từ 3 phút xuống còn 0 giây).
* **Ràng buộc đầu ra (Structured Outputs)**: Định nghĩa JSON Schema đầu ra nghiêm ngặt trong API payload, ép buộc Gemini phải trả về dữ liệu đúng cấu trúc bao gồm chùm câu hỏi (`passages`), câu hỏi đơn lẻ (`single_questions`), phân loại dạng bài học tập (`suggested_skill_name`), và bọc các công thức toán học chính xác theo cú pháp LaTeX dấu đô-la `$ ... $`.
