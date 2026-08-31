import sys
import os
import json
import base64
import re
from concurrent.futures import ThreadPoolExecutor
import pymupdf # type: ignore

# Reconfigure stdout/stderr to use UTF-8 for console output on Windows
if sys.platform.startswith('win'):
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import exam_parser
import ai_post_processor
import local_post_processor
import subprocess
import shutil

import urllib.request
import urllib.error
import socket

def query_gemini_api(pdf_path, api_key):
    try:
        print("[AI Process] Đang đọc trực tiếp file PDF và mã hóa sang base64 để gửi sang Gemini...", file=sys.stderr)
        
        # 1. Đọc trực tiếp file PDF và chuyển sang base64
        with open(pdf_path, "rb") as f:
            pdf_bytes = f.read()
        pdf_base64 = base64.b64encode(pdf_bytes).decode("utf-8")
        
        print(f"[AI Process] Đọc file PDF thành công ({len(pdf_bytes)/1024:.1f} KB). Đang chuẩn bị payload gửi sang Gemini...", file=sys.stderr)
        
        prompt = (
            "Bạn là một chuyên gia chuyển đổi tài liệu đề thi sang cấu trúc JSON chuẩn.\n"
            "Hãy đọc toàn bộ tài liệu đề thi ĐGNL (Đánh giá năng lực) PDF này và trích xuất tất cả các câu hỏi.\n"
            "Yêu cầu:\n"
            "1. Trích xuất đúng cấu trúc câu hỏi đơn lẻ (single_questions) và chùm câu hỏi đọc hiểu (passages).\n"
            "2. Mọi công thức toán học, vật lý, hóa học, phương trình, biến số, hằng số (kể cả các ký hiệu chữ đơn lẻ như x, y, z, m, T, t) BẮT BUỘC phải được bao bọc bởi một cặp dấu đô-la đơn $...$ (ví dụ: viết $y = -x^3 + 3(m-1)x^2 + 6mx + 1$, $z = 3 - i$ hoặc $\\int_0^1 x dx$). Đảm bảo viết đúng các công thức phân số (dùng \\frac{a}{b}), chỉ số dưới (dùng m_0, t_0), chỉ số trên (dùng x^2), tránh viết rời rạc vô nghĩa như '0 m m 16 = .'. KHÔNG được để trống hoặc dùng chữ thường không có dấu $ cho công thức.\n"
            "3. Nếu trang có đồ thị, biểu đồ, hoặc sơ đồ hình vẽ, hãy thêm mô tả bằng chữ chi tiết về hình vẽ đó ngay dưới nội dung câu hỏi hoặc passage tương ứng (ví dụ: *([Hình vẽ]: Đồ thị parabol...)*) để người học nắm được thông tin.\n"
            "4. Điền đầy đủ bốn phương án A, B, C, D vào thuộc tính options. Đảm bảo toàn bộ câu hỏi đều được trích xuất đầy đủ từ trang đầu đến trang cuối.\n"
            "5. Phân tích nội dung từng câu hỏi để gợi ý tên dạng bài / kỹ năng tương ứng (suggested_skill_name), ví dụ: 'Thì động từ', 'Biện pháp tu từ', 'Phóng xạ hạt nhân', 'Cực trị hàm số', 'Đọc hiểu biểu đồ'..."
        )
        
        schema = {
          "type": "OBJECT",
          "properties": {
            "passages": {
              "type": "ARRAY",
              "items": {
                "type": "OBJECT",
                "properties": {
                  "start_question": {"type": "INTEGER"},
                  "end_question": {"type": "INTEGER"},
                  "content": {"type": "STRING"},
                  "questions": {
                    "type": "ARRAY",
                    "items": {
                      "type": "OBJECT",
                      "properties": {
                        "question_number": {"type": "INTEGER"},
                        "page_number": {"type": "INTEGER"},
                        "content": {"type": "STRING"},
                        "suggested_skill_name": {"type": "STRING"},
                        "options": {
                          "type": "OBJECT",
                          "properties": {
                            "A": {"type": "STRING"},
                            "B": {"type": "STRING"},
                            "C": {"type": "STRING"},
                            "D": {"type": "STRING"}
                          },
                          "required": ["A", "B", "C", "D"]
                        }
                      },
                      "required": ["question_number", "content", "options", "suggested_skill_name"]
                    }
                  }
                },
                "required": ["start_question", "end_question", "content", "questions"]
              }
            },
            "single_questions": {
              "type": "ARRAY",
              "items": {
                "type": "OBJECT",
                "properties": {
                  "question_number": {"type": "INTEGER"},
                  "page_number": {"type": "INTEGER"},
                  "content": {"type": "STRING"},
                  "suggested_skill_name": {"type": "STRING"},
                  "options": {
                    "type": "OBJECT",
                    "properties": {
                      "A": {"type": "STRING"},
                      "B": {"type": "STRING"},
                      "C": {"type": "STRING"},
                      "D": {"type": "STRING"}
                    },
                    "required": ["A", "B", "C", "D"]
                  }
                },
                "required": ["question_number", "content", "options", "suggested_skill_name"]
              }
            }
          },
          "required": ["passages", "single_questions"]
        }
        
        url = f"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={api_key}"
        
        payload = {
            "contents": [
                {
                    "parts": [
                        {
                            "inlineData": {
                                "mimeType": "application/pdf",
                                "data": pdf_base64
                            }
                        },
                        {
                            "text": prompt
                        }
                    ]
                }
            ],
            "generationConfig": {
                "responseMimeType": "application/json",
                "responseSchema": schema
            }
        }
        
        headers = {
            "Content-Type": "application/json",
            "Connection": "close"
        }
        
        req = urllib.request.Request(
            url,
            data=json.dumps(payload).encode("utf-8"),
            headers=headers,
            method="POST"
        )
        
        max_attempts = 3
        for attempt in range(max_attempts):
            try:
                if attempt > 0:
                    print(f"[AI Process] Thử lại gửi yêu cầu tới Gemini API (Lần {attempt + 1})...", file=sys.stderr)
                else:
                    print("[AI Process] Đang tải payload lên Google Gemini API và chờ AI xử lý (quá trình này có thể mất 10-30s)...", file=sys.stderr)
                with urllib.request.urlopen(req, timeout=240) as response:
                    print("[AI Process] Đã nhận phản hồi thành công từ Gemini. Đang xử lý cấu trúc JSON...", file=sys.stderr)
                    res_data = response.read().decode("utf-8")
                    res_json = json.loads(res_data)
                    if "candidates" in res_json and len(res_json["candidates"]) > 0:
                        candidate = res_json["candidates"][0]
                        if "content" in candidate:
                            content_text = candidate["content"]["parts"][0]["text"]
                            parsed_json = json.loads(content_text.strip())
                        else:
                            finish_reason = candidate.get("finishReason", "UNKNOWN")
                            print(f"[Warning] Gemini không trả về nội dung. Lý do dừng: {finish_reason}", file=sys.stderr)
                            if "safetyRatings" in candidate:
                                print(f"Chi tiết đánh giá an toàn: {json.dumps(candidate['safetyRatings'])}", file=sys.stderr)
                            raise Exception(f"Gemini API blocked generation. Reason: {finish_reason}")
                    else:
                        print(f"Phản hồi lạ từ Gemini: {json.dumps(res_json)}", file=sys.stderr)
                        raise Exception("No candidates returned from Gemini API")
                    
                    doc = pymupdf.open(pdf_path)
                    total_pages = len(doc)
                    doc.close()
                    
                    return {
                        "format": "V-ACT Exam",
                        "file_name": os.path.basename(pdf_path),
                        "total_pages": total_pages,
                        "total_passages": len(parsed_json.get("passages", [])),
                        "total_single_questions": len(parsed_json.get("single_questions", [])),
                        "passages": parsed_json.get("passages", []),
                        "single_questions": parsed_json.get("single_questions", [])
                    }
            except urllib.error.HTTPError as http_err:
                error_content = ""
                try:
                    error_content = http_err.read().decode("utf-8")
                except:
                    pass
                print(f"[Warning] Thử gọi Gemini lần {attempt + 1} gặp lỗi HTTP {http_err.code}: {http_err.reason}\nChi tiết phản hồi: {error_content}", file=sys.stderr)
                
                if http_err.code in (429, 503):
                    sleep_time = 10
                    print(f"[AI Process] Gặp lỗi {http_err.code}. Đang dừng {sleep_time} giây trước khi thử lại...", file=sys.stderr)
                    import time
                    time.sleep(sleep_time)
                    continue
                    
                if attempt == max_attempts - 1:
                    raise http_err
                import time
                time.sleep(2)
            except Exception as e:
                print(f"[Warning] Thử gọi Gemini lần {attempt + 1} gặp lỗi: {str(e)}", file=sys.stderr)
                if "content" in str(e) and 'res_json' in locals():
                    print(f"JSON phản hồi từ Gemini: {json.dumps(res_json, ensure_ascii=False)}", file=sys.stderr)
                if attempt == max_attempts - 1:
                    raise e
                import time
                time.sleep(2)
    except Exception as e:
        print(f"[Warning] Lỗi khi gọi Gemini API sau {max_attempts} lần thử: {str(e)}", file=sys.stderr)
        return None

def is_math_phys_chem_question(q):
    content = q.get("content", "").lower()
    options = q.get("options", {})
    opt_text = " ".join(options.values()).lower()
    combined = content + " " + opt_text
    
    math_phys_chem_keywords = [
        # Toán
        "hàm số", "phương trình", "bất phương trình", "tích phân", "đạo hàm", "giới hạn",
        "hình chóp", "lăng trụ", "tọa độ", "vectơ", "vector", "đồ thị", "xác suất", "nhị thức", "cấp số", 
        "logarit", "số phức", "hình học", "tam giác", "đường tròn", "mặt cầu", "góc giữa", "khoảng cách từ",
        "tư duy logic", "phân tích số liệu", "biểu đồ", "công thức", "chia hết", "tập nghiệm", "đường thẳng",
        "mặt phẳng", "nguyên hàm", "cực trị", "tiệm cận", "biểu thức", "phương trình", "hệ phương trình",
        # Vật lý
        "vật lí", "vật lý", "lực", "vận tốc", "gia tốc", "dao động", "sóng", "điện xoay chiều", 
        "quang phổ", "hạt nhân", "năng lượng", "chu kì", "chu kỳ", "tần số", "thấu kính", 
        "khúc xạ", "tụ điện", "điện trở", "từ trường", "bức xạ", "phóng xạ", "cơ năng", "động năng",
        "thế năng", "li độ", "biên độ", "bước sóng", "chiết suất", "hạt prôtôn", "hạt nơtrôn", "hạt êlectron",
        "phản xạ", "điện thế", "cường độ dòng điện", "suất điện động", "từ thông",
        # Hóa học
        "hóa học", "phản ứng", "kim loại", "axit", "bazơ", "muối", "este", "glucozơ", "ete", "amin", 
        "polime", "hidrocacbon", "nồng độ", "mol", "khối lượng", "dung dịch", "kết tủa", "hữu cơ", 
        "vô cơ", "nguyên tố", "đồng phân", "hiệu suất", "chuỗi phản ứng", "hóa trị", "nguyên tử",
        "phân tử", "ion", "dung môi", "hiđrôcacbon", "alkane", "alkene", "alkyne", "alcohol",
        "aldehyde", "ketone", "carboxylic acid", "amino acid", "peptide", "protein", "saccarozơ",
        "tinh bột", "xenlulozơ", "anilin", "phenol"
    ]
    
    for kw in math_phys_chem_keywords:
        if kw in combined:
            return True
            
    math_symbols = [
        r'\b[yXz]\s*=\s*',
        r'\bx\s*=\s*',
        r'\bf\(x\)',
        r'\\le',
        r'\\ge',
        r'\\pi',
        r'\\rightarrow',
        r'CO2',
        r'H2O',
        r'NaOH',
        r'HCl',
        r'Fe\s*\+',
        r'Cu\s*\+',
        r'\b\d+%\b',
        r'\b\d+°'
    ]
    for pat in math_symbols:
        if re.search(pat, combined):
            return True
            
    q_num = q.get("question_number")
    if q_num is not None and 41 <= q_num <= 90:
        return True
        
    return False

def is_math_phys_chem_page_text(text):
    text_lower = text.lower()
    keywords = [
        "hàm số", "tích phân", "đạo hàm", "hình chóp", "lăng trụ", "tọa độ", "vectơ", "đồ thị", 
        "vật lí", "vật lý", "xoay chiều", "hóa học", "este", "glucozơ", "polime", "hidrocacbon",
        "nồng độ", "kết tủa", "phản ứng"
    ]
    for kw in keywords:
        if kw in text_lower:
            return True
    return False

def merge_exam_jsons(chunks_json, file_name, total_pages):
    """
    Hợp nhất các phần JSON kết quả từ OpenAI của từng trang/nhóm trang.
    """
    merged = {
        "format": "V-ACT Exam",
        "file_name": file_name,
        "total_pages": total_pages,
        "total_passages": 0,
        "total_single_questions": 0,
        "passages": [],
        "single_questions": []
    }
    
    seen_single_questions = set()
    seen_passages_start = set()
    
    for chunk in chunks_json:
        if not chunk:
            continue
            
        # Hợp nhất single_questions
        for q in chunk.get("single_questions", []):
            q_num = q.get("question_number")
            if q_num not in seen_single_questions:
                seen_single_questions.add(q_num)
                merged["single_questions"].append(q)
                
        # Hợp nhất passages
        for p in chunk.get("passages", []):
            start_q = p.get("start_question")
            if start_q not in seen_passages_start:
                seen_passages_start.add(start_q)
                
                # Tránh trùng lặp câu hỏi con trong passage
                cleaned_questions = []
                for sub_q in p.get("questions", []):
                    sub_q_num = sub_q.get("question_number")
                    if sub_q_num not in seen_single_questions:
                        seen_single_questions.add(sub_q_num)
                        cleaned_questions.append(sub_q)
                
                p["questions"] = cleaned_questions
                merged["passages"].append(p)
                
    # Sắp xếp lại theo thứ tự câu hỏi
    merged["single_questions"].sort(key=lambda x: x.get("question_number", 0))
    merged["passages"].sort(key=lambda x: x.get("start_question", 0))
    
    merged["total_passages"] = len(merged["passages"])
    merged["total_single_questions"] = len(merged["single_questions"])
    
    return merged

def run_local_pipeline(pdf_path):
    try:
        # 1. Đọc file PDF bằng PyMuPDF
        doc = pymupdf.open(pdf_path)
        total_pages = len(doc)
        
        full_markdown = ""
        prompt = (
            "Bạn là chuyên gia OCR và chuyển đổi tài liệu đề thi sang định dạng Markdown.\n"
            "Nhiệm vụ của bạn là chuyển đổi hình ảnh trang đề thi này thành văn bản Markdown chuẩn xác nhất.\n"
            "Yêu cầu:\n"
            "1. Giữ nguyên cấu trúc các câu hỏi (ví dụ: Câu 1, Câu 2) và các lựa chọn trắc nghiệm A, B, C, D.\n"
            "2. Các công thức toán học, vật lý, hóa học BẮT BUỘC phải được viết dưới dạng công thức LaTeX chuẩn, sử dụng kí hiệu $ để bao bọc biểu thức toán (ví dụ: $y = x^3 - 3x^2 + 1$ hoặc $\\int_0^1 x dx$).\n"
            "3. Nếu trang có bảng biểu, hãy dựng lại thành bảng Markdown.\n"
            "4. Nếu trang có đồ thị, biểu đồ, hoặc hình vẽ khoa học, hãy chèn một đoạn mô tả chi tiết hình vẽ đó trực tiếp dưới dạng văn bản (ví dụ: *([Hình vẽ/Đồ thị minh họa]: đồ thị parabol có cực tiểu tại (1; -2)...)*).\n"
            "5. Chỉ trả về văn bản Markdown sạch của trang đề thi, tuyệt đối không thêm lời chào, giải thích hoặc kết luận ngoài lề."
        )
        
        # 2. Xử lý tuần tự từng trang để tiết kiệm VRAM
        for page_idx in range(total_pages):
            page_num = page_idx + 1
            print(f"[AI Process] Đang nhận diện trang {page_num}/{total_pages} bằng Qwen 2.5-VL...", file=sys.stderr)
            
            # Chụp ảnh trang PDF với độ phân giải DPI = 90 để tiết kiệm VRAM và tăng tốc độ xử lý
            page = doc[page_idx]
            pix = page.get_pixmap(dpi=90)
            img_bytes = pix.tobytes("png")
            
            # Tạo file ảnh tạm thời để truyền vào hàm của local_post_processor
            temp_img_path = os.path.join(os.path.dirname(pdf_path), f"temp_page_{page_num}.png")
            with open(temp_img_path, "wb") as temp_file:
                temp_file.write(img_bytes)
                
            try:
                # Gọi API Ollama thông qua local_post_processor
                page_md = local_post_processor.query_qwen_vl_via_ollama(temp_img_path, prompt)
                
                # Xóa file ảnh tạm
                if os.path.exists(temp_img_path):
                    os.remove(temp_img_path)
                    
                if not page_md:
                    raise Exception(f"Không nhận được phản hồi từ Qwen ở trang {page_num}")
                    
                full_markdown += f"\n\n<!-- PAGE_BREAK_BEFORE_{page_num} -->\n\n" + page_md
                
            except Exception as page_err:
                print(f"[Warning] Lỗi khi xử lý trang {page_num}: {str(page_err)}", file=sys.stderr)
                if os.path.exists(temp_img_path):
                    os.remove(temp_img_path)
                # Fallback lấy text chay của trang nếu Qwen bị lỗi để không làm hỏng cả đề
                page_text = page.get_text()
                full_markdown += f"\n\n<!-- PAGE_BREAK_BEFORE_{page_num} -->\n\n" + page_text

        doc.close()
        
        # 3. Phân tích cú pháp Markdown tổng hợp thành JSON cấu trúc V-ACT
        passages, single_questions = local_post_processor.parse_markdown_to_json(full_markdown)
        
        return {
            "format": "V-ACT Exam",
            "file_name": os.path.basename(pdf_path),
            "total_pages": total_pages,
            "total_passages": len(passages),
            "total_single_questions": len(single_questions),
            "passages": passages,
            "single_questions": single_questions
        }
    except Exception as e:
        print(f"[Warning] Lỗi trong pipeline local Qwen: {str(e)}", file=sys.stderr)
        return None

def run_openai_pipeline(pdf_path, api_key):
    doc = pymupdf.open(pdf_path)
    total_pages = len(doc)
    
    # Chạy local parser trước để lấy cấu trúc câu hỏi thô và nhãn phân chia
    exam_data_local = exam_parser.parse_exam_pdf(pdf_path)
    
    # Xác định các trang chứa câu hỏi Toán, Lý, Hóa cần đưa lên AI
    ai_pages = set()
    for q in exam_data_local.get("single_questions", []):
        if is_math_phys_chem_question(q):
            ai_pages.add(q.get("page_number"))
            
    for p in exam_data_local.get("passages", []):
        for q in p.get("questions", []):
            if is_math_phys_chem_question(q):
                ai_pages.add(q.get("page_number"))
                
    # Kiểm tra text thô để chắc chắn không bỏ sót trang
    for page_idx in range(total_pages):
        page_num = page_idx + 1
        if page_num not in ai_pages:
            page_text = doc[page_idx].get_text()
            if is_math_phys_chem_page_text(page_text):
                ai_pages.add(page_num)
    
    # Bước 1: Trích xuất hình ảnh base64 chỉ dành cho các trang thuộc Toán, Lý, Hóa
    page_images = []
    for page_idx in range(total_pages):
        page_num = page_idx + 1
        if page_num in ai_pages:
            page = doc[page_idx]
            pix = page.get_pixmap(dpi=96)
            img_bytes = pix.tobytes("png")
            base64_image = base64.b64encode(img_bytes).decode('utf-8')
            page_images.append((page_num, base64_image))
    
    # Bước 2: Chạy song song gọi OpenAI GPT-4o-mini cho các trang này
    chunks_json = []
    
    def parse_page(page_info):
        page_num, base64_img = page_info
        try:
            return ai_post_processor.call_openai_to_parse_page_image(base64_img, page_num, api_key)
        except Exception as e:
            print(f"Lỗi parse trang {page_num}: {str(e)}", file=sys.stderr)
            return None
    
    # Sử dụng ThreadPoolExecutor với tối đa 3 luồng song song để tránh bị rate limit (TPM 30k)
    with ThreadPoolExecutor(max_workers=3) as executor:
        ai_results = list(executor.map(parse_page, page_images))
        
    # Map kết quả AI theo số trang
    ai_results_by_page = {}
    for page_info, res in zip(page_images, ai_results):
        page_num = page_info[0]
        if res:
            ai_results_by_page[page_num] = res
    
    # Bước 3: Hợp nhất kết quả giữa Local Parser (cho các trang text thường) và AI Parser (cho các trang Toán, Lý, Hóa)
    merged = {
        "format": "V-ACT Exam",
        "file_name": os.path.basename(pdf_path),
        "total_pages": total_pages,
        "total_passages": 0,
        "total_single_questions": 0,
        "passages": [],
        "single_questions": []
    }
    
    seen_single_questions = set()
    seen_passages_start = set()
    
    def add_single_question(q):
        q_num = q.get("question_number")
        if q_num not in seen_single_questions:
            seen_single_questions.add(q_num)
            merged["single_questions"].append(q)
            
    def add_passage(p):
        start_q = p.get("start_question")
        if start_q not in seen_passages_start:
            seen_passages_start.add(start_q)
            cleaned_questions = []
            for sub_q in p.get("questions", []):
                sub_q_num = sub_q.get("question_number")
                if sub_q_num not in seen_single_questions:
                    seen_single_questions.add(sub_q_num)
                    cleaned_questions.append(sub_q)
            p["questions"] = cleaned_questions
            merged["passages"].append(p)
    
    for page_num in range(1, total_pages + 1):
        if page_num in ai_results_by_page:
            ai_data = ai_results_by_page[page_num]
            for q in ai_data.get("single_questions", []):
                add_single_question(q)
            for p in ai_data.get("passages", []):
                add_passage(p)
        else:
            # Lấy câu hỏi từ local parser cho trang này
            for q in exam_data_local.get("single_questions", []):
                if q.get("page_number") == page_num:
                    add_single_question(q)
            for p in exam_data_local.get("passages", []):
                p_page = page_num
                if p.get("questions"):
                    p_page = p["questions"][0].get("page_number", page_num)
                if p_page == page_num:
                    add_passage(p)
    
    merged["single_questions"].sort(key=lambda x: x.get("question_number", 0))
    merged["passages"].sort(key=lambda x: x.get("start_question", 0))
    
    merged["total_passages"] = len(merged["passages"])
    merged["total_single_questions"] = len(merged["single_questions"])
    
    return merged

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Thiếu đối số đường dẫn file PDF."}), file=sys.stderr)
        sys.exit(1)
        
    pdf_path = sys.argv[1]
    
    if not os.path.exists(pdf_path):
        print(json.dumps({"error": f"Không tìm thấy file: {pdf_path}"}), file=sys.stderr)
        sys.exit(1)
        
    # --- CẤP ĐỘ 1: Gemini 2.5 Flash API (Không mất phí, chạy cực nhanh, xử lý tốt scan/viết tay) ---
    gemini_key = os.environ.get("GEMINI_API_KEY", "AIzaSyA5I8azj-Qva6AURzh_95PJ4bfnmSjlA58")
    if gemini_key and not gemini_key.startswith("YOUR_GEMINI_"):
        gemini_data = query_gemini_api(pdf_path, gemini_key)
        if gemini_data:
            print(json.dumps(gemini_data, ensure_ascii=False, indent=2))
            sys.exit(0)
        print("[AI Process] Không thể kết xuất qua Gemini API. Thử chuyển sang OpenAI GPT-4o-mini...", file=sys.stderr)
        
    # --- CẤP ĐỘ 2: OpenAI GPT-4o-mini API (Dự phòng có phí nhưng chạy nhanh, chia luồng song song) ---
    openai_key = os.environ.get("OPENAI_API_KEY")
    is_openai_valid = openai_key and not openai_key.startswith("YOUR_OPENAI_API_KEY_") and len(openai_key) > 20
    if is_openai_valid:
        print("[AI Process] Đang chạy trích xuất dự phòng qua OpenAI GPT-4o-mini...", file=sys.stderr)
        try:
            openai_data = run_openai_pipeline(pdf_path, openai_key)
            if openai_data:
                print(json.dumps(openai_data, ensure_ascii=False, indent=2))
                sys.exit(0)
        except Exception as e:
            print(f"[Warning] Lỗi khi chạy OpenAI GPT-4o-mini Pipeline: {str(e)}", file=sys.stderr)
        print("[AI Process] OpenAI Pipeline thất bại hoặc chưa cấu hình. Chuyển sang Regex local parser...", file=sys.stderr)

    # --- CẤP ĐỘ 3: Regex Local Parser (Dự phòng cuối cùng, không dùng AI, đảm bảo dịch vụ không sập) ---
    print("[AI Process] Sử dụng Regex local parser...", file=sys.stderr)
    try:
        exam_data = exam_parser.parse_exam_pdf(pdf_path)
        exam_data["warning"] = "Tất cả các mô hình AI đều thất bại. Đã tự động chuyển sang chế độ phân tích cục bộ bằng regex."
        print(json.dumps(exam_data, ensure_ascii=False, indent=2))
        sys.exit(0)
    except Exception as local_err:
        print(json.dumps({"error": f"Tất cả các mô hình AI và Regex local đều thất bại: {str(local_err)}"}), file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
