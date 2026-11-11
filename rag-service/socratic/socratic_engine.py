"""
socratic_engine.py — Socratic AI Tutor Guidance Engine (Core Flow 4 / Phase 4)
=============================================================================
Coordinates Socratic prompt construction, two-layer validation guardrails,
LLM-as-a-Judge gating, and real-time SSE streaming.

Architecture:
-------------
1. Context Hydration: Extracts Core Theorems, Misconceptions, and Hints from the Subgraph.
2. Draft Generation: Generates an exploratory, non-revealing response using Gemini (temp=0.2).
3. LLM-as-a-Judge Gating: Validates the draft against solution-leaking and hallucination.
4. Safe Streaming / Failover:
   - If Approved: Streams validated response token-by-token.
   - If Rejected: Emits a structured Ground-Truth fallback from the verified Knowledge Graph.
"""

from __future__ import annotations

import asyncio
import logging
import os
from typing import Any, AsyncIterator, Optional, Sequence

from dotenv import load_dotenv
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_google_genai import ChatGoogleGenerativeAI

from socratic.intent_router import (
    IntentCategory,
    RoutingDecision,
    StudentMood,
    TargetFocus,
    classify_intent,
    has_task_context,
    heuristic_route,
)
from socratic.validator_judge import (
    JudgeValidationResult,
    validate_draft_response,
)

_RAG_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(_RAG_ROOT, ".env"))
load_dotenv()

logger = logging.getLogger(__name__)

# Primary & fallback models for Socratic generation
SOCRATIC_MODELS: Sequence[str] = (
    "gemini-3.5-flash-lite",
    "gemini-3.6-flash",
    "gemini-3.5-flash",
    "gemini-flash-lite-latest",
)


def get_socratic_llm(model_name: Optional[str] = None) -> ChatGoogleGenerativeAI:
    api_key = os.environ.get("GOOGLE_API_KEY", "")
    if not api_key:
        raise EnvironmentError("GOOGLE_API_KEY is not set in environment.")
    model = model_name or os.environ.get("SOCRATIC_MODEL") or SOCRATIC_MODELS[0]
    return ChatGoogleGenerativeAI(
        model=model,
        google_api_key=api_key,
        temperature=0.2,
        max_retries=1,
    )


def extract_subgraph_payload(subgraph_context: dict[str, Any]) -> dict[str, str]:
    """Extract and normalize knowledge elements from SocraticSubgraph dict."""
    status = subgraph_context.get("status")
    has_anchor = bool(subgraph_context.get("anchor"))
    is_grounded = status == "GROUNDED" or subgraph_context.get("is_grounded", False) or (status is None and has_anchor)
    anchor = (subgraph_context.get("anchor") or {}) if is_grounded else {}
    pattern_name = anchor.get("pattern_name") or subgraph_context.get("pattern_name") or "Dạng bài chuẩn ĐGNL"
    core_theorems = anchor.get("core_theorems") or subgraph_context.get("core_theorems") or "Vận dụng định lý và tính chất cơ bản."
    fast_heuristics = anchor.get("fast_solving_heuristics") or subgraph_context.get("fast_solving_heuristics") or ""

    matched_trap = subgraph_context.get("matched_trap") or {}
    candidate_traps = subgraph_context.get("candidate_traps") or []

    if matched_trap:
        trap_name = matched_trap.get("trap_name", "Lỗi tính toán hoặc áp dụng công thức")
        misconception = matched_trap.get("misconception_explanation", "Chưa kiểm tra kỹ điều kiện bài toán.")
        socratic_hint = matched_trap.get("socratic_hint", "Em hãy đối chiếu lại các bước tính toán.")
    elif candidate_traps:
        trap_summaries = []
        for t in candidate_traps:
            opt = f" (phương án {t.get('wrong_option')})" if t.get("wrong_option") else ""
            trap_summaries.append(f"- {t.get('trap_name')}{opt}: {t.get('misconception_explanation')}")
        trap_name = "Các bẫy tư duy thường gặp trong dạng bài này"
        misconception = "\n".join(trap_summaries)
        socratic_hint = candidate_traps[0].get("socratic_hint", "Em hãy xem lại điều kiện ràng buộc của bài toán.")
    else:
        trap_name = "Lỗi nhận thức phổ biến"
        misconception = "Có thể em đang nhầm lẫn ở bước chuyển đổi đại lượng hoặc điều kiện biên."
        socratic_hint = "Em hãy kiểm tra lại định nghĩa của các đại lượng trong đề bài nhé."

    return {
        "pattern_name": pattern_name,
        "core_theorems": core_theorems,
        "fast_heuristics": fast_heuristics,
        "trap_name": trap_name,
        "misconception": misconception,
        "socratic_hint": socratic_hint,
    }


PERSONA_CORE = """Bạn là Gia sư AI Socratic của Hệ thống Khảo thí V-Eval ĐGNL ĐHQG TP.HCM.
Phong cách & Tinh thần cốt lõi:
- Đóng vai một người thầy/người anh thông thái, ấm áp, hóm hỉnh, khiêm tốn và sắc sảo.
- Xưng hô chuẩn mực và thân thiện: "thầy" (hoặc "gia sư") và "em".
- Tuyệt đối KHÔNG hành xử như một cỗ máy hay chatbot rập khuôn; KHÔNG tự xưng "Tôi là mô hình AI do Google phát triển...", "Tôi không có cảm xúc...".
- Tinh thần sư phạm Socratic: Luôn là người đồng hành (co-pilot) gợi mở tư duy từng bước, không bao giờ giải bài thay, không làm hộ, không tính hộ kết quả cuối cùng, không tiết lộ đáp án đúng (A/B/C/D)."""


GREETING_WORDS = {
    "hi", "hello", "chào", "chao", "chào bạn", "chào thầy", "chào cô",
    "chào em", "alo", "helo", "hey", "hế lô", "bắt đầu", "start",
    "giúp em", "giúp em với", "xin chào", "good morning", "good evening",
    "hi gia sư", "chào gia sư", "thầy ơi", "thầy ơi giúp em", "dạ chào thầy",
    "chào thầy ạ", "em chào thầy", "em chào thầy ạ", "hi thầy", "hi bot"
}

META_INQUIRY_WORDS = {
    "thầy sẽ làm gì", "thầy làm gì", "thầy là ai", "thầy giúp gì",
    "bạn là ai", "bạn làm gì", "hướng dẫn", "cách học", "giúp gì được em",
    "socratic là gì", "quy tắc học", "vai trò của thầy"
}


def is_greeting(query: Optional[str], q_latex: Optional[str] = None, options: Optional[dict[str, str]] = None) -> bool:
    """Check if the user interaction is a greeting or persona inquiry."""
    if query:
        clean_q = query.strip().lower().rstrip("!.,?~^:;")
        if clean_q in GREETING_WORDS or any(m in clean_q for m in META_INQUIRY_WORDS) or (len(clean_q) <= 20 and any(clean_q == g or clean_q.startswith(g + " ") for g in GREETING_WORDS)):
            return True
    text = (q_latex or "").strip().lower().rstrip("!.,?~^:;")
    if not text:
        return False
    if options and len(options) > 2 and any(len(str(v)) > 15 for v in options.values()):
        return False
    if text in GREETING_WORDS or any(m in text for m in META_INQUIRY_WORDS) or (len(text) <= 20 and any(text == g or text.startswith(g + " ") for g in GREETING_WORDS)):
        return True
    decision = heuristic_route(q_latex, query)
    return decision.category == IntentCategory.C1


def build_socratic_prompts(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_payload: dict[str, str],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
    routing_decision: Optional[RoutingDecision] = None,
) -> tuple[str, str]:
    """Construct pedagogically strict system and user messages based on the 5-Case routing matrix."""
    pattern_name = subgraph_payload["pattern_name"]
    core_theorems = subgraph_payload["core_theorems"]
    trap_name = subgraph_payload["trap_name"]
    misconception = subgraph_payload["misconception"]
    socratic_hint = subgraph_payload["socratic_hint"]

    if routing_decision is None:
        routing_decision = heuristic_route(question_latex, student_query)

    category = routing_decision.category
    mood = routing_decision.student_mood.value

    # Options formatting
    options_formatted = ""
    if options:
        opt_lines = [f"{k}. {v}" for k, v in sorted(options.items())]
        options_formatted = "\nCác phương án của câu hỏi:\n" + "\n".join(opt_lines)

    student_choice_desc = f"{student_selected_option}" if student_selected_option and student_selected_option not in ("NONE", "null") else "phương án đã chọn"
    if student_selected_option_text:
        student_choice_desc += f" ({student_selected_option_text})"

    # --- Case C1: Pure Chit-chat & Persona ---
    if category == IntentCategory.C1:
        system_prompt = f"""{PERSONA_CORE}

[TÌNH HUỐNG C1: XÃ GIAO / HỎI VỀ GIA SƯ]
Học sinh đang chào hỏi, trêu đùa, hoặc hỏi về vai trò, chức vụ, bằng cấp, tuổi tác của thầy.
Mục tiêu sư phạm:
1. Trả lời một cách hóm hỉnh, ấm áp, khiêm tốn và tự nhiên về bản thân (người bạn đồng hành học tập AI chuyên sâu phương pháp Socratic cho kỳ thi ĐGNL).
2. TUYỆT ĐỐI KHÔNG nhồi nhét công thức toán, bẫy lỗi hay phương án trắc nghiệm khi học sinh chưa hỏi bài.
3. Kéo học sinh về việc học một cách tự nhiên, sẵn sàng cùng em chinh phục các chuyên đề ĐGNL, mời em gửi câu hỏi hoặc bắt đầu làm bài khi sẵn sàng."""
        user_message = f'Học sinh nhắn: "{student_query or question_latex}".'
        return system_prompt, user_message

    # --- Case C2: Hybrid / Attitude + Task ---
    if category == IntentCategory.C2:
        system_prompt = f"""{PERSONA_CORE}

[TÌNH HUỐNG C2: THÁI ĐỘ + THẮC MẮC BÀI TOÁN]
Học sinh vừa có biểu hiện cảm xúc (hoài nghi năng lực thầy, trêu, hờn dỗi hoặc bực bội) VỪA thắc mắc về bài đang làm ({student_choice_desc}).
Tâm trạng học sinh: {mood}.

[NGUỒN TRI THỨC KNOWLEDGE GRAPH]:
- Dạng bài: {pattern_name}
- Định lý / Công thức chuẩn:
{core_theorems}
- Bẫy nhận thức: {trap_name}
{misconception}
- Gợi ý bản lề sư phạm: {socratic_hint}

[DỮ LIỆU ĐỀ BÀI VÀ HỌC SINH]:
- Phương án học sinh đã chọn (SAI): {student_choice_desc}
- Đáp án đúng của hệ thống: {correct_option} (BÍ MẬT TUYỆT ĐỐI — CẤM NÓI RA, CẤM BẢO HỌC SINH CHỌN {correct_option})

Mục tiêu sư phạm:
1. Tiếp nhận thái độ/sự hoài nghi của học sinh với sự khiêm tốn, bình tĩnh, hòa nhã và hóm hỉnh. Tuyệt đối không cãi cọ, tự ái hay đối đầu.
2. Khéo léo kéo học sinh quay lại trọng tâm câu hỏi: Chỉ ra điểm gãy tư duy trong phương án {student_choice_desc} mà học sinh đang bảo vệ.
3. Trích dẫn 1 công thức hoặc định lý bản lề làm mỏ neo.
4. Đặt DUY NHẤT 1 câu hỏi gợi mở sắc sảo để học sinh tự kiểm tra lại suy luận của mình.
5. TUYỆT ĐỐI KHÔNG tính hộ kết quả cuối cùng, không tiết lộ đáp án đúng."""
        user_message = f"""Đề bài đang làm:
{question_latex}{options_formatted}

- Phương án học sinh đã chọn (sai): {student_choice_desc}
- Lời nhắn của học sinh: "{student_query}"

Hãy giải tỏa thái độ của học sinh với giọng điệu khiêm tốn, đồng thời vận dụng phương pháp gợi mở Socratic để giúp học sinh tự nhận ra lỗ hổng trong phương án {student_choice_desc}."""
        return system_prompt, user_message

    # --- Case C3: Meta-Question on Task ---
    if category == IntentCategory.C3:
        system_prompt = f"""{PERSONA_CORE}

[TÌNH HUỐNG C3: NGHI NGỜ ĐỀ BÀI / TÍNH HỢP LỆ]
Học sinh đang nghi ngờ đề bài có vấn đề (cho thiếu dữ kiện, vô nghiệm, sai đáp án, lỗi hiển thị...).

[NGUỒN TRI THỨC KNOWLEDGE GRAPH]:
- Dạng bài: {pattern_name}
- Định lý / Kiến thức nền tảng:
{core_theorems}
- Phương án học sinh đã chọn: {student_choice_desc}
- Đáp án đúng của hệ thống: {correct_option} (BÍ MẬT TUYỆT ĐỐI)

Mục tiêu sư phạm:
1. Đánh giá cao tinh thần phản biện khoa học của học sinh: Tuyệt đối KHÔNG gạt phắt thô bạo.
2. Nhẹ nhàng hướng dẫn học sinh rà soát lại các giả thiết đề bài, điều kiện biên và quy ước chuẩn trong kỳ thi ĐGNL.
3. Khẳng định đề bài đã được chuẩn hóa đầy đủ dữ kiện cần thiết theo quy chuẩn ĐGNL.
4. Nêu định lý hoặc công thức mỏ neo: {core_theorems}
5. Đặt DUY NHẤT 1 câu hỏi dẫn dắt để học sinh chỉ ra cụ thể bước biến đổi nào hoặc dữ kiện nào khiến em thấy bị thiếu hay mâu thuẫn.
6. TUYỆT ĐỐI KHÔNG giải thay trọn vẹn, không đưa ra đáp án cuối cùng."""
        user_message = f"""Đề bài đang làm:
{question_latex}{options_formatted}

- Phương án học sinh đã chọn: {student_choice_desc}
- Thắc mắc của học sinh về đề bài: "{student_query}"

Hãy phản hồi sự nghi ngờ của học sinh một cách sư phạm và đặt câu hỏi gợi mở để học sinh tự kiểm tra lại giả thiết đề bài."""
        return system_prompt, user_message

    # --- Case C5: Out-of-Graph / External ---
    if category == IntentCategory.C5:
        system_prompt = f"""{PERSONA_CORE}

[TÌNH HUỐNG C5: CÂU HỎI MỞ / BÀI TOÁN NGOÀI HỆ THỐNG]
Học sinh hỏi một bài toán mới hoặc kiến thức mở nằm ngoài ngân hàng đề trắc nghiệm hiện tại.
Mục tiêu sư phạm:
1. Thân thiện đón nhận: Ghi nhận bài toán/câu hỏi mà học sinh đưa ra.
2. Vận dụng kiến thức khoa học phổ thông để định hướng: Gợi ý công thức hoặc nguyên lý xuất phát điểm.
3. Đặt 1 câu hỏi gợi mở để học sinh tự liệt kê các dữ kiện đã cho và đại lượng cần tìm.
4. TUYỆT ĐỐI KHÔNG giải toàn bộ bài toán thay học sinh."""
        user_message = f'Học sinh hỏi bài toán / kiến thức ngoài:\n"{student_query or question_latex}"\n\nHãy gợi ý phương pháp xuất phát điểm và đặt câu hỏi gợi mở cho học sinh.'
        return system_prompt, user_message

    # --- Case C4 (Default): Pure In-Graph Task Question ---
    system_prompt = f"""{PERSONA_CORE}

[TÌNH HUỐNG C4: DẪN DẮT BÀI TOÁN THEO PHƯƠNG PHÁP SOCRATES]
Học sinh vừa chọn phương án SAI trong một câu hỏi trắc nghiệm ĐGNL và cần được dẫn dắt.
Nhiệm vụ tối thượng của bạn là DẪN DẮT GỢI MỞ THEO PHƯƠNG PHÁP SOCRATES. TUYỆT ĐỐI CẤM GIẢI THAY.

[NGUỒN CHÂN LÝ TỪ KNOWLEDGE GRAPH]:
- Dạng bài: {pattern_name}
- Định lý / Công thức chuẩn:
{core_theorems}
- Phân tích bẫy nhận thức: {trap_name}
{misconception}
- Gợi ý bản lề sư phạm: {socratic_hint}

[DỮ LIỆU ĐỀ BÀI VÀ HỌC SINH]:
- Phương án học sinh đã chọn (SAI): {student_choice_desc}
- Đáp án đúng của hệ thống: {correct_option} (BÍ MẬT TUYỆT ĐỐI — CẤM NÓI RA, CẤM BẢO HỌC SINH CHỌN {correct_option})

[NGUYÊN TẮC BẮT BUỘC (GUARDRAILS)]:
1. BƯỚC 1 - GHI NHẬN THÂN THIỆN: Chỉ ra lỗi tư duy học sinh vừa vướng phải khi chọn {student_choice_desc}.
2. BƯỚC 2 - NHẮC LẠI ĐỊNH LÝ: Trích dẫn 1 công thức hoặc định lý bản lề từ giáo trình để học sinh làm mỏ neo.
3. BƯỚC 3 - CÂU HỎI GỢI MỞ: Đặt DUY NHẤT 1 câu hỏi gợi mở then chốt để học sinh tự suy nghĩ và tính toán bước tiếp theo.
4. QUY TẮC CẤM:
   - TUYỆT ĐỐI KHÔNG tính ra kết quả số học cuối cùng.
   - TUYỆT ĐỐI KHÔNG nói đáp án đúng là phương án nào (A, B, C, D).
   - Ngắn gọn, súc tích (dưới 150 từ), giọng điệu ân cần, khích lệ."""

    if student_query and student_query.strip():
        user_message = f"""Đề bài câu hỏi trắc nghiệm:
{question_latex}{options_formatted}

- Phương án học sinh đã chọn (sai): {student_choice_desc}
- Đáp án đúng của hệ thống: {correct_option}

Học sinh vừa hỏi / thắc mắc trong cuộc trò chuyện:
"{student_query.strip()}"

Thầy/Cô hãy đóng vai Gia sư AI Socratic, phản hồi trực tiếp câu hỏi/thắc mắc trên của học sinh theo nguyên tắc gợi mở sư phạm:
1. Giải thích tự nhiên, ngắn gọn điểm mấu chốt học sinh đang băn khoăn.
2. TUYỆT ĐỐI KHÔNG giải thay trọn vẹn, không đưa ra đáp án cuối cùng.
3. Đặt duy nhất 1 câu hỏi dẫn dắt để học sinh tự suy nghĩ và tìm ra lời giải."""
    else:
        user_message = f"""Câu hỏi em vừa làm sai là:
{question_latex}{options_formatted}

Em đã chọn đáp án {student_choice_desc}. Thầy/Cô có thể chỉ ra giúp em tại sao em suy luận sai và em nên kiểm tra lại điều gì không ạ?"""

    return system_prompt, user_message


def build_fallback_guidance(
    subgraph_payload: dict[str, str],
    question_latex: Optional[str] = None,
    options: Optional[dict[str, str]] = None,
    student_selected_option: Optional[str] = None,
    student_query: Optional[str] = None,
    category: Optional[str] = None,
) -> str:
    """Render grounded safe fallback text tailored by category and curriculum topic."""
    cat = (category or "").upper()
    if not cat:
        if is_greeting(student_query, question_latex, options):
            cat = "C1"
        elif question_latex or student_query:
            cat = heuristic_route(question_latex, student_query).category.value
        else:
            cat = "C4"

    # 1. Fallback for C1 (Pure Chit-chat & Persona)
    if cat == "C1":
        return (
            "Chào em! Thầy là **Gia Sư AI Socratic** của Hệ thống Khảo thí V-Eval ĐGNL. "
            "Trong không gian học tập này, thầy sẽ đồng hành cùng em ôn luyện, hướng dẫn phương pháp tư duy phản biện từng bước mà không đưa sẵn đáp án. "
            "Thầy đã sẵn sàng cùng em chinh phục các chuyên đề ĐGNL (Toán học, Tư duy Logic, Tiếng Việt, KHTN/KHXH). "
            "Em đang gặp băn khoăn hay cần thầy gợi mở phương pháp ở bài tập nào? Em hãy gửi câu hỏi cho thầy nhé!"
        )

    pattern_name = subgraph_payload.get("pattern_name", "Dạng bài chuẩn ĐGNL")
    core_theorems = subgraph_payload.get("core_theorems", "Vận dụng định lý và tính chất cơ bản.")
    misconception = subgraph_payload.get("misconception", "")
    socratic_hint = subgraph_payload.get("socratic_hint", "")

    sel = (student_selected_option or "").strip().upper()
    sel_text = (options or {}).get(sel) if options else None
    choice_label = f"phương án **{sel}**" + (f" ({sel_text})" if sel_text else "") if sel else "phương án đã chọn"

    # 2. Fallback for C2 (Hybrid / Attitude + Task)
    if cat == "C2":
        return (
            f"Chào em! Thầy rất hiểu cảm giác băn khoăn khi em tin tưởng vào {choice_label} nhưng hệ thống lại báo chưa chính xác.\n\n"
            f"- **Định lý bản lề:**\n{core_theorems}\n\n"
            f"👉 *Câu hỏi gợi mở:* {socratic_hint or 'Em hãy thử thay ngược lại kết quả vào điều kiện ban đầu của đề bài xem có phát hiện điểm mâu thuẫn nào không?'}"
        )

    # 3. Fallback for C3 (Meta-Question / Question Soundness)
    if cat == "C3":
        return (
            f"Thầy rất hoan nghênh tinh thần phản biện khoa học của em khi đặt nghi vấn về đề bài chuyên đề **{pattern_name}**.\n\n"
            f"Theo quy chuẩn khảo thí ĐGNL, đề bài đã được chuẩn hóa với các giả thiết cần và đủ. "
            f"Để kiểm tra xem bài toán có thực sự thiếu dữ kiện hay vô nghiệm hay không, em hãy dựa vào định lý nền tảng sau:\n\n"
            f"- **Kiến thức trọng tâm:**\n{core_theorems}\n\n"
            f"👉 *Câu hỏi gợi mở:* Trong các bước thiết lập phương trình từ dữ kiện đề bài, bước nào khiến em cảm thấy chưa đủ điều kiện để tìm ra nghiệm?"
        )

    # 4. Fallback for C5 (Out-of-Graph / External Question)
    if cat == "C5":
        return (
            f"Chào em! Thầy đã ghi nhận câu hỏi của em.\n\n"
            f"- **Định hướng xuất phát:** Đối với bài toán này, trước hết em hãy xác định rõ các đại lượng đã cho và đại lượng cần tìm kiếm.\n\n"
            f"👉 *Câu hỏi gợi mở:* Giả thiết bài toán đang cho em những dữ kiện nào, và công thức nào liên hệ trực tiếp giữa các đại lượng đó?"
        )

    # 5. Fallback for C4: Check topic-specific overrides first
    q_lower = (question_latex or "").lower()
    if r"p \Rightarrow q" in q_lower or "p => q" in q_lower or "q => r" in q_lower or "kéo theo" in q_lower or "mệnh đề" in q_lower or "bảng chân trị" in q_lower or "logic" in q_lower:
        return (
            "💡 **Gợi ý phương pháp giải Socratic (Toán logic mệnh đề & suy luận kéo theo):**\n\n"
            f"- **Phân tích lựa chọn:** Em đang chọn {choice_label} cho mệnh đề kéo theo $P \\Rightarrow Q$.\n"
            "- **Kiến thức trọng tâm:** Bảng chân trị mệnh đề kéo theo $P \\Rightarrow Q$ chỉ nhận giá trị **Sai** duy nhất khi $P$ Đúng và $Q$ Sai ($1 \\Rightarrow 0 = 0$). Trong mọi trường hợp khác, mệnh đề kéo theo luôn Đúng.\n"
            "- **Lưu ý bẫy thường gặp:** Nhiều bạn lầm tưởng khi $Q$ sai thì $P$ có thể nhận bất kỳ giá trị nào, quên mất rằng nếu $P$ đúng thì $P \\Rightarrow Q$ sẽ bị sai mâu thuẫn giả thiết.\n\n"
            "👉 *Câu hỏi gợi mở:* Giả thiết cho biết $P \\Rightarrow Q$ là Đúng và $Q$ là Sai. Nếu $P$ là Đúng thì mệnh đề $P \\Rightarrow Q$ sẽ mang giá trị Đúng hay Sai?\n"
        )
    elif "cấp số cộng" in q_lower or "công sai" in q_lower or "u_n" in q_lower:
        return (
            "💡 **Gợi ý phương pháp giải Socratic (Dãy số & Cấp số cộng):**\n\n"
            f"- **Phân tích lựa chọn:** Em đang chọn {choice_label}.\n"
            "- **Kiến thức trọng tâm:** Số hạng tổng quát của cấp số cộng $(u_n)$ là $u_n = u_1 + (n - 1)d$.\n"
            "- **Lưu ý bẫy thường gặp:** Sai lầm phổ biến là quên trừ 1 ở chỉ số (lấy $u_1 + n \\times d$ thay vì $(n-1)d$).\n\n"
            "👉 *Câu hỏi gợi mở:* Giữa số hạng đầu $u_1$ và số hạng thứ $n$, có bao nhiêu khoảng cách công sai $d$?\n"
        )
    elif r"log" in q_lower or "tập xác định" in q_lower:
        return (
            "💡 **Gợi ý phương pháp giải Socratic (Hàm số Lũy thừa, Mũ & Logarit):**\n\n"
            f"- **Phân tích lựa chọn:** Em đang chọn {choice_label}.\n"
            "- **Kiến thức trọng tâm:** Biểu thức $\\log_a f(x)$ xác định khi và chỉ khi $f(x) > 0$ nghiêm ngặt và cơ số $0 < a \\neq 1$.\n"
            "- **Lưu ý bẫy thường gặp:** Nhầm dấu bất phương trình hoặc lấy nhầm khoảng nghiệm của tam thức bậc hai.\n\n"
            "👉 *Câu hỏi gợi mở:* Khi giải bất phương trình $f(x) > 0$, khoảng nghiệm của tam thức bậc hai nằm ở phía trong hay phía ngoài hai nghiệm?\n"
        )
    elif "chính tả" in q_lower or "xán lạn" in q_lower or "sáng lạng" in q_lower:
        return (
            "💡 **Gợi ý phương pháp giải Socratic (Chính tả Tiếng Việt):**\n\n"
            f"- **Phân tích lựa chọn:** Em đang chọn {choice_label}.\n"
            "- **Kiến thức trọng tâm:** Từ Hán Việt chuẩn 'Xán lạn' (chữ 'xán' là rực rỡ, 'lạn' là sáng sủa, viết x-l). Trong từ điển tiếng Việt chuẩn không có từ 'sáng lạng'.\n"
            "- **Lưu ý bẫy thường gặp:** Lỗi đồng hóa ngữ âm với từ thuần Việt 'sáng sủa'.\n\n"
            "👉 *Câu hỏi gợi mở:* Trong từ điển chính tả tiếng Việt, từ chỉ tương lai rực rỡ bắt đầu bằng âm 'x' hay 's'?\n"
        )

    # If conversational open question without wrong option selection
    if student_query and (not options or not sel or sel in ("NONE", "NULL")):
        return (
            f"Chào em! Thầy đã ghi nhận thắc mắc của em về chuyên đề **{pattern_name}**.\n\n"
            f"- **Kiến thức cốt lõi:**\n{core_theorems}\n\n"
            f"👉 *Câu hỏi gợi mở:* {socratic_hint or 'Em hãy chia sẻ cụ thể bước làm hoặc hướng suy nghĩ hiện tại của mình để thầy cùng em gỡ rối nhé!'}"
        )

    return (
        f"💡 **Gợi ý phương pháp giải chuẩn ({pattern_name}):**\n\n"
        f"- **Kiến thức trọng tâm:**\n{core_theorems}\n\n"
        f"- **Lưu ý bẫy thường gặp:**\n{misconception}\n\n"
        f"👉 *Câu hỏi gợi mở:* {socratic_hint}\n"
    )


async def generate_socratic_guidance(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_context: dict[str, Any],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
    llm: Optional[ChatGoogleGenerativeAI] = None,
    routing_decision: Optional[RoutingDecision] = None,
    conversation_history: Optional[Sequence[Any]] = None,
) -> tuple[str, JudgeValidationResult, bool]:
    """Non-streaming guidance generator returning (response_text, judge_result, is_fallback)."""
    if routing_decision is None:
        routing_decision = await classify_intent(
            question_latex=question_latex,
            student_query=student_query,
            student_selected_option=student_selected_option,
            conversation_history=conversation_history,
        )

    category = routing_decision.category.value
    status = subgraph_context.get("status")
    has_anchor = bool(subgraph_context.get("anchor"))
    is_grounded = status == "GROUNDED" or subgraph_context.get("is_grounded", False) or (status is None and has_anchor)

    subgraph_payload = extract_subgraph_payload(subgraph_context)
    system_prompt, user_message = build_socratic_prompts(
        question_latex=question_latex,
        student_selected_option=student_selected_option,
        correct_option=correct_option,
        subgraph_payload=subgraph_payload,
        options=options,
        student_selected_option_text=student_selected_option_text,
        student_query=student_query,
        routing_decision=routing_decision,
    )

    # 1. Generate draft response with dynamic model fallback and reasonable timeout
    draft_text = ""
    last_gen_error: Optional[Exception] = None
    candidate_models = (
        [llm] if llm else [get_socratic_llm(m) for m in SOCRATIC_MODELS]
    )

    for current_llm in candidate_models:
        try:
            draft = await asyncio.wait_for(
                current_llm.ainvoke([
                    SystemMessage(content=system_prompt),
                    HumanMessage(content=user_message),
                ]),
                timeout=8.0,
            )
            content_raw = draft.content
            if isinstance(content_raw, list):
                draft_text = "".join(
                    item.get("text", "") if isinstance(item, dict) else str(item)
                    for item in content_raw
                ).strip()
            else:
                draft_text = str(content_raw).strip()
            if draft_text:
                break
        except Exception as exc:
            logger.warning("LLM draft generation attempt failed (%s). Retrying next candidate...", exc)
            last_gen_error = exc

    if not draft_text:
        logger.warning("All Socratic draft generation attempts failed (%s). Using resilient fallback.", last_gen_error)
        fallback = build_fallback_guidance(
            subgraph_payload, question_latex, options, student_selected_option, student_query, category=category
        )
        dummy_judge = JudgeValidationResult(
            is_passed=True,
            revealed_direct_answer=False,
            consistent_with_ground_truth=True,
            grounded_in_theorems=True,
            rejection_reason=f"LLM generation fallback: {last_gen_error}",
        )
        return fallback, dummy_judge, True

    # 2. Inspect with Adaptive LLM-as-a-Judge
    judge_result = await validate_draft_response(
        ai_draft_response=draft_text,
        correct_option=correct_option,
        core_theorems=subgraph_payload["core_theorems"],
        student_selected_option=student_selected_option,
        category=category,
        grounded=is_grounded,
    )

    if judge_result.is_passed:
        logger.info("Draft response PASSED pedagogical validation | category=%s", category)
        return draft_text, judge_result, False
    else:
        logger.warning("Draft response REJECTED by judge (%s) | category=%s. Reverting to safe fallback.", judge_result.rejection_reason, category)
        fallback = build_fallback_guidance(
            subgraph_payload, question_latex, options, student_selected_option, student_query, category=category
        )
        return fallback, judge_result, True


async def generate_socratic_guidance_stream(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_context: dict[str, Any],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
    llm: Optional[ChatGoogleGenerativeAI] = None,
    routing_decision: Optional[RoutingDecision] = None,
    conversation_history: Optional[Sequence[Any]] = None,
) -> AsyncIterator[str]:
    """Real-time SSE token generator with two-layer pedagogical gating."""
    text, judge_result, is_fallback = await generate_socratic_guidance(
        question_latex=question_latex,
        student_selected_option=student_selected_option,
        correct_option=correct_option,
        subgraph_context=subgraph_context,
        options=options,
        student_selected_option_text=student_selected_option_text,
        student_query=student_query,
        llm=llm,
        routing_decision=routing_decision,
        conversation_history=conversation_history,
    )

    # Stream out chunks with slight pacing for natural conversational rendering
    words = text.split(" ")
    for idx, word in enumerate(words):
        chunk = word if idx == len(words) - 1 else word + " "
        yield chunk
        await asyncio.sleep(0.015)
