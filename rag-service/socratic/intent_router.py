"""
intent_router.py — Track 2: Dynamic Intent & Tone Router (Decoupled Dual-Track)
==============================================================================
Classifies the student's latest message into one of five pedagogical cases,
independently from the knowledge retrieval of Track 1:

    C1  Pure chit-chat / persona      ("Thầy mấy tuổi?", "Hello robot")
    C2  Attitude + task               ("Thầy có bằng thạc sĩ chưa mà bảo A sai?")
    C3  Meta-question on the task     ("Đề này có cho thiếu vận tốc không?")
    C4  Pure in-graph task question   ("Vì sao câu A sai?", "Em tính ra 5 mà")
    C5  Out-of-graph / external       (a pasted new problem, general knowledge)

Two invariants:
* ``student_query`` is NEVER mixed into the Track 1 embedding — routing only
  decides how much of the Track 1 subgraph the tutor is allowed to use.
* When there is no task on screen (freeform chat), only C1 or C5 are valid.

Primary path: small LLM in JSON mode (single attempt, hard timeout).
Degraded path: deterministic lexical heuristics, so routing never blocks a turn.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
from dataclasses import asdict, dataclass
from enum import Enum
from typing import Any, Optional, Sequence

from pydantic import BaseModel

from socratic.llm_clients import get_fast_genai_client

logger = logging.getLogger(__name__)

ROUTER_MODEL: str = os.environ.get("INTENT_ROUTER_MODEL", "gemini-3.5-flash-lite")
ROUTER_TIMEOUT_S: float = float(os.environ.get("INTENT_ROUTER_TIMEOUT_S", "3.0"))
MAX_HISTORY_TURNS: int = 6
MAX_TURN_CHARS: int = 500


class IntentCategory(str, Enum):
    C1 = "C1"
    C2 = "C2"
    C3 = "C3"
    C4 = "C4"
    C5 = "C5"


class StudentMood(str, Enum):
    NEUTRAL = "NEUTRAL"
    SKEPTICAL = "SKEPTICAL"
    FRUSTRATED = "FRUSTRATED"
    CURIOUS = "CURIOUS"


class TargetFocus(str, Enum):
    PERSONA = "PERSONA"
    EXAM_VALIDITY = "EXAM_VALIDITY"
    STEP_HINT = "STEP_HINT"
    CONCEPT_EXPLANATION = "CONCEPT_EXPLANATION"


# Categories that consume the Track 1 knowledge subgraph.
KNOWLEDGE_CATEGORIES = frozenset({IntentCategory.C2, IntentCategory.C3, IntentCategory.C4})
# Categories that are meaningful without a task on screen.
TASKLESS_CATEGORIES = frozenset({IntentCategory.C1, IntentCategory.C5})

ROUTE_SOURCE_LLM = "LLM"
ROUTE_SOURCE_HEURISTIC = "HEURISTIC"


@dataclass
class RoutingDecision:
    category: IntentCategory
    student_mood: StudentMood
    target_focus: TargetFocus
    has_task_context: bool
    source: str = ROUTE_SOURCE_HEURISTIC

    @property
    def uses_knowledge(self) -> bool:
        return self.category in KNOWLEDGE_CATEGORIES and self.has_task_context

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["category"] = self.category.value
        data["student_mood"] = self.student_mood.value
        data["target_focus"] = self.target_focus.value
        return data


class _RouterSchema(BaseModel):
    """Structured output contract for the LLM router (JSON mode)."""

    category: IntentCategory
    student_mood: StudentMood
    target_focus: TargetFocus


# ---------------------------------------------------------------------------
# Task-context detection
# ---------------------------------------------------------------------------

_PLACEHOLDER_STEMS = frozenset({"", "câu hỏi của học sinh", "câu hỏi ôn tập đgnl"})
_WS_RE = re.compile(r"\s+")


def _norm(text: Optional[str]) -> str:
    return _WS_RE.sub(" ", (text or "").strip().lower())


def has_task_context(question_latex: Optional[str], student_query: Optional[str] = None) -> bool:
    """True when a real exam item is on screen.

    Upstream services copy ``student_query`` into ``question_latex`` for freeform
    chat (the field is mandatory). Embedding that copy is exactly the semantic
    contamination we must avoid, so such payloads count as "no task".
    """
    stem = _norm(question_latex)
    if stem in _PLACEHOLDER_STEMS:
        return False
    query = _norm(student_query)
    return not (query and stem == query)


def normalize_history(history: Optional[Sequence[Any]]) -> list[dict[str, str]]:
    """Keep the last few turns as ``{"role": "student"|"tutor", "text": str}``."""
    turns: list[dict[str, str]] = []
    for item in history or []:
        if isinstance(item, BaseModel):
            item = item.model_dump()
        if not isinstance(item, dict):
            continue
        text = str(item.get("text") or "").strip()
        if not text:
            continue
        role = "tutor" if str(item.get("role", "")).lower() in ("tutor", "ai", "assistant", "model") else "student"
        turns.append({"role": role, "text": text[:MAX_TURN_CHARS]})
    return turns[-MAX_HISTORY_TURNS:]


# ---------------------------------------------------------------------------
# Degraded mode: deterministic lexical heuristics
# ---------------------------------------------------------------------------

_PERSONA_MARKERS = (
    "thầy là ai", "thầy là gì", "thầy sẽ làm gì", "thầy làm gì", "thầy giúp gì", "bạn là ai",
    "chức vụ", "mấy tuổi", "bao nhiêu tuổi", "thạc sĩ", "tiến sĩ", "bằng cấp", "uy tín",
    "robot", "bot", "chatbot", "hello", "hi", "chào", "xin chào", "alo", "cảm ơn", "thanks",
    "người thật", "con người", "vai trò", "có vợ", "đẹp trai", "ăn cơm",
)
_SKEPTIC_MARKERS = (
    "thạc sĩ", "tiến sĩ", "bằng cấp", "uy tín", "có chắc", "chắc không", "chắc chưa",
    "biết gì", "đòi", "xạo", "bịp", "dỏm", "tin được", "nhìn mặt",
)
_FRUSTRATED_MARKERS = ("khó quá", "không hiểu", "ko hiểu", "chán", "bực", "nản", "mãi không", "bó tay", "điên")
_EXAM_VALIDITY_MARKERS = (
    "đề sai", "đề bài sai", "đề lỗi", "đề có vấn đề", "đề cho thiếu", "thiếu dữ kiện", "thiếu số liệu",
    "thiếu dữ liệu", "cho thiếu", "vô nghiệm", "không có đáp án", "đáp án sai", "đáp án bị sai",
    "lỗi hiển thị", "hiển thị lỗi", "không giải được", "không đủ dữ kiện", "đề thiếu",
)
_TASK_MARKERS = (
    "vì sao", "tại sao", "sao lại", "công thức", "phương án", "đáp án", "bước", "tính", "giải",
    "gợi ý", "sai ở đâu", "chỗ nào", "định lý", "hiểu", "câu này", "bài này", "chọn",
)
_KNOWLEDGE_MARKERS = ("công thức", "định lý", "định nghĩa", "là gì", "cách tính", "khái niệm", "tính chất")
_PROBLEM_MARKERS = (
    "cho hàm số", "tích phân", "đạo hàm", "phương trình", "bất phương trình", "chứng minh",
    "tìm m", "tìm x", "giải giúp", "giải hộ", "bài toán", "tính giá trị",
)
_OPTION_MENTION_RE = re.compile(r"(?<!\w)[abcd](?!\w)")
_MATH_CHARS_RE = re.compile(r"[=^\\$∫≤≥√π]|\d")
_TOKEN_RE = re.compile(r"\w+")


def _contains(text: str, markers: Sequence[str]) -> bool:
    """Multi-word markers match as substrings; short single words match whole tokens only."""
    tokens = set(_TOKEN_RE.findall(text))
    for marker in markers:
        if " " in marker or len(marker) > 4:
            if marker in text:
                return True
        elif marker in tokens:
            return True
    return False


def _looks_like_problem(text: str) -> bool:
    return len(text) >= 25 and (len(_MATH_CHARS_RE.findall(text)) >= 3 or _contains(text, _PROBLEM_MARKERS))


def _token_overlap(a: str, b: str) -> float:
    ta, tb = set(_TOKEN_RE.findall(a)), set(_TOKEN_RE.findall(b))
    return len(ta & tb) / len(ta) if ta and tb else 0.0


def _infer_mood(query: str) -> StudentMood:
    if _contains(query, _SKEPTIC_MARKERS):
        return StudentMood.SKEPTICAL
    if _contains(query, _FRUSTRATED_MARKERS):
        return StudentMood.FRUSTRATED
    if "?" in query or _contains(query, ("vì sao", "tại sao", "như thế nào", "là gì")):
        return StudentMood.CURIOUS
    return StudentMood.NEUTRAL


def _focus_for(category: IntentCategory, query: str) -> TargetFocus:
    if category == IntentCategory.C1:
        return TargetFocus.PERSONA
    if category == IntentCategory.C3:
        return TargetFocus.EXAM_VALIDITY
    if category == IntentCategory.C5 or _contains(query, ("bước", "gợi ý", "bắt đầu", "tiếp theo")):
        return TargetFocus.STEP_HINT
    return TargetFocus.CONCEPT_EXPLANATION


def heuristic_route(
    question_latex: Optional[str],
    student_query: Optional[str],
) -> RoutingDecision:
    """Deterministic routing used when the LLM router is unavailable."""
    task = has_task_context(question_latex, student_query)
    query = _norm(student_query)

    if task:
        if not query:
            category = IntentCategory.C4  # "explain my mistake" button without free text
        elif _contains(query, _EXAM_VALIDITY_MARKERS):
            category = IntentCategory.C3
        elif _looks_like_problem(query) and _token_overlap(query, _norm(question_latex)) < 0.2:
            category = IntentCategory.C5  # a different problem pasted next to the current one
        else:
            persona = _contains(query, _PERSONA_MARKERS)
            attitude = persona or _contains(query, _SKEPTIC_MARKERS) or _contains(query, _FRUSTRATED_MARKERS)
            task_signal = _contains(query, _TASK_MARKERS) or bool(_OPTION_MENTION_RE.search(query))
            if attitude and task_signal:
                category = IntentCategory.C2
            elif persona:
                category = IntentCategory.C1
            else:
                category = IntentCategory.C4
    else:
        if _looks_like_problem(query) or _contains(query, _KNOWLEDGE_MARKERS):
            category = IntentCategory.C5
        else:
            category = IntentCategory.C1

    return RoutingDecision(
        category=category,
        student_mood=_infer_mood(query),
        target_focus=_focus_for(category, query),
        has_task_context=task,
        source=ROUTE_SOURCE_HEURISTIC,
    )


def enforce_routing_constraints(decision: RoutingDecision, fallback: RoutingDecision) -> RoutingDecision:
    """Repair LLM outputs that are impossible for the current context."""
    if not decision.has_task_context and decision.category not in TASKLESS_CATEGORIES:
        decision.category = fallback.category if fallback.category in TASKLESS_CATEGORIES else IntentCategory.C1
    if decision.category == IntentCategory.C1:
        decision.target_focus = TargetFocus.PERSONA
    elif decision.category == IntentCategory.C3:
        decision.target_focus = TargetFocus.EXAM_VALIDITY
    elif decision.target_focus == TargetFocus.PERSONA:
        decision.target_focus = _focus_for(decision.category, "")
    return decision


# ---------------------------------------------------------------------------
# Primary path: LLM router in JSON mode
# ---------------------------------------------------------------------------


def build_router_prompt(
    question_latex: Optional[str],
    student_query: Optional[str],
    student_selected_option: Optional[str],
    history: Sequence[dict[str, str]],
    task_context: bool,
) -> str:
    history_text = "\n".join(
        f"- {'Học sinh' if t['role'] == 'student' else 'Gia sư'}: {t['text'][:200]}" for t in history
    ) or "(chưa có)"
    stem = (question_latex or "").strip()[:400] if task_context else "(không có)"
    selected = (student_selected_option or "").strip().upper()
    selected = selected if task_context and selected not in ("", "NONE", "NULL") else "(không có)"
    return f"""Bạn là bộ định tuyến ý định (intent router) cho Gia sư AI Socratic. Phân loại TIN NHẮN MỚI NHẤT của học sinh.

ĐỊNH NGHĨA:
- C1: Chỉ xã giao / hỏi về gia sư (vai trò, chức vụ, tuổi, bằng cấp), trêu đùa, cảm ơn — KHÔNG hỏi gì về bài.
- C2: Vừa có thái độ (trêu, hoài nghi năng lực gia sư, cáu) VỪA thắc mắc về bài đang làm.
- C3: Nghi ngờ tính hợp lệ của đề bài đang làm (thiếu dữ kiện, vô nghiệm, đáp án sai, lỗi hiển thị).
- C4: Thắc mắc học thuật thuần về bài đang làm (vì sao sai, công thức, bước giải, "em tính ra X").
- C5: Hỏi bài toán khác hoặc kiến thức ngoài bài đang làm (dán đề mới, hỏi khái niệm chung).
RÀNG BUỘC: Nếu KHÔNG có bài đang làm thì chỉ được chọn C1 hoặc C5.
student_mood: NEUTRAL | SKEPTICAL | FRUSTRATED | CURIOUS
target_focus: PERSONA | EXAM_VALIDITY | STEP_HINT | CONCEPT_EXPLANATION

[CÓ BÀI ĐANG LÀM]: {"Có" if task_context else "Không"}
[ĐỀ BÀI ĐANG LÀM (rút gọn)]: {stem}
[PHƯƠNG ÁN HỌC SINH ĐÃ CHỌN]: {selected}
[LỊCH SỬ GẦN NHẤT]:
{history_text}
[TIN NHẮN MỚI NHẤT]: "{(student_query or '').strip()[:500]}"

Chỉ trả về JSON theo schema."""


async def classify_intent(
    question_latex: Optional[str],
    student_query: Optional[str],
    student_selected_option: Optional[str] = None,
    conversation_history: Optional[Sequence[Any]] = None,
    timeout_s: float = ROUTER_TIMEOUT_S,
) -> RoutingDecision:
    """Route the turn with the LLM; degrade to heuristics on any failure."""
    fallback = heuristic_route(question_latex, student_query)
    if not (student_query or "").strip():
        return fallback  # nothing to interpret: the "explain my mistake" default applies

    prompt = build_router_prompt(
        question_latex,
        student_query,
        student_selected_option,
        normalize_history(conversation_history),
        fallback.has_task_context,
    )
    try:
        from google.genai import types

        response = await asyncio.wait_for(
            get_fast_genai_client().aio.models.generate_content(
                model=ROUTER_MODEL,
                contents=prompt,
                config=types.GenerateContentConfig(
                    response_mime_type="application/json",
                    response_schema=_RouterSchema,
                    temperature=0.0,
                ),
            ),
            timeout=timeout_s,
        )
        parsed = _RouterSchema(**json.loads(response.text))
        decision = RoutingDecision(
            category=parsed.category,
            student_mood=parsed.student_mood,
            target_focus=parsed.target_focus,
            has_task_context=fallback.has_task_context,
            source=ROUTE_SOURCE_LLM,
        )
        return enforce_routing_constraints(decision, fallback)
    except Exception as exc:
        logger.info("Intent router degraded to heuristics (%s: %s).", type(exc).__name__, exc)
        return fallback

