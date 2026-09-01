import re
import os
import json
import sys
import pymupdf # type: ignore

# Reconfigure stdout/stderr to use UTF-8 for console output on Windows
if sys.platform.startswith('win'):
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')

def clean_pua_symbols(text):
    if not text:
        return text
    replacements = {
        "\uf0a3": " \\le ",
        "\uf0b3": " \\ge ",
        "\uf03c": " < ",
        "\uf03e": " > ",
        "\uf070": " \\pi ",
        "\uf0a2": "'",
        "\uf0e6": "(",
        "\uf0e8": ")",
        "\uf0e7": "(",
        "\uf0eb": ")",
    }
    for pua, std in replacements.items():
        text = text.replace(pua, std)
    return text

def extract_underlines_by_page(doc):
    """
    Trích xuất các phần gạch chân cho từng trang bằng tọa độ hình vẽ và chữ.
    Trả về dict: {page_number: [(underline_rect, phrase_text)]}
    """
    underlines_by_page = {}
    for page_idx in range(len(doc)):
        page_num = page_idx + 1
        page = doc[page_idx]
        drawings = page.get_drawings()
        words = page.get_text("words")
        
        underline_rects = []
        for d in drawings:
            r = d["rect"]
            w_d = r.x1 - r.x0
            h_d = r.y1 - r.y0
            if h_d < 3 and w_d > 3 and 50 < r.y0 < 800:
                underline_rects.append(r)
                
        page_underlines = []
        for ur in underline_rects:
            underlined_words = []
            for w in words:
                wx0, wy0, wx1, wy1, word_text = w[:5]
                overlap_x = max(0, min(ur.x1, wx1) - max(ur.x0, wx0))
                word_w = wx1 - wx0
                is_vertical_aligned = abs(ur.y0 - wy1) <= 3 or (wy0 <= ur.y0 <= wy1 + 2)
                
                if is_vertical_aligned and (overlap_x > 0.4 * word_w or overlap_x > 0.4 * (ur.x1 - ur.x0)):
                    underlined_words.append((wx0, word_text))
                    
            underlined_words.sort(key=lambda x: x[0])
            phrase = " ".join([w[1] for w in underlined_words])
            if phrase:
                page_underlines.append((ur, phrase))
                
        page_underlines.sort(key=lambda x: (x[0].y0, x[0].x0))
        underlines_by_page[page_num] = page_underlines
        
    return underlines_by_page

def get_question_y_coords_by_page(doc):
    """
    Tìm tọa độ y của từng tiêu đề câu hỏi (Ví dụ: Câu 26, Question 26) trên từng trang.
    Trả về dict: {page_number: [(question_number, y0)]}
    """
    coords_by_page = {}
    for page_idx in range(len(doc)):
        page_num = page_idx + 1
        page = doc[page_idx]
        words = page.get_text("words")
        
        q_coords = []
        for i, w in enumerate(words):
            text = w[4]
            if text.lower() in ["câu", "question"] and i + 1 < len(words):
                next_w = words[i+1]
                next_text = next_w[4]
                m = re.match(r"^(\d+)[:.]?$", next_text)
                if m:
                    q_num = int(m.group(1))
                    q_coords.append((q_num, w[1]))
                    
        q_coords.sort(key=lambda x: x[1])
        coords_by_page[page_num] = q_coords
    return coords_by_page

def replace_underlines(content, underlines):
    """
    Thay thế các cụm từ gạch chân bằng tag HTML <u> kèm nhãn (A), (B), (C), (D)...
    """
    labels = ["A", "B", "C", "D", "E", "F"]
    current_pos = 0
    normalized_content = content
    
    for idx, phrase in enumerate(underlines):
        if idx >= len(labels):
            break
        label = labels[idx]
        
        escaped_phrase = re.escape(phrase)
        pattern_str = ""
        if phrase[0].isalnum():
            pattern_str += r'\b'
        pattern_str += escaped_phrase
        if phrase[-1].isalnum():
            pattern_str += r'\b'
            
        match = re.search(pattern_str, normalized_content[current_pos:])
        if match:
            start_match = current_pos + match.start()
            end_match = current_pos + match.end()
            replacement = f"<u>{phrase}</u> ({label})"
            normalized_content = normalized_content[:start_match] + replacement + normalized_content[end_match:]
            current_pos = start_match + len(replacement)
        else:
            match = re.search(re.escape(phrase), normalized_content[current_pos:])
            if match:
                start_match = current_pos + match.start()
                end_match = current_pos + match.end()
                replacement = f"<u>{phrase}</u> ({label})"
                normalized_content = normalized_content[:start_match] + replacement + normalized_content[end_match:]
                current_pos = start_match + len(replacement)
                
    return normalized_content

def parse_exam_pdf(file_path):
    """
    Parse cấu trúc đề thi ĐGNL 120 câu:
    Tách Passage (Đoạn văn đọc hiểu/ngữ cảnh) và Questions (Câu hỏi đơn lẻ hoặc trong chùm).
    Tách các đáp án A, B, C, D của từng câu.
    """
    if not os.path.exists(file_path):
        raise FileNotFoundError(f"Không tìm thấy file: {file_path}")
        
    doc = pymupdf.open(file_path)
    underlines_by_page = extract_underlines_by_page(doc)
    q_coords_by_page = get_question_y_coords_by_page(doc)
    full_text = ""
    for i, page in enumerate(doc):
        text = page.get_text()
        if text:
            full_text += f"\n--- PAGE_{i+1} ---\n" + clean_pua_symbols(text) + "\n"
            
    # Chuẩn hóa khoảng trắng và dòng
    # Thay thế nhiều dòng trống bằng 1 dòng trống
    normalized_text = re.sub(r'\n+', '\n', full_text)
    
    # 1. Tìm các chùm câu hỏi (Passages)
    # Định dạng: "Dựa vào thông tin dưới đây để trả lời các câu từ X đến Y" hoặc tương đương tiếng Anh
    passage_pattern = re.compile(
        r"(Dựa vào thông tin dưới đây để trả lời các câu từ\s+(\d+)\s+đến\s+(\d+)|Questions\s+(\d+)-(\d+):\s*Read the passage carefully\.?)\s*\n(.*?)(?=(?:Câu\s+\d+:|Question\s+\d+:|$))",
        re.DOTALL | re.IGNORECASE
    )
    
    passages = []
    passage_matches = list(passage_pattern.finditer(normalized_text))
    
    # Lưu lại khoảng vị trí của các passage để loại bỏ khi parse câu hỏi đơn lập
    passage_ranges = []
    
    for match in passage_matches:
        start_q = int(match.group(2) or match.group(4))
        end_q = int(match.group(3) or match.group(5))
        content = match.group(6).strip()
        
        passages.append({
            "start_question": start_q,
            "end_question": end_q,
            "content": content,
            "questions": []
        })
        passage_ranges.append((match.start(), match.end()))
        
    # 2. Tìm tất cả các câu hỏi
    # Định dạng: "Câu X:" hoặc "Question X:"
    question_pattern = re.compile(
        r"(Câu\s+(\d+):|Question\s+(\d+):)\s*(.*?)(?=(?:Câu\s+\d+:|Question\s+\d+:|Dựa vào thông tin|Questions\s+\d+-\d+:|--------------- HẾT ---------------|$))",
        re.DOTALL | re.IGNORECASE
    )
    
    all_questions = []
    question_matches = list(question_pattern.finditer(normalized_text))
    
    for match in question_matches:
        q_num = int(match.group(2) or match.group(3))
        q_body_raw = match.group(4).strip()
        
        # Tách nội dung câu hỏi và các lựa chọn A, B, C, D
        # Thường có dạng: <Nội dung> \n A. <A> B. <B> \n C. <C> D. <D>
        # Hoặc tất cả trên cùng dòng
        opt_pattern = re.compile(r"([A-D])\.\s+(.*?)(?=\s+[A-D]\.\s+|$)", re.DOTALL)
        
        options = {}
        content_only = q_body_raw
        
        # Thử tìm các lựa chọn A, B, C, D trong body câu hỏi
        opt_matches = list(opt_pattern.finditer(q_body_raw))
        if opt_matches:
            # Lấy phần text trước lựa chọn đầu tiên làm nội dung câu hỏi
            first_opt_idx = opt_matches[0].start()
            content_only = q_body_raw[:first_opt_idx].strip()
            
            for opt_match in opt_matches:
                opt_key = opt_match.group(1).upper()
                opt_val = opt_match.group(2).strip()
                # Loại bỏ ký tự xuống dòng thừa trong lựa chọn
                opt_val = re.sub(r'\s+', ' ', opt_val)
                # Auto-wrap math if it has LaTeX symbols
                if any(x in opt_val for x in ["\\le", "\\ge", "\\pi", "<", ">", "="]):
                    opt_val = f"${opt_val}$"
                options[opt_key] = opt_val
        else:
            # Check for error identification type (e.g. A B C D without dots at the end)
            error_corr_match = re.search(r"(\n|\s)+A\s+B\s+C\s+D\s*$", q_body_raw, re.IGNORECASE)
            if error_corr_match:
                content_only = q_body_raw[:error_corr_match.start()].strip()
                options = {
                    "A": "A",
                    "B": "B",
                    "C": "C",
                    "D": "D"
                }
        
        # Tìm page_number bằng cách quét xem marker PAGE_X nào đứng trước vị trí của câu hỏi
        q_pos = match.start()
        page_num = 1
        page_matches = list(re.finditer(r"--- PAGE_(\d+) ---", normalized_text))
        for pm in page_matches:
            if pm.start() < q_pos:
                page_num = int(pm.group(1))
            else:
                break

        q_underlines = []
        page_coords = q_coords_by_page.get(page_num, [])
        page_unds = underlines_by_page.get(page_num, [])
        
        this_q_y = None
        next_q_y = None
        for idx, (num, y0) in enumerate(page_coords):
            if num == q_num:
                this_q_y = y0
                if idx + 1 < len(page_coords):
                    next_q_y = page_coords[idx+1][1]
                break
                
        if this_q_y is not None:
            y_start = this_q_y - 5
            y_end = next_q_y - 5 if next_q_y is not None else 800
            
            for ur, phrase in page_unds:
                if y_start <= ur.y0 <= y_end:
                    q_underlines.append(phrase)
                    
        if q_underlines:
            content_only = replace_underlines(content_only.strip(), q_underlines)

        question_obj = {
            "question_number": q_num,
            "page_number": page_num,
            "content": content_only.strip(),
            "options": options if options else {
                "A": "Chưa trích xuất được lựa chọn A",
                "B": "Chưa trích xuất được lựa chọn B",
                "C": "Chưa trích xuất được lựa chọn C",
                "D": "Chưa trích xuất được lựa chọn D"
            }
        }
        
        # Kiểm tra xem câu hỏi này thuộc chùm câu hỏi (passage) nào không
        belongs_to_passage = False
        for p in passages:
            if p["start_question"] <= q_num <= p["end_question"]:
                p["questions"].append(question_obj)
                belongs_to_passage = True
                break
                
        if not belongs_to_passage:
            all_questions.append(question_obj)
            
    # Sắp xếp các câu hỏi và chùm câu hỏi theo thứ tự tăng dần
    all_questions.sort(key=lambda x: x["question_number"])
    passages.sort(key=lambda x: x["start_question"])
    
    return {
        "format": "V-ACT Exam",
        "file_name": os.path.basename(file_path),
        "total_pages": len(doc),
        "total_passages": len(passages),
        "total_single_questions": len(all_questions),
        "passages": passages,
        "single_questions": all_questions
    }

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Thiếu đối số đường dẫn file PDF."}), file=sys.stderr)
        sys.exit(1)
        
    pdf_path = sys.argv[1]
    
    try:
        exam_data = parse_exam_pdf(pdf_path)
        print(json.dumps(exam_data, ensure_ascii=False, indent=2))
    except Exception as e:
        print(json.dumps({"error": str(e)}), file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
