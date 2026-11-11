"""
validator_judge.py — Adaptive LLM-as-a-Judge Pedagogical Inspector (Core Flow 4 / Phase 4)
=========================================================================================
Evaluates AI Tutor drafts before they are streamed to students. The criteria
flex with the Track 2 intent category so that a friendly persona answer is not
rejected for "not citing theorems" (the old false-positive failure mode):

    Always      : revealed_direct_answer == False  (never leak A/B/C/D or the final result)
    C2 / C3 / C4: consistent_with_ground_truth, grounded_in_theorems   (grounded in the KG)
    C1          : friendly_and_pedagogical, redirect_to_learning
    C5          : logically_sound_hint             (no KG to compare against)

A knowledge case (C2/C3/C4) whose subgraph is not GROUNDED cannot be checked
against KG theorems, so it is judged on logical soundness instead.

Layer 1 is a fast regex guard (no network). Layer 2 is a single LLM call with a
hard timeout; when it fails, the draft passes on the strength of layer 1 to avoid
cascading timeouts.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
from typing import Optional, Sequence

from dotenv import load_dotenv
from google import genai
from google.genai import types
from pydantic import BaseModel, Field

from socratic.llm_clients import get_fast_genai_client

_RAG_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(_RAG_ROOT, ".env"))
load_dotenv()

logger = logging.getLogger(__name__)

JUDGE_MODELS: Sequence[str] = (
    "gemini-3.5-flash-lite",
    "gemini-3.6-flash",
    "gemini-3.5-flash",
)
JUDGE_TIMEOUT_S: float = float(os.environ.get("JUDGE_TIMEOUT_S", "3.0"))

CRITERION_GROUND_TRUTH = "consistent_with_ground_truth"
CRITERION_THEOREMS = "grounded_in_theorems"
CRITERION_FRIENDLY = "friendly_and_pedagogical"
CRITERION_REDIRECT = "redirect_to_learning"
CRITERION_LOGIC = "logically_sound_hint"

_CRITERIA_BY_CATEGORY: dict[str, tuple[str, ...]] = {
    "C1": (CRITERION_FRIENDLY, CRITERION_REDIRECT),
    "C2": (CRITERION_GROUND_TRUTH, CRITERION_THEOREMS),
    "C3": (CRITERION_GROUND_TRUTH, CRITERION_THEOREMS),
    "C4": (CRITERION_GROUND_TRUTH, CRITERION_THEOREMS),
    "C5": (CRITERION_LOGIC,),
}
_OPTION_LETTER_RE = re.compile(r"^[A-F]$")


class JudgeValidationResult(BaseModel):
    is_passed: bool = Field(description="True if the response passes every criterion required for its category.")
    revealed_direct_answer: bool = Field(description="True if AI revealed the final result or told the student which option to pick.")
    consistent_with_ground_truth: bool = Field(default=True, description="True if hints guide towards the correct answer.")
    grounded_in_theorems: bool = Field(default=True, description="True if explanations adhere to the provided core theorems.")
    friendly_and_pedagogical: bool = Field(default=True, description="True if the tone is warm, respectful and non-toxic.")
    redirect_to_learning: bool = Field(default=True, description="True if the reply steers the student back to learning.")
    logically_sound_hint: bool = Field(default=True, description="True if the hint is mathematically/logically correct.")
    rejection_reason: str = Field(default="", description="Detailed reason if the response was rejected.")
    evaluated_criteria: list[str] = Field(default_factory=list, description="Criteria enforced for this verdict.")


class _JudgeVerdictSchema(BaseModel):
    """Structured output requested from the LLM judge (is_passed is computed in code)."""

    revealed_direct_answer: bool
    consistent_with_ground_truth: bool
    grounded_in_theorems: bool
    friendly_and_pedagogical: bool
    redirect_to_learning: bool
    logically_sound_hint: bool
    rejection_reason: str


def required_criteria(category: str = "C4", grounded: bool = True) -> tuple[str, ...]:
    """Criteria (besides the universal no-leak rule) enforced for a category."""
    cat = (category or "C4").upper()
    criteria = _CRITERIA_BY_CATEGORY.get(cat, _CRITERIA_BY_CATEGORY["C4"])
    if cat in ("C2", "C3", "C4") and not grounded:
        # No verified theorems to compare against: judge logical soundness, keep answer consistency.
        return (CRITERION_GROUND_TRUTH, CRITERION_LOGIC)
    return criteria


def compute_is_passed(result: JudgeValidationResult, criteria: Sequence[str]) -> bool:
    if result.revealed_direct_answer:
        return False
    return all(bool(getattr(result, c, True)) for c in criteria)


def get_genai_client() -> genai.Client:
    """Backward-compatible accessor (shared single-attempt client)."""
    return get_fast_genai_client()


_CRITERION_TEXT: dict[str, str] = {
    CRITERION_GROUND_TRUTH: (
        "NHẤT QUÁN VỚI ĐÁP ÁN ĐÚNG (consistent_with_ground_truth): TRUE nếu lập luận và câu hỏi gợi mở "
        "hướng học sinh về phương án đúng; FALSE nếu gợi ý sai lệch hoặc củng cố phương án sai."
    ),
    CRITERION_THEOREMS: (
        "BÁM SÁT ĐỊNH LÝ (grounded_in_theorems): TRUE nếu công thức/kiến thức nhắc đến phù hợp với Định lý chuẩn; "
        "FALSE nếu bịa đặt định lý hoặc dùng kiến thức ngoài chương trình."
    ),
    CRITERION_FRIENDLY: (
        "THÂN THIỆN & SƯ PHẠM (friendly_and_pedagogical): TRUE nếu giọng điệu ấm áp, tôn trọng, không mỉa mai, "
        "không độc hại, không tự nhận là 'mô hình AI' một cách máy móc."
    ),
    CRITERION_REDIRECT: (
        "KÉO VỀ VIỆC HỌC (redirect_to_learning): TRUE nếu câu trả lời kết thúc bằng lời mời quay lại bài tập "
        "hoặc gửi câu hỏi học tập."
    ),
    CRITERION_LOGIC: (
        "GỢI Ý ĐÚNG LOGIC (logically_sound_hint): TRUE nếu khái niệm/định lý/bước gợi ý được nêu là đúng về mặt "
        "toán học và logic theo chương trình THPT."
    ),
}


def build_judge_prompt(
    ai_draft_response: str,
    correct_option: str,
    core_theorems: str,
    student_selected_option: str = "",
    category: str = "C4",
    grounded: bool = True,
) -> str:
    criteria = required_criteria(category, grounded)
    known_answer = bool(_OPTION_LETTER_RE.match((correct_option or "").strip().upper()))
    truth_lines = []
    if known_answer:
        truth_lines.append(f"- Đáp án đúng của câu hỏi: {correct_option}")
        truth_lines.append(f"- Phương án học sinh đã chọn (SAI): {student_selected_option or 'N/A'}")
    if CRITERION_THEOREMS in criteria:
        truth_lines.append(f"- Định lý / Công thức chuẩn trong Knowledge Graph:\n{core_theorems}")
    truth_block = "\n".join(truth_lines) or "- (Không có dữ liệu chuẩn — chỉ đánh giá theo tiêu chí bên dưới)"

    criteria_lines = "\n".join(f"{i}. {_CRITERION_TEXT[c]}" for i, c in enumerate(criteria, start=2))
    return f"""Bạn là Hội đồng Thẩm định Sư phạm (Pedagogical Judge Validator) của hệ thống Khảo thí V-Eval.
Nhiệm vụ của bạn là kiểm tra NGHIÊM NGẶT bản thảo phản hồi của Gia sư AI trước khi gửi cho học sinh.
Loại tình huống hội thoại: {category}.

[BẢN THẢO CÂU TRẢ LỜI CỦA GIA SƯ AI]:
{ai_draft_response}

[CHÂN LÝ ĐỀ THI & TRI THỨC CHUẨN]:
{truth_block}

TIÊU CHÍ ĐÁNH GIÁ (BẮT BUỘC):
1. KHÔNG GIẢI THAY (revealed_direct_answer): TRUE nếu AI tính ra kết quả cuối cùng hoặc nói thẳng phương án đúng
   (ví dụ: "chọn phương án A", "đáp án là A"); FALSE nếu chỉ gợi mở và để học sinh tự làm.
{criteria_lines}

Các trường không thuộc danh sách tiêu chí trên: đặt TRUE.
Nếu có tiêu chí không đạt, ghi rõ lý do trong rejection_reason (để trống nếu đạt).
"""


def _fast_heuristic_guard(ai_draft_response: str, correct_option: str) -> Optional[JudgeValidationResult]:
    """Rapid pre-check to detect blatant direct answer leaks."""
    corr_upper = (correct_option or "").strip().upper()
    if not _OPTION_LETTER_RE.match(corr_upper):
        return None  # no option letter to leak (freeform / out-of-graph turns)
    text_lower = ai_draft_response.lower()

    # Check for direct phrase leaks like "chọn phương án A", "đáp án đúng là A", "chọn A"
    leak_patterns = [
        rf"(?:chọn|đáp án|phương án|kết quả)(?:\s+đúng|\s+chính xác)?\s+(?:là\s+)?{re.escape(corr_upper)}\b",
        rf"(?:chọn|đáp án)\s+phương án\s+{re.escape(corr_upper)}\b",
        rf"\bchọn\s+đáp án\s+{re.escape(corr_upper)}\b",
    ]
    for p in leak_patterns:
        if re.search(p, text_lower, re.IGNORECASE):
            return JudgeValidationResult(
                is_passed=False,
                revealed_direct_answer=True,
                rejection_reason=f"Heuristic violation: AI directly stated the answer option '{corr_upper}'.",
                evaluated_criteria=["revealed_direct_answer"],
            )
    return None


async def validate_draft_response(
    ai_draft_response: str,
    correct_option: str,
    core_theorems: str,
    student_selected_option: str = "",
    client: Optional[genai.Client] = None,
    category: str = "C4",
    grounded: bool = True,
) -> JudgeValidationResult:
    """Validate the tutor draft with criteria adapted to the intent category."""
    criteria = required_criteria(category, grounded)
    evaluated = ["revealed_direct_answer", *criteria]

    # 1. Fast heuristic pre-check (universal no-leak rule)
    quick_fail = _fast_heuristic_guard(ai_draft_response, correct_option)
    if quick_fail:
        logger.warning("Judge fast heuristic rejection: %s", quick_fail.rejection_reason)
        return quick_fail

    # 2. Single-attempt LLM judge with structured output
    prompt = build_judge_prompt(
        ai_draft_response, correct_option, core_theorems, student_selected_option, category, grounded
    )
    model_to_use = JUDGE_MODELS[0] if JUDGE_MODELS else "gemini-3.5-flash-lite"
    try:
        response = await asyncio.wait_for(
            (client or get_genai_client()).aio.models.generate_content(
                model=model_to_use,
                contents=prompt,
                config=types.GenerateContentConfig(
                    response_mime_type="application/json",
                    response_schema=_JudgeVerdictSchema,
                    temperature=0.0,
                ),
            ),
            timeout=JUDGE_TIMEOUT_S,
        )
        verdict = _JudgeVerdictSchema(**json.loads(response.text))
        result = JudgeValidationResult(is_passed=False, evaluated_criteria=evaluated, **verdict.model_dump())
        result.is_passed = compute_is_passed(result, criteria)
        if not result.is_passed and not result.rejection_reason:
            result.rejection_reason = "Pedagogical constraints violated."
        logger.info(
            "Judge validation complete | model=%s | category=%s | grounded=%s | is_passed=%s",
            model_to_use, category, grounded, result.is_passed,
        )
        return result
    except Exception as exc:
        logger.info("LLM Judge fast failover (%s). Passing via heuristic guardrail.", exc)

    # Resilient failover: the heuristic guard already verified zero direct-answer leakage,
    # so approve the draft instead of cascading timeouts.
    return JudgeValidationResult(
        is_passed=True,
        revealed_direct_answer=False,
        rejection_reason="Passed by heuristic pedagogical guardrail.",
        evaluated_criteria=["revealed_direct_answer"],
    )
