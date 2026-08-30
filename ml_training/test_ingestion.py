import os
import json
import sys

# Reconfigure stdout/stderr to use UTF-8 for console output on Windows
if sys.platform.startswith('win'):
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')

# Đảm bảo import được document_parser trong cùng thư mục
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import document_parser

def main():
    print("=== BẮT ĐẦU CHẠY THỬ NGHIỆM INGESTION DỮ LIỆU ===")
    
    # 1. Đường dẫn các file tài nguyên kiểm thử
    pdf_path = r"e:\CapStone\docs\Nghiệp Vụ ThinhTT.pdf"
    docx_path = r"e:\CapStone\docs\FA26SE090_AI_POWERED_PERSONALIZED_LEARNING_PATH_PL_taint.docx"
    
    results = {}
    
    # 2. Thử nghiệm đọc file PDF
    if os.path.exists(pdf_path):
        print(f"\n[INFO] Đang đọc file PDF: {pdf_path}")
        try:
            pdf_data = document_parser.parse_pdf(pdf_path)
            print(f"[SUCCESS] Đọc PDF thành công. Số trang: {pdf_data['total_pages']}")
            # Lưu lại một phần dữ liệu
            results["pdf_document"] = {
                "file_name": pdf_data["file_name"],
                "total_pages": pdf_data["total_pages"],
                "sample_content_page_1": pdf_data["pages"][0]["text"][:2000] if pdf_data["pages"] else ""
            }
        except Exception as e:
            print(f"[ERROR] Lỗi khi đọc PDF: {e}")
            results["pdf_document"] = {"error": str(e)}
    else:
        print(f"\n[WARNING] Không tìm thấy file PDF mẫu ở đường dẫn: {pdf_path}")
        
    # 3. Thử nghiệm đọc file DOCX
    if os.path.exists(docx_path):
        print(f"\n[INFO] Đang đọc file DOCX: {docx_path}")
        try:
            docx_data = document_parser.parse_docx(docx_path)
            print(f"[SUCCESS] Đọc DOCX thành công. Số đoạn văn: {docx_data['paragraphs_count']}, Số bảng biểu: {len(docx_data['tables'])}")
            
            # Lưu lại một phần dữ liệu
            results["docx_document"] = {
                "file_name": docx_data["file_name"],
                "paragraphs_count": docx_data["paragraphs_count"],
                "tables_count": len(docx_data["tables"]),
                "sample_paragraphs": [p["text"] for p in docx_data["paragraphs"][:10]],
                "sample_tables": docx_data["tables"][:2]
            }
        except Exception as e:
            print(f"[ERROR] Lỗi khi đọc DOCX: {e}")
            results["docx_document"] = {"error": str(e)}
    else:
        print(f"\n[WARNING] Không tìm thấy file DOCX mẫu ở đường dẫn: {docx_path}")
        
    # 4. Lưu toàn bộ kết quả trích xuất ra file JSON để C# API sử dụng hiển thị
    output_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "parsed_output.json")
    print(f"\n[INFO] Đang lưu kết quả trích xuất vào: {output_path}")
    
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
        
    print("\n=== THỬ NGHIỆM INGESTION HOÀN TẤT THÀNH CÔNG ===")

if __name__ == "__main__":
    main()
