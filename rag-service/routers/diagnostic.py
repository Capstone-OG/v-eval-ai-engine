"""
routers/diagnostic.py — Competency Diagnosis API Endpoints
============================================================
Handles student diagnostic test evaluation for V-Eval Core Flow 1:
- Estimates IRT 2PL ability theta_0 via Maximum Likelihood Estimation (MLE)
- Computes per-skill BKT initial mastery P(L0) = Sigmoid(theta)
- Maps results to placement classes (FOUNDATION, ACCELERATION, BREAKTHROUGH)
- Generates radar chart data and Socratic AI commentary
"""

from __future__ import annotations

import logging
from typing import Optional

from fastapi import APIRouter, HTTPException, status

from config import GOOGLE_API_KEY, get_llm
from diagnostic_engine import (
    DIFFICULTY_TO_B,
    PLACEMENT_THRESHOLD_BREAKTHROUGH,
    PLACEMENT_THRESHOLD_FOUNDATION,
    VACT_MAX_SCORE,
    AnswerItem,
    run_diagnostic_analysis,
)
from schemas import (
    DiagnosticAnalyzeRequest,
    DiagnosticAnalyzeResponse,
    DiagnosticDomainScoreDto,
    DiagnosticRadarAxisDto,
    DiagnosticSkillPriorDto,
)

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/v1/diagnostic", tags=["Competency Diagnosis"])


def _generate_rule_based_commentary(
    placement_class: str,
    domain_scores: list[DiagnosticDomainScoreDto],
    theta_0: float,
) -> str:
    """Generate deterministic pedagogical commentary when LLM is offline or unconfigured."""
    placement_labels = {
        "FOUNDATION": "Lớp Nền tảng (Foundation) — tập trung củng cố kiến thức trọng tâm",
        "ACCELERATION": "Lớp Tăng tốc (Acceleration) — rèn luyện phương pháp và tư duy nâng cao",
        "BREAKTHROUGH": "Lớp Bứt phá (Breakthrough) — chinh phục các câu hỏi vận dụng cao 900+",
    }
    label = placement_labels.get(placement_class, placement_class)

    if not domain_scores:
        return f"Học sinh được phân lớp vào {label} (ước lượng năng lực θ = {theta_0:.2f})."

    sorted_domains = sorted(domain_scores, key=lambda d: d.accuracy_pct, reverse=True)
    best = sorted_domains[0]
    worst = sorted_domains[-1]

    if len(sorted_domains) == 1 or best.domain_id == worst.domain_id:
        return (
            f"Kết quả phân tích cho thấy năng lực tổng quan θ = {theta_0:.2f}. "
            f"Bạn được đề xuất tham gia {label}. "
            f"Lĩnh vực '{best.domain_name}' đạt độ chính xác {best.accuracy_pct:.1f}%."
        )

    return (
        f"Kết quả phân tích cho thấy năng lực tổng quan θ = {theta_0:.2f}. "
        f"Bạn được đề xuất tham gia {label}. "
        f"Thế mạnh nổi bật của bạn là '{best.domain_name}' (độ chính xác {best.accuracy_pct:.1f}%). "
        f"Cần ưu tiên bổ trợ kiến thức ở lĩnh vực '{worst.domain_name}' ({worst.accuracy_pct:.1f}%) "
        f"để đạt mục tiêu điểm thi mong đợi."
    )


def _generate_ai_commentary(
    placement_class: str,
    domain_scores: list[DiagnosticDomainScoreDto],
    theta_0: float,
    target_score: int,
) -> str:
    """Generate concise, encouraging Socratic commentary using Gemini with safe fallback."""
    # Check if Google API Key is set and valid
    if not GOOGLE_API_KEY or GOOGLE_API_KEY == "mock_key_for_testing" or "..." in GOOGLE_API_KEY:
        return _generate_rule_based_commentary(placement_class, domain_scores, theta_0)

    try:
        llm = get_llm()
        domain_summary = ", ".join(
            f"{d.domain_name}: {d.accuracy_pct:.1f}%" for d in domain_scores
        )
        prompt = (
            "Bạn là cố vấn học tập AI của hệ thống luyện thi đánh giá năng lực ĐHQG-HCM (V-Eval). "
            "Dựa trên kết quả bài kiểm tra chẩn đoán 30 câu của học sinh:\n"
            f"- Ước lượng năng lực IRT θ: {theta_0:.2f} (thang -3.0 đến +3.0)\n"
            f"- Lớp đề xuất: {placement_class}\n"
            f"- Điểm mục tiêu: {target_score}/1200\n"
            f"- Kết quả từng phần: {domain_summary}\n\n"
            "Hãy đưa ra nhận xét ngắn gọn (2-3 câu, tiếng Việt), giọng điệu tích cực, sư phạm, chỉ rõ thế mạnh "
            "và 1 trọng tâm cần ưu tiên cải thiện trong lộ trình học tiếp theo."
        )
        response = llm.invoke(prompt)
        raw_content = response.content
        if isinstance(raw_content, list):
            commentary = "".join(
                part if isinstance(part, str) else str(part.get("text", "") if isinstance(part, dict) else part)
                for part in raw_content
            ).strip()
        else:
            commentary = str(raw_content).strip()

        if commentary:
            return commentary
    except Exception as exc:
        logger.warning("Failed to generate AI commentary via LLM: %s. Using fallback.", exc)

    return _generate_rule_based_commentary(placement_class, domain_scores, theta_0)


@router.post(
    "/analyze",
    response_model=DiagnosticAnalyzeResponse,
    summary="Analyze diagnostic test submission",
    description=(
        "Processes 30 diagnostic test answers to estimate student IRT ability theta_0, "
        "compute BKT initial mastery priors P(L0) for all skills, determine placement class, "
        "generate radar chart comparison data, and produce pedagogical AI commentary."
    ),
)
def analyze_diagnostic_submission(
    payload: DiagnosticAnalyzeRequest,
) -> DiagnosticAnalyzeResponse:
    """Evaluate diagnostic submission and return learning profile parameters."""
    if not payload.answers:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Answers list cannot be empty.",
        )

    try:
        # Convert Pydantic models to engine dataclasses
        engine_answers = [
            AnswerItem(
                question_id=ans.question_id,
                skill_id=ans.skill_id,
                domain_id=ans.domain_id,
                difficulty_level=ans.difficulty_level,
                is_correct=ans.is_correct,
                time_spent_seconds=ans.time_spent_seconds,
            )
            for ans in payload.answers
        ]

        # Domain name lookup dictionary
        domain_names = {d.domain_id: d.domain_name for d in payload.domain_names}

        # Run core psychometric analysis
        result = run_diagnostic_analysis(
            answers=engine_answers,
            domain_names=domain_names,
            target_score=payload.target_score,
            all_skill_ids=payload.all_skill_ids or None,
        )

        # Convert domain scores to DTOs
        domain_dtos = [
            DiagnosticDomainScoreDto(
                domain_id=ds.domain_id,
                domain_name=ds.domain_name,
                theta_domain=ds.theta_domain,
                total_questions=ds.total_questions,
                correct_count=ds.correct_count,
                accuracy_pct=ds.accuracy_pct,
            )
            for ds in result.domain_scores
        ]

        # Convert skill priors to DTOs
        prior_dtos = [
            DiagnosticSkillPriorDto(
                skill_id=sp.skill_id,
                domain_id=sp.domain_id,
                theta_skill=sp.theta_skill,
                p_l0=sp.p_l0,
                source=sp.source,
            )
            for sp in result.skill_priors
        ]

        # Convert radar axes to DTOs
        radar_dtos = [
            DiagnosticRadarAxisDto(
                domain_id=ra.domain_id,
                domain_name=ra.domain_name,
                student_pct=ra.student_pct,
                benchmark_pct=ra.benchmark_pct,
            )
            for ra in result.radar_chart
        ]

        # Generate AI / rule-based pedagogical commentary
        commentary = _generate_ai_commentary(
            placement_class=result.placement_class,
            domain_scores=domain_dtos,
            theta_0=result.theta_0,
            target_score=payload.target_score,
        )

        return DiagnosticAnalyzeResponse(
            student_id=payload.student_id,
            submission_id=payload.submission_id,
            theta_0=result.theta_0,
            placement_class=result.placement_class,
            domain_scores=domain_dtos,
            skill_priors=prior_dtos,
            radar_chart=radar_dtos,
            ai_commentary=commentary,
        )

    except Exception as exc:
        logger.error("Diagnostic analysis error: %s", exc, exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"Failed to process diagnostic analysis: {str(exc)}",
        )


@router.get(
    "/config",
    summary="Get diagnostic engine configuration and thresholds",
    description="Returns IRT parameters, placement thresholds, and score limits for client inspection.",
)
def get_diagnostic_config() -> dict:
    """Return static diagnostic configuration parameters."""
    return {
        "max_score": VACT_MAX_SCORE,
        "difficulty_levels": DIFFICULTY_TO_B,
        "placement_thresholds": {
            "foundation_max_theta": PLACEMENT_THRESHOLD_FOUNDATION,
            "breakthrough_min_theta": PLACEMENT_THRESHOLD_BREAKTHROUGH,
            "classes": ["FOUNDATION", "ACCELERATION", "BREAKTHROUGH"],
        },
        "bkt_prior_clamping": {
            "min_prior": 0.05,
            "max_prior": 0.95,
        },
    }
