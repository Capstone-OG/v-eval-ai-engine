# UPDATE LOG — V-Eval AI Engine

## [24/11/2026] Chuẩn hóa ngôn ngữ nhận xét Chẩn đoán thân thiện với học sinh
- Cập nhật `rag-service/routers/diagnostic.py`: Loại bỏ ký hiệu kỹ thuật `θ = ...` khỏi nhận xét rule-based và bổ sung chỉ dẫn trong prompt Gemini không dùng thuật ngữ kỹ thuật (`IRT`, `theta`, `BKT`, `P(L0)`, `DAG`) khi trả lời học sinh.
- Kiểm tra cú pháp `python -m py_compile` đạt 100%.
