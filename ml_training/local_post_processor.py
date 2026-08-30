import os
import re
import json
import base64
import urllib.request
import urllib.error
import socket
import sys

def query_qwen_vl_via_ollama(image_path, question_text=None, ollama_url="http://localhost:11434/api/chat"):
    """
    Gửi ảnh đồ thị/hình vẽ tới model Qwen 2.5-VL 3B qua Ollama cục bộ để phân tích và lấy số liệu.
    """
    if not os.path.exists(image_path):
        print(f"[Warning] Không tìm thấy ảnh để gửi tới Qwen: {image_path}", file=sys.stderr)
        return None

    try:
        # 1. Đọc và chuyển ảnh sang Base64
        with open(image_path, "rb") as img_file:
            base64_image = base64.b64encode(img_file.read()).decode("utf-8")

        # 2. Xây dựng prompt kèm ngữ cảnh câu hỏi nếu có
        prompt = "Bạn là trợ lý AI chuyên phân tích đồ thị, biểu đồ và sơ đồ khoa học trong đề thi."
        if question_text:
            prompt += f"\nHình vẽ này thuộc câu hỏi sau:\n\"{question_text}\"\n"
        prompt += (
            "\nHãy phân tích hình vẽ này. Thực hiện các yêu cầu sau:\n"
            "1. Trích xuất các mốc số liệu hoặc tọa độ quan trọng trên các trục (trục hoành, trục tung, các điểm cực trị, giao điểm nếu có).\n"
            "2. Chuyển đổi dữ liệu biểu đồ/bảng số liệu thành bảng Markdown hoặc mô tả chi tiết nội dung để học sinh hiểu được hình vẽ thông qua mô tả này.\n"
            "Chỉ trả về văn bản phân tích mô tả hình vẽ, không thêm bớt lời chào hỏi hay kết luận thừa thãi."
        )

        payload = {
            "model": "qwen2.5vl:3b",
            "messages": [
                {
                    "role": "user",
                    "content": prompt,
                    "images": [base64_image]
                }
            ],
            "stream": False,
            "options": {
                "temperature": 0.1,
                "num_ctx": 2048,
                "num_gpu": 10
            }
        }

        # 3. Gửi HTTP Request tới Ollama
        req = urllib.request.Request(
            ollama_url,
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )

        # Sử dụng cơ chế thử lại (retry) với thời gian chờ lâu hơn để xử lý trường hợp
        # Ollama phải nạp mô hình vào card đồ họa (VRAM) ở yêu cầu đầu tiên.
        max_attempts = 3
        timeout_seconds = 180
        
        for attempt in range(max_attempts):
            try:
                with urllib.request.urlopen(req, timeout=timeout_seconds) as response:
                    res_data = response.read().decode("utf-8")
                    res_json = json.loads(res_data)
                    description = res_json["message"]["content"].strip()
                    return description
            except (urllib.error.URLError, socket.timeout) as err:
                if attempt == max_attempts - 1:
                    raise err
                print(f"[Warning] Ollama phản hồi lâu hoặc đang nạp model. Thử lại lần {attempt + 2}...", file=sys.stderr)
                import time
                time.sleep(2)

    except Exception as e:
        print(f"[Warning] Không thể kết nối hoặc lỗi xử lý với Ollama: {str(e)}", file=sys.stderr)
        return None

def clean_html_sub_sup(text):
    if not text:
        return text
    # Replace <sup>text</sup> with ^{text}
    text = re.sub(r'<sup>(.*?)</sup>', r'^{\1}', text)
    # Replace <sub>text</sub> with _{\1}
    text = re.sub(r'<sub>(.*?)</sub>', r'_{\1}', text)
    return text

def parse_markdown_to_json(content):
    """
    Phân tích file Markdown tổng hợp từ Qwen thành cấu trúc JSON đề thi chuẩn V-ACT Exam.
    """
    # 1. Tách chùm câu hỏi (Passages)
    # Định dạng ngữ cảnh dùng chung cho các câu từ X đến Y
    passage_pattern = re.compile(
        r"(Dựa vào thông tin dưới đây để trả lời các câu từ\s+(\d+)\s+đến\s+(\d+)(?:\*\*|)|Questions\s+(\d+)-(\d+)(?:\*\*|):\s*Read the passage carefully\.?)\s*(?:\n|\r|\s)*\n(.*?)(?=(?:\*\*|)\b(?:Câu|Question)(?:\*\*|)\s+\d+[:.]?|$)",
        re.DOTALL | re.IGNORECASE
    )

    passages = []
    passage_matches = list(passage_pattern.finditer(content))
    for match in passage_matches:
        start_q = int(match.group(2) or match.group(4))
        end_q = int(match.group(3) or match.group(5))
        p_content = match.group(6).strip()

        passages.append({
            "start_question": start_q,
            "end_question": end_q,
            "content": clean_html_sub_sup(p_content),
            "questions": []
        })

    # 2. Tách tất cả các câu hỏi
    # Tìm các vị trí xuất hiện của "Câu X:" hoặc "Question X:" hoặc "**Câu X:**"
    q_header_pattern = re.compile(
        r"(?:\*\*|)\b(?:Câu|Question)\s+(\d+)\s*(?::\*\*|:\s*|.\*\*|.\s*)",
        re.IGNORECASE
    )
    
    matches = list(q_header_pattern.finditer(content))
    all_questions = []
    
    for i, match in enumerate(matches):
        q_num = int(match.group(1))
        start_pos = match.end()
        end_pos = matches[i+1].start() if i + 1 < len(matches) else len(content)
        q_body_raw = content[start_pos:end_pos].strip()

        # Loại bỏ các tiêu đề phân phần (sections), đoạn đọc hiểu (passages),
        # hoặc dấu kết thúc kì thi ("HẾT") bị dính vào cuối câu hỏi hiện tại.
        cut_pattern = re.compile(
            r"(?:\n|\s)*(?:#+|=+|-+)\s*\*\*?(?:Dựa vào thông tin|Questions\s+\d+-\d+|PHẦN\s+\d+|Thứ tự câu|1\.2\.\s*TIẾNG|TIẾNG ANH|TOÁN HỌC|TƯ DUY LOGIC|PHÂN TÍCH SỐ LIỆU|GIẢI QUYẾT VẤN ĐỀ|HÓA HỌC|VẬT LÝ|SINH HỌC|ĐỊA LÝ|LỊCH SỬ|---------- HẾT ----------)\*\*?",
            re.IGNORECASE
        )
        cut_match = cut_pattern.search(q_body_raw)
        if cut_match:
            q_body_raw = q_body_raw[:cut_match.start()].strip()

        # Tách nội dung câu hỏi và các lựa chọn A, B, C, D
        # Thường các lựa chọn cũng được bọc trong dấu sao: **A.** hoặc A.
        opt_pattern = re.compile(
            r"(?:\*\*|)\b([A-D])\s*[:.]\s*(?:\*\*|)\s*(.*?)(?=\s*(?:\*\*|)\b[A-D]\s*[:.]|$)",
            re.DOTALL
        )
        options = {}
        content_only = q_body_raw

        opt_matches = list(opt_pattern.finditer(q_body_raw))
        if opt_matches:
            first_opt_idx = opt_matches[0].start()
            content_only = q_body_raw[:first_opt_idx].strip()
            for opt_match in opt_matches:
                opt_key = opt_match.group(1).upper()
                opt_val = opt_match.group(2).strip()
                # Dọn dẹp khoảng trắng thừa và dấu sao thừa ở cuối
                opt_val = re.sub(r"\s+", " ", opt_val)
                opt_val = opt_val.rstrip("*").strip()
                options[opt_key] = clean_html_sub_sup(opt_val)
        else:
            # Check lỗi sai Tiếng Anh dạng A B C D không có dấu chấm
            error_corr_match = re.search(r"(\n|\s)+A\s+B\s+C\s+D\s*$", q_body_raw, re.IGNORECASE)
            if error_corr_match:
                content_only = q_body_raw[:error_corr_match.start()].strip()
                options = {"A": "A", "B": "B", "C": "C", "D": "D"}

        # Tạo đối tượng câu hỏi
        question_obj = {
            "question_number": q_num,
            "page_number": 1,
            "content": clean_html_sub_sup(content_only),
            "options": options if options else {
                "A": "Chưa trích xuất được lựa chọn A",
                "B": "Chưa trích xuất được lựa chọn B",
                "C": "Chưa trích xuất được lựa chọn C",
                "D": "Chưa trích xuất được lựa chọn D"
            }
        }

        # Kiểm tra xem câu hỏi này thuộc chùm câu hỏi nào không
        belongs_to_passage = False
        for p in passages:
            if p["start_question"] <= q_num <= p["end_question"]:
                p["questions"].append(question_obj)
                belongs_to_passage = True
                break

        if not belongs_to_passage:
            all_questions.append(question_obj)

    # Sắp xếp lại thứ tự câu hỏi và chùm câu hỏi
    all_questions.sort(key=lambda x: x["question_number"])
    passages.sort(key=lambda x: x["start_question"])

    return passages, all_questions
