"""
socratic_engine.py â€” Socratic AI Tutor Guidance Engine (Core Flow 4 / Phase 4)
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
    "gemini-flash-lite-latest",
    "gemini-3.6-flash",
    "gemini-3.8-flash",
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
    )


def extract_subgraph_payload(subgraph_context: dict[str, Any]) -> dict[str, str]:
    """Extract and normalize knowledge elements from SocraticSubgraph dict."""
    status = subgraph_context.get("status")
    has_anchor = bool(subgraph_context.get("anchor"))
    is_grounded = status == "GROUNDED" or subgraph_context.get("is_grounded", False) or (status is None and has_anchor)
    anchor = (subgraph_context.get("anchor") or {}) if is_grounded else {}
    pattern_name = anchor.get("pattern_name") or subgraph_context.get("pattern_name") or "Dáº¡ng bÃ i chuáº©n ÄGNL"
    core_theorems = anchor.get("core_theorems") or subgraph_context.get("core_theorems") or "Váº­n dá»¥ng Ä‘á»‹nh lÃ½ vÃ  tÃ­nh cháº¥t cÆ¡ báº£n."
    fast_heuristics = anchor.get("fast_solving_heuristics") or subgraph_context.get("fast_solving_heuristics") or ""

    matched_trap = subgraph_context.get("matched_trap") or {}
    candidate_traps = subgraph_context.get("candidate_traps") or []

    if matched_trap:
        trap_name = matched_trap.get("trap_name", "Lá»—i tÃ­nh toÃ¡n hoáº·c Ã¡p dá»¥ng cÃ´ng thá»©c")
        misconception = matched_trap.get("misconception_explanation", "ChÆ°a kiá»ƒm tra ká»¹ Ä‘iá»u kiá»‡n bÃ i toÃ¡n.")
        socratic_hint = matched_trap.get("socratic_hint", "Em hÃ£y Ä‘á»‘i chiáº¿u láº¡i cÃ¡c bÆ°á»›c tÃ­nh toÃ¡n.")
    elif candidate_traps:
        trap_summaries = []
        for t in candidate_traps:
            opt = f" (phÆ°Æ¡ng Ã¡n {t.get('wrong_option')})" if t.get("wrong_option") else ""
            trap_summaries.append(f"- {t.get('trap_name')}{opt}: {t.get('misconception_explanation')}")
        trap_name = "CÃ¡c báº«y tÆ° duy thÆ°á»ng gáº·p trong dáº¡ng bÃ i nÃ y"
        misconception = "\n".join(trap_summaries)
        socratic_hint = candidate_traps[0].get("socratic_hint", "Em hÃ£y xem láº¡i Ä‘iá»u kiá»‡n rÃ ng buá»™c cá»§a bÃ i toÃ¡n.")
    else:
        trap_name = "Lá»—i nháº­n thá»©c phá»• biáº¿n"
        misconception = "CÃ³ thá»ƒ em Ä‘ang nháº§m láº«n á»Ÿ bÆ°á»›c chuyá»ƒn Ä‘á»•i Ä‘áº¡i lÆ°á»£ng hoáº·c Ä‘iá»u kiá»‡n biÃªn."
        socratic_hint = "Em hÃ£y kiá»ƒm tra láº¡i Ä‘á»‹nh nghÄ©a cá»§a cÃ¡c Ä‘áº¡i lÆ°á»£ng trong Ä‘á» bÃ i nhÃ©."

    return {
        "pattern_name": pattern_name,
        "core_theorems": core_theorems,
        "fast_heuristics": fast_heuristics,
        "trap_name": trap_name,
        "misconception": misconception,
        "socratic_hint": socratic_hint,
    }


GREETING_WORDS = {
    "hi", "hello", "chào", "chao", "chào bạn", "chào thầy", "chào cô",
    "chào em", "alo", "helo", "hey", "hế lô", "bắt đầu", "start",
    "giúp em", "giúp em với", "xin chào", "good morning", "good evening"
}


def is_greeting(query: Optional[str], q_latex: Optional[str] = None, options: Optional[dict[str, str]] = None) -> bool:
    text = (query or q_latex or "").strip().lower().rstrip("!.,?~^:;")
    if not text:
        return False
    if options and len(options) > 2 and any(len(str(v)) > 15 for v in options.values()):
        return False
    return text in GREETING_WORDS or (len(text) <= 15 and any(text == g or text.startswith(g + " ") for g in GREETING_WORDS))

def build_socratic_prompts(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_payload: dict[str, str],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
) -> tuple[str, str]:
    """Construct pedagogically strict system and user messages."""
    pattern_name = subgraph_payload["pattern_name"]
    core_theorems = subgraph_payload["core_theorems"]
    trap_name = subgraph_payload["trap_name"]
    misconception = subgraph_payload["misconception"]
    socratic_hint = subgraph_payload["socratic_hint"]

    # 1. Standalone Greeting Check
    if is_greeting(student_query, question_latex, options):
        system_prompt = """Bạn là Gia sư AI Socratic của Hệ thống Khảo thí V-Eval ĐGNL ĐHQG TP.HCM.
Học sinh vừa gửi lời chào hỏi hoặc bắt đầu cuộc trò chuyện.
Hãy phản hồi với giọng điệu ân cần, ấm áp, truyền cảm hứng của một người thầy:
1. Chào đón học sinh thân thiện.
2. Giới thiệu ngắn gọn phương pháp gợi mở Socratic (thầy sẽ đồng hành đặt câu hỏi dẫn dắt từng bước để em tự tìm ra hướng giải đúng).
3. Hỏi xem hôm nay học sinh muốn cùng thầy chinh phục bài tập nào, chuyên đề nào (Toán học, Tư duy Logic, Tiếng Việt, Khoa học Tự nhiên/Xã hội), và mời em gửi câu hỏi hoặc đề bài để cùng bắt đầu."""
        user_message = f"""Học sinh nói: "{student_query or question_latex}"."""
        return system_prompt, user_message

    # 2. General Open Question Check (No multiple-choice options or no selected option)
    has_real_options = bool(options and len(options) >= 2 and any(not str(v).startswith("Phương án ") for v in options.values()))
    if not has_real_options or not student_selected_option or student_selected_option in ("NONE", "null"):
        system_prompt = f"""Bạn là Gia sư AI Socratic của Hệ thống Khảo thí V-Eval ĐGNL ĐHQG TP.HCM.
Nhiệm vụ của bạn là DẪN DẮT GỢI MỞ THEO PHƯƠNG PHÁP SOCRATES. TUYỆT ĐỐI CẤM GIẢI BÀI THAY HOẶC ĐƯA ĐÁP ÁN SẴN.

[NGUỒN TRI THỨC]:
- Chuyên đề: {pattern_name}
- Định lý / Kiến thức nền tảng:
{core_theorems}

[NGUYÊN TẮC HƯỚNG DẪN]:
1. Giải thích tự nhiên, ngắn gọn điểm mấu chốt học sinh đang băn khoăn.
2. Nêu một công thức hoặc định lý làm mỏ neo nếu cần.
3. Đặt duy nhất 1 câu hỏi dẫn dắt then chốt để học sinh tự suy nghĩ và tìm hướng giải quyết."""
        user_message = f"""Học sinh hỏi/thắc mắc: "{student_query or question_latex}"."""
        return system_prompt, user_message

    # 3. Standard In-Exam Socratic Mode (with specific wrong choice)
    options_formatted = ""
    if options:
        opt_lines = [f"{k}. {v}" for k, v in sorted(options.items())]
        options_formatted = "\nCác phương án của câu hỏi:\n" + "\n".join(opt_lines)

    student_choice_desc = f"{student_selected_option}"
    if student_selected_option_text:
        student_choice_desc += f" ({student_selected_option_text})"

    system_prompt = f"""Bạn là Gia sư AI Socratic của Hệ thống Khảo thí V-Eval.
Học sinh vừa chọn phương án SAI trong một câu hỏi trắc nghiệm ĐGNL.
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
1. BƯỚC 1 - GHI NHẬN THÂN THIỆN: Chỉ ra lỗi tư duy học sinh vừa vướng phải.
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
) -> str:
    """Render grounded safe fallback text tailored by curriculum topic from Knowledge Graph."""
    pattern_name = subgraph_payload.get("pattern_name", "Dáº¡ng bÃ i chuáº©n ÄGNL")
    core_theorems = subgraph_payload.get("core_theorems", "")
    misconception = subgraph_payload.get("misconception", "")
    socratic_hint = subgraph_payload.get("socratic_hint", "")

    q_lower = (question_latex or "").lower()
    sel = (student_selected_option or "").strip().upper()
    sel_text = (options or {}).get(sel) if options else None
    choice_label = f"phÆ°Æ¡ng Ã¡n **{sel}**" + (f" ({sel_text})" if sel_text else "") if sel else "phÆ°Æ¡ng Ã¡n Ä‘Ã£ chá»n"

    # Specific topic override if subgraph was generic
    if r"p \Rightarrow q" in q_lower or "kÃ©o theo" in q_lower or "má»‡nh Ä‘á»" in q_lower:
        return (
            "💡 **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (ToÃ¡n logic má»‡nh Ä‘á» & suy luáº­n kÃ©o theo):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label} cho má»‡nh Ä‘á» kÃ©o theo $P \Rightarrow Q$.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Báº£ng chÃ¢n trá»‹ má»‡nh Ä‘á» kÃ©o theo $P \Rightarrow Q$ chá»‰ nháº­n giÃ¡ trá»‹ **Sai** duy nháº¥t khi $P$ ÄÃºng vÃ  $Q$ Sai ($1 \Rightarrow 0 = 0$). Trong má»i trÆ°á»ng há»£p khÃ¡c, má»‡nh Ä‘á» kÃ©o theo luÃ´n ÄÃºng.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Nhiá»u báº¡n láº§m tÆ°á»Ÿng khi $Q$ sai thÃ¬ $P$ cÃ³ thá»ƒ nháº­n báº¥t ká»³ giÃ¡ trá»‹ nÃ o, quÃªn máº¥t ráº±ng náº¿u $P$ Ä‘Ãºng thÃ¬ $P \Rightarrow Q$ sáº½ bá»‹ sai mÃ¢u thuáº«n giáº£ thiáº¿t.\n\n"
            "👉 *CÃ¢u há»i gá»£i má»Ÿ:* Giáº£ thiáº¿t cho biáº¿t $P \Rightarrow Q$ lÃ  ÄÃºng vÃ  $Q$ lÃ  Sai. Náº¿u $P$ lÃ  ÄÃºng thÃ¬ má»‡nh Ä‘á» $P \Rightarrow Q$ sáº½ mang giÃ¡ trá»‹ ÄÃºng hay Sai?\n"
        )
    elif "cáº¥p sá»‘ cá»™ng" in q_lower or "cÃ´ng sai" in q_lower or "u_n" in q_lower:
        return (
            "💡 **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (DÃ£y sá»‘ & Cáº¥p sá»‘ cá»™ng):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label}.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Sá»‘ háº¡ng tá»•ng quÃ¡t cá»§a cáº¥p sá»‘ cá»™ng $(u_n)$ lÃ  $u_n = u_1 + (n - 1)d$.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Sai láº§m phá»• biáº¿n lÃ  quÃªn trá»« 1 á»Ÿ chá»‰ sá»‘ (láº¥y $u_1 + n \times d$ thay vÃ¬ $(n-1)d$).\n\n"
            "👉 *CÃ¢u há»i gá»£i má»Ÿ:* Giá»¯a sá»‘ háº¡ng Ä‘áº§u $u_1$ vÃ  sá»‘ háº¡ng thá»© $n$, cÃ³ bao nhiÃªu khoáº£ng cÃ¡ch cÃ´ng sai $d$?\n"
        )
    elif r"log" in q_lower or "táº­p xÃ¡c Ä‘á»‹nh" in q_lower:
        return (
            "💡 **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (HÃ m sá»‘ LÅ©y thá»«a, MÅ© & Logarit):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label}.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Biá»ƒu thá»©c $\\log_a f(x)$ xÃ¡c Ä‘á»‹nh khi vÃ  chá»‰ khi $f(x) > 0$ nghiÃªm ngáº·t vÃ  cÆ¡ sá»‘ $0 < a \neq 1$.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Nháº§m dáº¥u báº¥t phÆ°Æ¡ng trÃ¬nh hoáº·c láº¥y nháº§m khoáº£ng nghiá»‡m cá»§a tam thá»©c báº­c hai.\n\n"
            "👉 *CÃ¢u há»i gá»£i má»Ÿ:* Khi giáº£i báº¥t phÆ°Æ¡ng trÃ¬nh $f(x) > 0$, khoáº£ng nghiá»‡m cá»§a tam thá»©c báº­c hai náº±m á»Ÿ phÃ­a trong hay phÃ­a ngoÃ i hai nghiá»‡m?\n"
        )
    elif "chÃ­nh táº£" in q_lower or "xÃ¡n láº¡n" in q_lower or "sÃ¡ng láº¡ng" in q_lower:
        return (
            "💡 **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (ChÃ­nh táº£ Tiáº¿ng Viá»‡t):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label}.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Tá»« HÃ¡n Viá»‡t chuáº©n 'XÃ¡n láº¡n' (chá»¯ 'xÃ¡n' lÃ  rá»±c rá»¡, 'láº¡n' lÃ  sÃ¡ng sá»§a, viáº¿t x-l). Trong tá»« Ä‘iá»ƒn tiáº¿ng Viá»‡t chuáº©n khÃ´ng cÃ³ tá»« 'sÃ¡ng láº¡ng'.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Lá»—i Ä‘á»“ng hÃ³a ngá»¯ Ã¢m vá»›i tá»« thuáº§n Viá»‡t 'sÃ¡ng sá»§a'.\n\n"
            "👉 *CÃ¢u há»i gá»£i má»Ÿ:* Trong tá»« Ä‘iá»ƒn chÃ­nh táº£ tiáº¿ng Viá»‡t, tá»« chá»‰ tÆ°Æ¡ng lai rá»±c rá»¡ báº¯t Ä‘áº§u báº±ng Ã¢m 'x' hay 's'?\n"
        )

    return (
        f"💡 **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i chuáº©n ({pattern_name}):**\n\n"
        f"- **Kiáº¿n thá»©c trá»ng tÃ¢m:**\n{core_theorems}\n\n"
        f"- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:**\n{misconception}\n\n"
        f"👉 *CÃ¢u há»i gá»£i má»Ÿ:* {socratic_hint}\n"
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
) -> tuple[str, JudgeValidationResult, bool]:
    """Non-streaming guidance generator returning (response_text, judge_result, is_fallback)."""
    subgraph_payload = extract_subgraph_payload(subgraph_context)
    system_prompt, user_message = build_socratic_prompts(
        question_latex=question_latex,
        student_selected_option=student_selected_option,
        correct_option=correct_option,
        subgraph_payload=subgraph_payload,
        options=options,
        student_selected_option_text=student_selected_option_text,
        student_query=student_query,
    )

    llm = llm or get_socratic_llm()
    
    # 1. Generate draft response
    try:
        draft = await llm.ainvoke([
            SystemMessage(content=system_prompt),
            HumanMessage(content=user_message),
        ])
        content_raw = draft.content
        if isinstance(content_raw, list):
            draft_text = "".join(
                item.get("text", "") if isinstance(item, dict) else str(item)
                for item in content_raw
            ).strip()
        else:
            draft_text = str(content_raw).strip()
    except Exception as exc:
        logger.error("Socratic draft generation failed: %s. Using fallback.", exc)
        fallback = build_fallback_guidance(subgraph_payload, question_latex, options, student_selected_option)
        dummy_judge = JudgeValidationResult(
            is_passed=False,
            revealed_direct_answer=False,
            consistent_with_ground_truth=False,
            grounded_in_theorems=False,
            rejection_reason=f"LLM generation failed: {exc}",
        )
        return fallback, dummy_judge, True

    # 2. Skip LLM-as-a-Judge for greetings
    if is_greeting(student_query, question_latex, options):
        judge_result = JudgeValidationResult(
            is_passed=True,
            revealed_direct_answer=False,
            consistent_with_ground_truth=True,
            grounded_in_theorems=True,
            rejection_reason="Greeting interaction passed directly",
        )
        return draft_text, judge_result, False

    # 2. Inspect with LLM-as-a-Judge
    judge_result = await validate_draft_response(
        ai_draft_response=draft_text,
        correct_option=correct_option,
        core_theorems=subgraph_payload["core_theorems"],
        student_selected_option=student_selected_option,
    )

    if judge_result.is_passed:
        logger.info("Draft response PASSED pedagogical validation.")
        return draft_text, judge_result, False
    else:
        logger.warning("Draft response REJECTED by judge (%s). Reverting to safe fallback.", judge_result.rejection_reason)
        fallback = build_fallback_guidance(subgraph_payload, question_latex, options, student_selected_option)
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
    )

    # Stream out chunks with slight pacing for natural conversational rendering
    words = text.split(" ")
    for idx, word in enumerate(words):
        chunk = word if idx == len(words) - 1 else word + " "
        yield chunk
        await asyncio.sleep(0.015)

