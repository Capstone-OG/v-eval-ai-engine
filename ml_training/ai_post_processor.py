import os
import json
import sys
import urllib.request
import urllib.error

# Reconfigure stdout/stderr to use UTF-8 for console output on Windows
if sys.platform.startswith('win'):
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')

def call_openai_to_parse_page_image(base64_image, page_number, api_key):
    """
    Gọi OpenAI GPT-4o Multimodal để phân tích hình ảnh trang đề thi PDF,
    suy luận công thức toán LaTeX gốc, phát hiện phần gạch chân và trích xuất JSON.
    """
    url = "https://api.openai.com/v1/chat/completions"
    
    prompt = f"""Bạn là chuyên gia phân tích đề thi ĐGNL (Đánh giá năng lực). Nhiệm vụ của bạn là xem hình ảnh của trang đề thi này (Trang {page_number}) và cấu trúc lại toàn bộ các câu hỏi trên trang này thành dạng JSON chuẩn.

YÊU CẦU ĐẶC BIỆT:
1. CÔNG THỨC TOÁN HỌC:
   Hãy đọc kỹ các công thức toán từ hình ảnh và viết chúng dưới dạng LaTeX chuẩn, bao bọc bằng ký hiệu $ (inline math) hoặc $$ (block math).
   Ví dụ: $y = 2x^3 - 3(m+1)x^2 + 6mx + 1$, $z = 3-i$, $z = i(3-i)$, $y = x^3, y = x$.
   Hãy đảm bảo các ký hiệu toán học như \le (≤), \ge (≥), \pi (π), \pm (±), \in ( thuộc ) được viết chuẩn xác theo cú pháp LaTeX.

2. CÂU HỎI TIẾNG ANH TÌM LỖI SAI (GẠCH CHÂN):
   Ở các câu hỏi tìm lỗi sai Tiếng Anh, các phần gạch chân tương ứng với lựa chọn A, B, C, D.
   Bạn phải xác định từ/cụm từ nào trong câu được gạch chân tương ứng với A, B, C, D, và bao bọc chúng bằng tag `<u>...</u>` kèm ký hiệu tương ứng.
   Ví dụ: "I bought <u>a</u> (A) flower pot to decorate <u>a</u> (B) living room, <u>but</u> (C) my mom said <u>it was</u> (D) not very beautiful."
   Tất cả các lựa chọn A, B, C, D cho các câu hỏi này sẽ có giá trị lần lượt là "A", "B", "C", "D".

3. ĐỊNH DẠNG TỪ KHÓA BOLD/ITALIC:
   Giữ nguyên định dạng in đậm hoặc in hoa của các từ khóa quan trọng bằng Markdown (ví dụ: `**Originally**`).

4. CÁC HÌNH VẼ / BIỂU ĐỒ / ĐỒ THỊ:
   Nếu trên trang có hình vẽ, biểu đồ hoặc đồ thị đi kèm câu hỏi, bạn hãy tự động chèn một placeholder mô tả như `[Hình vẽ/Biểu đồ trong đề thi]` hoặc `[Đồ thị gia tốc - li độ]` vào trong nội dung câu hỏi tại vị trí thích hợp để học sinh biết có hình vẽ đi kèm.

5. CHÙM CÂU HỎI (PASSAGE) & CÂU HỎI ĐỘC LẬP:
   - Nếu trang chứa một đoạn văn đọc hiểu (ngữ cảnh) dùng chung cho nhiều câu hỏi, hãy đưa đoạn văn đó vào thuộc tính `content` của đối tượng trong danh sách `passages`.
   - Nếu câu hỏi là độc lập, hãy đưa vào danh sách `single_questions`.
   - Đảm bảo giữ đúng số thứ tự của các câu hỏi (ví dụ: Câu 41, Câu 42).
   - Đặt thuộc tính `page_number` của mỗi câu hỏi là {page_number}.

Yêu cầu xuất ra JSON chính xác theo cấu trúc sau (chỉ trả về JSON, không thêm bớt từ ngữ khác ngoài JSON):
{{
  "passages": [
    {{
      "start_question": 16,
      "end_question": 20,
      "content": "Nội dung đoạn văn đọc hiểu...",
      "questions": [
        {{
          "question_number": 16,
          "page_number": {page_number},
          "content": "Nội dung câu hỏi...",
          "options": {{
            "A": "...",
            "B": "...",
            "C": "...",
            "D": "..."
          }}
        }}
      ]
    }}
  ],
  "single_questions": [
    {{
      "question_number": 41,
      "page_number": {page_number},
      "content": "Nội dung câu hỏi...",
      "options": {{
        "A": "...",
        "B": "...",
        "C": "...",
        "D": "..."
      }}
    }}
  ]
}}
"""

    payload = {
        "model": "gpt-4o-mini",
        "messages": [
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": prompt},
                    {
                        "type": "image_url",
                        "image_url": {
                            "url": f"data:image/png;base64,{base64_image}"
                        }
                    }
                ]
            }
        ],
        "response_format": { "type": "json_object" },
        "temperature": 0.1
    }
    
    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {api_key}"
    }
    
    import time

    max_retries = 6
    backoff = 3.0
    
    for attempt in range(max_retries):
        req = urllib.request.Request(url, data=json.dumps(payload).encode('utf-8'), headers=headers)
        try:
            with urllib.request.urlopen(req) as response:
                res_data = response.read().decode('utf-8')
                res_json = json.loads(res_data)
                content = res_json['choices'][0]['message']['content']
                return json.loads(content)
        except urllib.error.HTTPError as e:
            error_msg = e.read().decode('utf-8')
            if e.code == 429 and attempt < max_retries - 1:
                # Rate limit hit, back off and retry
                print(f"[Warning] Bị giới hạn băng thông (429) ở trang {page_number}. Đang thử lại sau {backoff} giây...", file=sys.stderr)
                time.sleep(backoff)
                backoff *= 2.0
                continue
            raise Exception(f"OpenAI API Error: {e.code} - {error_msg}")
        except Exception as e:
            if attempt < max_retries - 1:
                time.sleep(backoff)
                backoff *= 2.0
                continue
            raise Exception(f"Lỗi gọi OpenAI: {str(e)}")
