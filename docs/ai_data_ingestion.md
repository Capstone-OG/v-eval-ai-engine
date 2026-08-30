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
