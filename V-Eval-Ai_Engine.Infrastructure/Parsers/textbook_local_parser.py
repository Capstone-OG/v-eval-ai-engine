import sys
import os
import json
import pymupdf # type: ignore
import numpy as np

# Reconfigure stdout/stderr to use UTF-8 for console output on Windows
if sys.platform.startswith('win'):
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')

worker_ocr_engine = None

def get_worker_ocr():
    global worker_ocr_engine
    if worker_ocr_engine is None:
        try:
            from rapidocr_onnxruntime import RapidOCR # type: ignore
            # max_side_len=640 tối ưu tốc độ và độ chính xác cho trang scan
            worker_ocr_engine = RapidOCR(max_side_len=640)
        except Exception:
            worker_ocr_engine = False
    return worker_ocr_engine if worker_ocr_engine is not False else None

def parse_textbook_local(pdf_path):
    if not os.path.exists(pdf_path):
        return {"error": f"File not found: {pdf_path}"}

    doc = pymupdf.open(pdf_path)
    total_pages = len(doc)
    page_texts = []
    total_chars = 0

    for page_idx in range(total_pages):
        page_num = page_idx + 1
        page = doc[page_idx]
        
        # 1. Trích xuất text thuần local siêu tốc từ PDF Searchable
        text = page.get_text("text") or ""
        lines = [line.strip() for line in text.splitlines() if line.strip()]
        clean_text = "\n".join(lines)
        is_scanned = len(clean_text) < 5

        # 2. Nếu rỗng -> PDF Scan dạng HÌNH ẢNH -> Dùng RapidOCR trực tiếp qua raw buffer numpy
        if is_scanned:
            ocr = get_worker_ocr()
            if ocr:
                try:
                    pix = page.get_pixmap(dpi=80)
                    img_np = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, pix.n)
                    if pix.n == 4:
                        img_np = img_np[:, :, :3]
                    result, _ = ocr(img_np)
                    if result:
                        ocr_lines = [item[1].strip() for item in result if item[1] and item[1].strip()]
                        clean_text = "\n".join(ocr_lines)
                except Exception:
                    pass

        # Log tiến trình real-time ra sys.stderr tuần tự 100%
        status = "RapidOCR Scan" if is_scanned else "Text Direct"
        print(f"[PROGRESS] [Trang {page_num}/{total_pages}] Đang bóc tách ({status})...", file=sys.stderr, flush=True)

        if clean_text:
            page_texts.append({"page": page_num, "text": clean_text})
            total_chars += len(clean_text)

    doc.close()

    return {
        "total_pages": total_pages,
        "total_chars": total_chars,
        "pages": page_texts
    }

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Missing pdf_path argument"}, ensure_ascii=False))
        sys.exit(1)

    pdf_path = sys.argv[1]
    result = parse_textbook_local(pdf_path)
    print(json.dumps(result, ensure_ascii=False))
