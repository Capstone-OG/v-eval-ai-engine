"""
test_intent_router.py — Unit Tests for Track 2 Intent & Tone Router (Offline)
=============================================================================
Tests deterministic heuristic routing, mood inference, and constraints enforcement
across all 5 pedagogical cases (C1–C5).
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest

from socratic.intent_router import (
    IntentCategory,
    RoutingDecision,
    StudentMood,
    TargetFocus,
    enforce_routing_constraints,
    has_task_context,
    heuristic_route,
)


def test_has_task_context_detects_freeform_and_real_stems():
    # Empty or placeholder stems
    assert has_task_context(None, "hi") is False
    assert has_task_context("", "hi") is False
    assert has_task_context("Câu hỏi ôn tập ĐGNL", "hi") is False

    # Semantic contamination case: upstream passed query as question_latex
    assert has_task_context("Thầy là ai vậy?", "Thầy là ai vậy?") is False
    assert has_task_context("  chào thầy  ", "chào thầy") is False

    # Real exam stem on screen
    assert has_task_context("Một vật dao động điều hòa với phương trình x = 5cos(2pi*t)", "Vì sao em sai?") is True
    assert has_task_context("Cho hàm số y = f(x) liên tục trên R", "") is True


def test_heuristic_route_c1_persona_and_chitchat():
    # With exam task on screen, but student only asks persona/chit-chat
    stem = "Một vật dao động điều hòa x = 5cos(2pi*t)"
    r1 = heuristic_route(stem, "Thầy có chức vụ gì trong trường?")
    assert r1.category == IntentCategory.C1
    assert r1.target_focus == TargetFocus.PERSONA

    r2 = heuristic_route(stem, "Chào thầy, thầy mấy tuổi rồi?")
    assert r2.category == IntentCategory.C1

    r3 = heuristic_route(stem, "Thầy là robot hay người thật?")
    assert r3.category == IntentCategory.C1


def test_heuristic_route_c2_attitude_and_task():
    stem = "Một con lắc lò xo dao động với tần số f"
    r1 = heuristic_route(stem, "Thầy có bằng thạc sĩ chưa mà bảo đáp án A của em sai?")
    assert r1.category == IntentCategory.C2
    assert r1.student_mood == StudentMood.SKEPTICAL

    r2 = heuristic_route(stem, "Thầy có chắc không, câu B em tính kỹ lắm rồi mà?")
    assert r2.category == IntentCategory.C2
    assert r2.student_mood == StudentMood.SKEPTICAL


def test_heuristic_route_c3_meta_question_validity():
    stem = "Cho hình chóp S.ABCD có đáy là hình vuông cạnh a"
    r1 = heuristic_route(stem, "Đề bài này có cho thiếu dữ kiện chiều cao không thầy?")
    assert r1.category == IntentCategory.C3
    assert r1.target_focus == TargetFocus.EXAM_VALIDITY

    r2 = heuristic_route(stem, "Em giải ra vô nghiệm, hình như đề bị sai rồi.")
    assert r2.category == IntentCategory.C3
    assert r2.target_focus == TargetFocus.EXAM_VALIDITY


def test_heuristic_route_c4_pure_task():
    stem = "Tính vận tốc cực đại của dao động điều hòa"
    # Empty query (clicked explain mistake button)
    r_empty = heuristic_route(stem, "")
    assert r_empty.category == IntentCategory.C4

    # Pure task question
    r_task = heuristic_route(stem, "Vì sao phương án A lại sai vậy thầy?")
    assert r_task.category == IntentCategory.C4

    r_calc = heuristic_route(stem, "Công thức liên hệ giữa v_max và omega là gì ạ?")
    assert r_task.category == IntentCategory.C4


def test_heuristic_route_c5_external_or_out_of_graph():
    # Freeform chat without task on screen: pasting a new math problem
    r_free = heuristic_route(None, "Cho hàm số y = x^3 - 3x^2 + 2, tìm m để phương trình có 3 nghiệm")
    assert r_free.category == IntentCategory.C5
    assert r_free.has_task_context is False

    # Exam on screen, but student pasted completely different problem
    stem = "Dao động điều hòa có chu kỳ T = 2s"
    diff_problem = "Cho tích phân int_0^1 f(x)dx = 3 và int_0^1 g(x)dx = 5. Tính giá trị biểu thức"
    r_diff = heuristic_route(stem, diff_problem)
    assert r_diff.category == IntentCategory.C5


def test_enforce_routing_constraints_repairs_taskless_intent():
    # If LLM hallucinates C4 when there is NO task on screen, must repair to C1 or C5
    invalid_decision = RoutingDecision(
        category=IntentCategory.C4,
        student_mood=StudentMood.NEUTRAL,
        target_focus=TargetFocus.CONCEPT_EXPLANATION,
        has_task_context=False,
    )
    fallback = RoutingDecision(
        category=IntentCategory.C1,
        student_mood=StudentMood.NEUTRAL,
        target_focus=TargetFocus.PERSONA,
        has_task_context=False,
    )
    repaired = enforce_routing_constraints(invalid_decision, fallback)
    assert repaired.category == IntentCategory.C1
    assert repaired.target_focus == TargetFocus.PERSONA

