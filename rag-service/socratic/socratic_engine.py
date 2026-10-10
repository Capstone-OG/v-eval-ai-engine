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
    anchor = subgraph_context.get("anchor") or {}
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


def build_socratic_prompts(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_payload: dict[str, str],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
) -> tuple[str, str]:
    """Construct pedagogically strict system and user messages."""
    pattern_name = subgraph_payload["pattern_name"]
    core_theorems = subgraph_payload["core_theorems"]
    trap_name = subgraph_payload["trap_name"]
    misconception = subgraph_payload["misconception"]
    socratic_hint = subgraph_payload["socratic_hint"]

    options_formatted = ""
    if options:
        options_formatted = "\nCác phương án của câu hỏi:\n" + "\n".join(
            f"{k}. {v}" for k, v in sorted(options.items())
        )

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
1. BƯỚC 1 - GHI NHẬN THÂN THIỆN: Chỉ ra lỗi tư duy học sinh vừa vướng phải (ví dụ: "Có vẻ em đang tính nhầm tần số góc thành pi/T...").
2. BƯỚC 2 - NHẮC LẠI ĐỊNH LÝ: Trích dẫn 1 công thức hoặc định lý bản lề từ giáo trình để học sinh làm mỏ neo.
3. BƯỚC 3 - CÂU HỎI GỢI MỞ: Đặt DUY NHẤT 1 câu hỏi gợi mở then chốt để học sinh tự suy nghĩ và tính toán bước tiếp theo.
4. QUY TẮC CẤM:
   - TUYỆT ĐỐI KHÔNG tính ra kết quả số học cuối cùng.
   - TUYỆT ĐỐI KHÔNG nói đáp án đúng là phương án nào (A, B, C, D).
   - Ngắn gọn, súc tích (dưới 150 từ), giọng điệu ân cần, khích lệ.
"""

    user_message = f"""Câu hỏi em vừa làm sai là:
{question_latex}{options_formatted}

Em đã chọn đáp án {student_choice_desc}. Thầy/Cô có thể chỉ ra giúp em tại sao em suy luận sai và em nên kiểm tra lại điều gì không ạ?"""

    return system_prompt, user_message


def build_fallback_guidance(subgraph_payload: dict[str, str]) -> str:
    """Render grounded safe fallback text directly from Knowledge Graph."""
    pattern_name = subgraph_payload["pattern_name"]
    core_theorems = subgraph_payload["core_theorems"]
    misconception = subgraph_payload["misconception"]
    socratic_hint = subgraph_payload["socratic_hint"]

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
        fallback = build_fallback_guidance(subgraph_payload)
        dummy_judge = JudgeValidationResult(
            is_passed=False,
            revealed_direct_answer=False,
            consistent_with_ground_truth=False,
            grounded_in_theorems=False,
            rejection_reason=f"LLM generation failed: {exc}",
        )
        return fallback, dummy_judge, True

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
        fallback = build_fallback_guidance(subgraph_payload)
        return fallback, judge_result, True


async def generate_socratic_guidance_stream(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_context: dict[str, Any],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
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
        llm=llm,
    )

    # Stream out chunks with slight pacing for natural conversational rendering
    words = text.split(" ")
    for idx, word in enumerate(words):
        chunk = word if idx == len(words) - 1 else word + " "
        yield chunk
        await asyncio.sleep(0.015)
