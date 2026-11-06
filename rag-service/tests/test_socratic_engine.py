"""
test_socratic_engine.py — Unit Tests for Socratic Engine & LLM-as-a-Judge (Offline)
===================================================================================
Covers prompt formatting, heuristic answer-leak defense, subgraph hydration,
and fallback logic without requiring external network access.
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
    heuristic_route,
)
from socratic.socratic_engine import (
    build_fallback_guidance,
    build_socratic_prompts,
    extract_subgraph_payload,
)
from socratic.validator_judge import (
    JudgeValidationResult,
    _fast_heuristic_guard,
    build_judge_prompt,
    required_criteria,
)


def test_heuristic_detects_direct_answer_leaks():
    # Direct leak phrases
    assert _fast_heuristic_guard("Chào em, em hãy chọn phương án A nhé.", "A") is not None
    assert _fast_heuristic_guard("Do đó đáp án đúng là B.", "B") is not None
    assert _fast_heuristic_guard("Em hãy chọn A để được điểm tối đa.", "A") is not None
    assert _fast_heuristic_guard("kết quả là c", "C") is not None

    # Safe Socratic guidance should NOT trigger leak
    assert _fast_heuristic_guard("Em hãy xem lại công thức v_max = omega * A nhé.", "A") is None
    assert _fast_heuristic_guard("Tần số góc omega bằng bao nhiêu nhỉ?", "B") is None


def test_judge_prompt_construction():
    prompt = build_judge_prompt(
        ai_draft_response="Hãy kiểm tra chu kỳ T.",
        correct_option="A",
        core_theorems="1. omega = 2pi/T",
        student_selected_option="B",
    )
    assert "[BẢN THẢO CÂU TRẢ LỜI CỦA GIA SƯ AI]" in prompt
    assert "Đáp án đúng của câu hỏi: A" in prompt
    assert "Phương án học sinh đã chọn (SAI): B" in prompt
    assert "1. omega = 2pi/T" in prompt


def test_extract_subgraph_payload_with_matched_trap():
    subgraph = {
        "anchor": {
            "pattern_name": "Dao động điều hòa",
            "core_theorems": "v_max = omega * A",
            "fast_solving_heuristics": "Tính omega trước.",
        },
        "matched_trap": {
            "trap_name": "Quên số 2",
            "misconception_explanation": "Tính omega = pi/T thay vì 2pi/T.",
            "socratic_hint": "Một chu kỳ tương ứng bao nhiêu radian?",
        },
    }
    payload = extract_subgraph_payload(subgraph)
    assert payload["pattern_name"] == "Dao động điều hòa"
    assert payload["core_theorems"] == "v_max = omega * A"
    assert payload["trap_name"] == "Quên số 2"
    assert payload["misconception"] == "Tính omega = pi/T thay vì 2pi/T."
    assert payload["socratic_hint"] == "Một chu kỳ tương ứng bao nhiêu radian?"


def test_extract_subgraph_payload_with_candidate_traps():
    subgraph = {
        "anchor": {"pattern_name": "Hàm phân thức", "core_theorems": "Tiệm cận đứng: mẫu = 0"},
        "candidate_traps": [
            {
                "trap_name": "Quên nghiệm tử",
                "wrong_option": "B",
                "misconception_explanation": "Chưa kiểm tra nghiệm tử số.",
                "socratic_hint": "Thay x vào tử xem sao?",
            },
            {
                "trap_name": "Nhầm tiệm cận ngang",
                "wrong_option": "D",
                "misconception_explanation": "Nhầm bậc tử và mẫu.",
                "socratic_hint": "x tiến ra vô cực thì sao?",
            },
        ],
    }
    payload = extract_subgraph_payload(subgraph)
    assert "Quên nghiệm tử" in payload["misconception"]
    assert "Nhầm tiệm cận ngang" in payload["misconception"]


def test_build_socratic_prompts_enforces_guardrails():
    payload = {
        "pattern_name": "Dao động điều hòa",
        "core_theorems": "v_max = omega * A",
        "trap_name": "Lỗi omega",
        "misconception": "Tính sai omega",
        "socratic_hint": "Gợi ý chu kỳ",
    }
    sys_prompt, user_msg = build_socratic_prompts(
        question_latex="Tính v_max với A=5, T=0.5",
        student_selected_option="B",
        correct_option="A",
        subgraph_payload=payload,
        options={"A": "20pi", "B": "10pi"},
        student_selected_option_text="10pi cm/s",
    )
    assert "BÍ MẬT TUYỆT ĐỐI — CẤM NÓI RA" in sys_prompt
    assert "CẤM GIẢI THAY" in sys_prompt
    assert "Tính v_max với A=5, T=0.5" in user_msg
    assert "10pi cm/s" in user_msg


def test_build_fallback_guidance_contains_verified_theorems():
    payload = {
        "pattern_name": "Tiệm cận đồ thị hàm số",
        "core_theorems": "lim x->x0 = +-inf",
        "misconception": "Quên điều kiện mẫu khác nghiệm tử",
        "socratic_hint": "Tử số có bằng 0 tại x0 không?",
    }
    fb = build_fallback_guidance(payload)
    assert "💡 **Gợi ý phương pháp giải chuẩn (Tiệm cận đồ thị hàm số):**" in fb
    assert "lim x->x0 = +-inf" in fb
    assert "Quên điều kiện mẫu khác nghiệm tử" in fb
    assert "👉 *Câu hỏi gợi mở:* Tử số có bằng 0 tại x0 không?" in fb


def test_judge_validation_result_strict_pass():
    # All 3 criteria must be strictly satisfied
    res_pass = JudgeValidationResult(
        is_passed=True,
        revealed_direct_answer=False,
        consistent_with_ground_truth=True,
        grounded_in_theorems=True,
    )
    assert res_pass.is_passed is True

    res_leak = JudgeValidationResult(
        is_passed=False,
        revealed_direct_answer=True,
        consistent_with_ground_truth=True,
        grounded_in_theorems=True,
        rejection_reason="Leaked answer",
    )
    assert res_leak.is_passed is False


def test_build_socratic_prompts_c1_persona():
    payload = {
        "pattern_name": "Dao động điều hòa",
        "core_theorems": "v_max = omega * A",
        "trap_name": "Lỗi",
        "misconception": "Lỗi",
        "socratic_hint": "Gợi ý",
    }
    decision = RoutingDecision(
        category=IntentCategory.C1,
        student_mood=StudentMood.CURIOUS,
        target_focus=TargetFocus.PERSONA,
        has_task_context=True,
    )
    sys_prompt, user_msg = build_socratic_prompts(
        question_latex="Tính v_max",
        student_selected_option="B",
        correct_option="A",
        subgraph_payload=payload,
        student_query="Thầy có chức vụ gì trong trường?",
        routing_decision=decision,
    )
    assert "TÌNH HUỐNG C1: XÃ GIAO / HỎI VỀ GIA SƯ" in sys_prompt
    assert "TUYỆT ĐỐI KHÔNG nhồi nhét công thức toán" in sys_prompt
    assert "Thầy có chức vụ gì trong trường?" in user_msg


def test_build_socratic_prompts_c2_attitude_and_task():
    payload = {
        "pattern_name": "Dao động điều hòa",
        "core_theorems": "v_max = omega * A",
        "trap_name": "Quên omega",
        "misconception": "Nhầm công thức",
        "socratic_hint": "Xem lại omega",
    }
    decision = RoutingDecision(
        category=IntentCategory.C2,
        student_mood=StudentMood.SKEPTICAL,
        target_focus=TargetFocus.CONCEPT_EXPLANATION,
        has_task_context=True,
    )
    sys_prompt, user_msg = build_socratic_prompts(
        question_latex="Tính v_max",
        student_selected_option="B",
        correct_option="A",
        subgraph_payload=payload,
        options={"A": "20pi", "B": "10pi"},
        student_selected_option_text="10pi",
        student_query="Thầy có bằng thạc sĩ chưa mà bảo câu B của em sai?",
        routing_decision=decision,
    )
    assert "TÌNH HUỐNG C2: THÁI ĐỘ + THẮC MẮC BÀI TOÁN" in sys_prompt
    assert "Tiếp nhận thái độ/sự hoài nghi của học sinh với sự khiêm tốn" in sys_prompt
    assert "Thầy có bằng thạc sĩ chưa mà bảo câu B của em sai?" in user_msg


def test_build_socratic_prompts_c3_meta_question():
    payload = {
        "pattern_name": "Cực trị hàm số",
        "core_theorems": "f'(x) = 0 và đổi dấu",
        "trap_name": "Nghiệm bội chẵn",
        "misconception": "Bội chẵn không đổi dấu",
        "socratic_hint": "Kiểm tra dấu qua nghiệm",
    }
    decision = RoutingDecision(
        category=IntentCategory.C3,
        student_mood=StudentMood.CURIOUS,
        target_focus=TargetFocus.EXAM_VALIDITY,
        has_task_context=True,
    )
    sys_prompt, user_msg = build_socratic_prompts(
        question_latex="Tìm m để hàm có cực trị",
        student_selected_option="C",
        correct_option="A",
        subgraph_payload=payload,
        student_query="Đề này có cho thiếu điều kiện tham số m không thầy?",
        routing_decision=decision,
    )
    assert "TÌNH HUỐNG C3: NGHI NGỜ ĐỀ BÀI / TÍNH HỢP LỆ" in sys_prompt
    assert "Đánh giá cao tinh thần phản biện khoa học" in sys_prompt
    assert "Đề này có cho thiếu điều kiện tham số m không thầy?" in user_msg


def test_build_socratic_prompts_c5_external():
    payload = {
        "pattern_name": "Dạng bài chuẩn ĐGNL",
        "core_theorems": "Định lý",
        "trap_name": "Bẫy",
        "misconception": "Sai lầm",
        "socratic_hint": "Gợi ý",
    }
    decision = RoutingDecision(
        category=IntentCategory.C5,
        student_mood=StudentMood.NEUTRAL,
        target_focus=TargetFocus.STEP_HINT,
        has_task_context=False,
    )
    sys_prompt, user_msg = build_socratic_prompts(
        question_latex="Tính tích phân từ 0 đến 1 của x^2 dx",
        student_selected_option="",
        correct_option="",
        subgraph_payload=payload,
        student_query="Giải giúp em bài tích phân này với: int_0^1 x^2 dx",
        routing_decision=decision,
    )
    assert "TÌNH HUỐNG C5: CÂU HỎI MỞ / BÀI TOÁN NGOÀI HỆ THỐNG" in sys_prompt
    assert "int_0^1 x^2 dx" in user_msg


def test_adaptive_judge_criteria_by_category():
    c1_criteria = required_criteria("C1")
    assert "friendly_and_pedagogical" in c1_criteria
    assert "redirect_to_learning" in c1_criteria
    assert "grounded_in_theorems" not in c1_criteria

    c4_criteria = required_criteria("C4", grounded=True)
    assert "consistent_with_ground_truth" in c4_criteria
    assert "grounded_in_theorems" in c4_criteria

    c5_criteria = required_criteria("C5")
    assert "logically_sound_hint" in c5_criteria


def test_fallback_guidance_c1_c2_c3_c5():
    payload = {
        "pattern_name": "Dao động điều hòa",
        "core_theorems": "v_max = omega * A",
        "socratic_hint": "Xem lại omega",
    }
    fb_c1 = build_fallback_guidance(payload, category="C1")
    assert "Gia Sư AI Socratic" in fb_c1
    assert "v_max = omega * A" not in fb_c1  # C1 does NOT leak formulas

    fb_c2 = build_fallback_guidance(payload, category="C2", student_selected_option="B")
    assert "phương án **B**" in fb_c2
    assert "v_max = omega * A" in fb_c2

    fb_c3 = build_fallback_guidance(payload, category="C3")
    assert "tinh thần phản biện khoa học" in fb_c3

    fb_c5 = build_fallback_guidance(payload, category="C5")
    assert "xác định rõ các đại lượng đã cho" in fb_c5
