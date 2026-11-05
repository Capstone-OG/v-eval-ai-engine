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
    "gemini-3.6-flash",
    "gemini-3.5-flash",
    "gemini-3.8-flash",
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


GREETING_WORDS = {
    "hi", "hello", "chào", "chao", "chào bạn", "chào thầy", "chào cô",
    "chào em", "alo", "helo", "hey", "hế lô", "bắt đầu", "start",
    "giúp em", "giúp em với", "xin chào", "good morning", "good evening",
    "hi gia sư", "chào gia sư", "thầy ơi", "thầy ơi giúp em", "dạ chào thầy",
    "chào thầy ạ", "em chào thầy", "em chào thầy ạ", "hi thầy", "hi bot"
}


def is_greeting(query: Optional[str], q_latex: Optional[str] = None, options: Optional[dict[str, str]] = None) -> bool:
    if query:
        clean_q = query.strip().lower().rstrip("!.,?~^:;")
        if clean_q in GREETING_WORDS or (len(clean_q) <= 20 and any(clean_q == g or clean_q.startswith(g + " ") for g in GREETING_WORDS)):
            return True
    text = (q_latex or "").strip().lower().rstrip("!.,?~^:;")
    if not text:
        return False
    if options and len(options) > 2 and any(len(str(v)) > 15 for v in options.values()):
        return False
    return text in GREETING_WORDS or (len(text) <= 20 and any(text == g or text.startswith(g + " ") for g in GREETING_WORDS))


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
        user_message = f'Học sinh nói: "{student_query or question_latex}".'
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
        user_message = f'Học sinh hỏi/thắc mắc: "{student_query or question_latex}".'
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
    student_query: Optional[str] = None,
) -> str:
    """Render grounded safe fallback text tailored by curriculum topic from Knowledge Graph."""
    # 0. Check Greeting First!
    if is_greeting(student_query, question_latex, options) or (not student_selected_option and len((student_query or question_latex or "").strip()) <= 20 and any(g in (student_query or question_latex or "").lower() for g in ("hi", "chào", "chao", "hello", "alo", "thầy ơi"))):
        return (
            "Chào em! Thầy là **Gia Sư AI Socratic** của Hệ thống Khảo thí V-Eval ĐGNL. "
            "Rất vui được đồng hành cùng em ôn luyện. Em đang gặp băn khoăn hay cần thầy gợi mở phương pháp ở bài tập nào, "
            "hoặc thuộc chuyên đề nào (Toán học, Tư duy Logic, Tiếng Việt, KHTN/KHXH)? Em hãy gửi đề bài hoặc câu hỏi cho thầy nhé!"
        )

    pattern_name = subgraph_payload.get("pattern_name", "Dạng bài chuẩn ĐGNL")
    core_theorems = subgraph_payload.get("core_theorems", "")
    misconception = subgraph_payload.get("misconception", "")
    socratic_hint = subgraph_payload.get("socratic_hint", "")

    q_lower = (question_latex or "").lower()
    sel = (student_selected_option or "").strip().upper()
    sel_text = (options or {}).get(sel) if options else None
    choice_label = f"phương án **{sel}**" + (f" ({sel_text})" if sel_text else "") if sel else "phương án đã chọn"

    # Specific topic override if subgraph was generic
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
                timeout=25.0,
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
        fallback = build_fallback_guidance(subgraph_payload, question_latex, options, student_selected_option, student_query)
        dummy_judge = JudgeValidationResult(
            is_passed=True,
            revealed_direct_answer=False,
            consistent_with_ground_truth=True,
            grounded_in_theorems=True,
            rejection_reason=f"LLM generation fallback: {last_gen_error}",
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

    # 3. Inspect with LLM-as-a-Judge
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
        fallback = build_fallback_guidance(subgraph_payload, question_latex, options, student_selected_option, student_query)
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
