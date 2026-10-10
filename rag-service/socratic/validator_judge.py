"""
validator_judge.py — LLM-as-a-Judge Automated Pedagogical Inspector (Core Flow 4 / Phase 4)
============================================================================================
Autonomous pedagogical judge evaluating AI Tutor draft responses before they are
streamed to students.

Verification Criteria:
----------------------
1. Zero Solution-Leaking (revealed_direct_answer = False):
   AI Tutor must ONLY guide step-by-step; NEVER calculate the final result or tell
   the student which option to pick (e.g., "chọn A", "đáp án là 20pi").
2. Ground-Truth Consistency (consistent_with_ground_truth = True):
   Guiding hints must steer the student towards the verified correct option.
3. Strict Curriculum Grounding (grounded_in_theorems = True):
   Hints must strictly adhere to verified theorems in the Knowledge Graph.
"""

from __future__ import annotations

import json
import logging
import os
import re
from typing import Optional, Sequence

from dotenv import load_dotenv
from google import genai
from google.genai import types
from pydantic import BaseModel, Field

_RAG_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(_RAG_ROOT, ".env"))
load_dotenv()

logger = logging.getLogger(__name__)

JUDGE_MODELS: Sequence[str] = (
    "gemini-flash-lite-latest",
    "gemini-3.6-flash",
    "gemini-3.8-flash",
)


class JudgeValidationResult(BaseModel):
    is_passed: bool = Field(description="True if the response strictly passes all pedagogical standards, False if violated.")
    revealed_direct_answer: bool = Field(description="True if AI revealed the final numerical result or told the student which option to pick.")
    consistent_with_ground_truth: bool = Field(description="True if hints guide towards the correct answer, False if misleading.")
    grounded_in_theorems: bool = Field(description="True if explanations adhere to the provided core theorems.")
    rejection_reason: str = Field(default="", description="Detailed reason if the response was rejected.")


def get_genai_client() -> genai.Client:
    api_key = os.environ.get("GOOGLE_API_KEY", "")
    if not api_key:
        raise EnvironmentError("GOOGLE_API_KEY is not set in environment.")
    return genai.Client(api_key=api_key)


def build_judge_prompt(
    ai_draft_response: str,
    correct_option: str,
    core_theorems: str,
    student_selected_option: str = "",
) -> str:
    return f"""Bạn là Hội đồng Thẩm định Sư phạm (Pedagogical Judge Validator) của hệ thống Khảo thí V-Eval.
Nhiệm vụ của bạn là kiểm tra NGHIÊM NGẶT bản thảo phản hồi của Gia sư AI trước khi gửi cho học sinh:

[BẢN THẢO CÂU TRẢ LỜI CỦA GIA SƯ AI]:
{ai_draft_response}

[CHÂN LÝ ĐỀ THI & TRI THỨC CHUẨN]:
- Đáp án đúng của câu hỏi: {correct_option}
- Phương án học sinh đã chọn (SAI): {student_selected_option or 'N/A'}
- Định lý / Công thức chuẩn trong Knowledge Graph:
{core_theorems}

TIÊU CHÍ ĐÁNH GIÁ (BẮT BUỘC):
1. KHÔNG GIẢI THAY (revealed_direct_answer):
   - Đặt là TRUE nếu AI tính ra kết quả số học cuối cùng (ví dụ: "v_max = 20pi") hoặc nói thẳng đáp án (ví dụ: "chọn phương án A", "đáp án là A").
   - Đặt là FALSE nếu AI chỉ đặt câu hỏi gợi mở, nhắc công thức tổng quát và để học sinh tự tính toán.

2. NHẤT QUÁN VỚI ĐÁP ÁN ĐÚNG (consistent_with_ground_truth):
   - Đặt là TRUE nếu lập luận và câu hỏi gợi mở đang hướng học sinh về phương án đúng ({correct_option}).
   - Đặt là FALSE nếu gợi ý sai lệch hoặc vô tình củng cố phương án sai.

3. BÁM SÁT ĐỊNH LÝ (grounded_in_theorems):
   - Đặt là TRUE nếu công thức và kiến thức nhắc đến nằm trong hoặc phù hợp với Định lý chuẩn ở trên.
   - Đặt là FALSE nếu bịa đặt định lý hoặc dùng kiến thức ngoài chương trình.

QUY TẮC PHÊ DUYỆT (is_passed):
- is_passed = TRUE khi và chỉ khi: revealed_direct_answer == FALSE AND consistent_with_ground_truth == TRUE AND grounded_in_theorems == TRUE.
- Nếu không đạt, ghi rõ lý do trong rejection_reason.
"""


def _fast_heuristic_guard(ai_draft_response: str, correct_option: str) -> Optional[JudgeValidationResult]:
    """Rapid pre-check to detect blatant direct answer leaks."""
    text_lower = ai_draft_response.lower()
    corr_upper = correct_option.strip().upper()
    
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
                consistent_with_ground_truth=True,
                grounded_in_theorems=True,
                rejection_reason=f"Heuristic violation: AI directly stated the answer option '{corr_upper}'."
            )
    return None


async def validate_draft_response(
    ai_draft_response: str,
    correct_option: str,
    core_theorems: str,
    student_selected_option: str = "",
    client: Optional[genai.Client] = None,
) -> JudgeValidationResult:
    """Validate AI Tutor's drafted response via LLM-as-a-Judge."""
    # 1. Fast heuristic pre-check
    quick_fail = _fast_heuristic_guard(ai_draft_response, correct_option)
    if quick_fail:
        logger.warning("Judge fast heuristic rejection: %s", quick_fail.rejection_reason)
        return quick_fail

    # 2. LLM Judge validation with structured schema output
    client = client or get_genai_client()
    prompt = build_judge_prompt(ai_draft_response, correct_option, core_theorems, student_selected_option)

    last_error: Optional[Exception] = None
    for model_name in JUDGE_MODELS:
        try:
            response = client.models.generate_content(
                model=model_name,
                contents=prompt,
                config=types.GenerateContentConfig(
                    response_mime_type="application/json",
                    response_schema=JudgeValidationResult,
                    temperature=0.0,
                ),
            )
            data = json.loads(response.text)
            result = JudgeValidationResult(**data)
            
            # Enforce is_passed rule consistency
            if result.revealed_direct_answer or not result.consistent_with_ground_truth or not result.grounded_in_theorems:
                result.is_passed = False
                if not result.rejection_reason:
                    result.rejection_reason = "Pedagogical constraints violated."
            
            logger.info("Judge validation complete | model=%s | is_passed=%s", model_name, result.is_passed)
            return result
        except Exception as exc:
            logger.warning("Judge model %s failed: %s", model_name, exc)
            last_error = exc

    # Fail-safe: if all LLM judges fail, reject by default to prevent unvetted leaks
    logger.error("All judge models failed. Invoking safe rejection: %s", last_error)
    return JudgeValidationResult(
        is_passed=False,
        revealed_direct_answer=False,
        consistent_with_ground_truth=False,
        grounded_in_theorems=False,
        rejection_reason=f"Judge system unavailable ({last_error}). Safe fallback triggered."
    )
