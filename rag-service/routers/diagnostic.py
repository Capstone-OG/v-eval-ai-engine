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

import json
import logging
import time
from typing import Optional

import requests

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
    GenerateExamRequest,
    GenerateExamResponse,
    GeneratedQuestionItem,
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


# ---------------------------------------------------------------------------
# Dynamic AI Exam Generator Endpoint (Core Flow 1)
# ---------------------------------------------------------------------------

DOMAIN_CATALOG = {
    "dom_math": {
        "name": "Toán học & Phân tích số liệu",
        "skills": [
            ("sk_calc", "Giải tích & Hàm số"),
            ("sk_geom", "Hình học không gian & Véctơ"),
            ("sk_stat", "Xác suất & Thống kê ứng dụng"),
            ("sk_opt", "Tối ưu hóa đa biến & Quy hoạch"),
        ],
    },
    "dom_logic": {
        "name": "Tư duy Logic & Suy luận",
        "skills": [
            ("sk_prop", "Mệnh đề logic & Phủ định"),
            ("sk_deduct", "Suy luận suy diễn (Modus Tollens)"),
            ("sk_order", "Sắp xếp thứ tự logic & Điều kiện"),
            ("sk_data_logic", "Phân tích đồ thị & Ma trận bảng số"),
            ("sk_eval_arg", "Đánh giá luận cứ & Nhận diện ngụy biện"),
            ("sk_model_logic", "Mô hình hóa bài toán hệ thống"),
        ],
    },
    "dom_lang": {
        "name": "Sử dụng Ngôn ngữ & Văn học",
        "skills": [
            ("sk_spelling", "Chính tả & Chuẩn hóa từ vựng"),
            ("sk_rhetoric", "Biện pháp tu từ & Phong cách"),
            ("sk_syntax", "Ngữ pháp & Cấu tạo ngữ đoạn"),
            ("sk_reading", "Đọc hiểu văn bản học thuật"),
            ("sk_critique", "Phê bình & Đánh giá tư tưởng"),
            ("sk_synth_lang", "Tổng hợp văn bản hành chính"),
        ],
    },
    "dom_nat_sci": {
        "name": "Khoa học Tự nhiên (Lý - Hóa - Sinh)",
        "skills": [
            ("sk_physics", "Dao động cơ & Sóng điện từ"),
            ("sk_chem", "Cân bằng hóa học & Phản ứng vô cơ"),
            ("sk_bio", "Di truyền học phân tử & Mendel"),
            ("sk_bio_tech", "Công nghệ tế bào & Đột biến"),
        ],
    },
    "dom_soc_sci": {
        "name": "Khoa học Xã hội (Sử - Địa)",
        "skills": [
            ("sk_hist", "Lịch sử Việt Nam & Kháng chiến"),
            ("sk_geo", "Địa lý tự nhiên & Khí hậu nhiệt đới"),
            ("sk_hist_eval", "Đánh giá tác động sự kiện quốc tế"),
            ("sk_geo_strat", "Quy hoạch không gian kinh tế & BĐKH"),
        ],
    },
}

QUESTION_TEMPLATES = [
    # Math Templates
    {
        "domain_id": "dom_math", "skill_id": "sk_calc", "bloom": 1,
        "content": "Hàm số nào sau đây đồng biến trên khoảng $(-\\infty; +\\infty)$?",
        "options": ["$y = x^4 + 2x^2$", "$y = \\frac{2x - 1}{x + 1}$", "$y = x^3 + 3x - 1$", "$y = -x^3 + x$"],
        "correct": "C", "explanation": "Ta có $y' = 3x^2 + 3 > 0, \\forall x \\in \\mathbb{R}$, do đó hàm số luôn đồng biến trên $\\mathbb{R}$."
    },
    {
        "domain_id": "dom_math", "skill_id": "sk_calc", "bloom": 2,
        "content": "Tìm giá trị cực đại $y_{\\text{CĐ}}$ của hàm số $y = x^3 - 3x + 2$.",
        "options": ["$y_{\\text{CĐ}} = 0$", "$y_{\\text{CĐ}} = 4$", "$y_{\\text{CĐ}} = 2$", "$y_{\\text{CĐ}} = -1$"],
        "correct": "B", "explanation": "$y' = 3x^2 - 3 = 0 \\Leftrightarrow x = \\pm 1$. Tại $x = -1$, $y'' = -6 < 0$ nên đạt cực đại $y(-1) = 4$."
    },
    {
        "domain_id": "dom_math", "skill_id": "sk_geom", "bloom": 3,
        "content": "Cho hình chóp $S.ABC$ có đáy là tam giác vuông tại $B$, $SA \\perp (ABC)$. Biết $SA = a\\sqrt{3}, AB = a, BC = 2a$. Tính thể tích khối chóp $S.ABC$.",
        "options": ["$\\frac{a^3\\sqrt{3}}{6}$", "$a^3\\sqrt{3}$", "$\\frac{a^3}{3}$", "$\\frac{a^3\\sqrt{3}}{3}$"],
        "correct": "D", "explanation": "$V = \\frac{1}{3} S_{ABC} \\cdot SA = \\frac{1}{3} \\cdot \\frac{1}{2} a \\cdot 2a \\cdot a\\sqrt{3} = \\frac{a^3\\sqrt{3}}{3}$."
    },
    {
        "domain_id": "dom_math", "skill_id": "sk_stat", "bloom": 4,
        "content": "Một hộp chứa 5 quả cầu đỏ và 7 quả cầu xanh. Chọn ngẫu nhiên 3 quả cầu. Xác suất để chọn được ít nhất một quả cầu đỏ là:",
        "options": ["$\\frac{37}{44}$", "$\\frac{7}{44}$", "$\\frac{21}{44}$", "$\\frac{35}{44}$"],
        "correct": "A", "explanation": "Dùng biến cố đối: $P = 1 - \\frac{C_7^3}{C_{12}^3} = 1 - \\frac{35}{220} = \\frac{185}{220} = \\frac{37}{44}$."
    },
    {
        "domain_id": "dom_math", "skill_id": "sk_calc", "bloom": 5,
        "content": "Cho tích phân $I = \\int_0^1 x f'(x) dx = 5$ và $f(1) = 2$. Đánh giá giá trị của tích phân $J = \\int_0^1 f(x) dx$.",
        "options": ["$J = 7$", "$J = -3$", "$J = 3$", "$J = -7$"],
        "correct": "B", "explanation": "Tích phân từng phần: $I = [x f(x)]_0^1 - J = f(1) - J \\Rightarrow 5 = 2 - J \\Rightarrow J = -3$."
    },
    {
        "domain_id": "dom_math", "skill_id": "sk_opt", "bloom": 6,
        "content": "Thiết kế thùng chứa hình trụ không nắp thể tích $V = 54\\pi\\text{ m}^3$. Tìm bán kính đáy $R$ để diện tích vật liệu chế tạo nhỏ nhất.",
        "options": ["$R = 6\\text{ m}$", "$R = 2\\text{ m}$", "$R = 3\\text{ m}$", "$R = 3\\sqrt{3}\\text{ m}$"],
        "correct": "C", "explanation": "$S(R) = \\pi R^2 + \\frac{108\\pi}{R}$. Đạo hàm $S'(R) = 0 \\Leftrightarrow 2\\pi R = \\frac{108\\pi}{R^2} \\Leftrightarrow R^3 = 54/2 = 27 \\Rightarrow R = 3\\text{ m}$."
    },

    # Logic Templates
    {
        "domain_id": "dom_logic", "skill_id": "sk_prop", "bloom": 1,
        "content": "Mệnh đề phủ định của mệnh đề 'Mọi số tự nhiên đều lớn hơn 0' là:",
        "options": ["Mọi số tự nhiên đều nhỏ hơn 0", "Tồn tại ít nhất một số tự nhiên nhỏ hơn hoặc bằng 0", "Không có số tự nhiên nào lớn hơn 0", "Tồn tại số tự nhiên lớn hơn 0"],
        "correct": "B", "explanation": "Phủ định của $\\forall x \\in \\mathbb{N}, P(x)$ là $\\exists x \\in \\mathbb{N}, \\neg P(x)$."
    },
    {
        "domain_id": "dom_logic", "skill_id": "sk_deduct", "bloom": 2,
        "content": "Nếu trời mưa thì đường ướt. Biết rằng đường không ướt. Kết luận logic nào sau đây là chắc chắn đúng?",
        "options": ["Trời có mưa", "Có thể trời đã mưa", "Không thể xác định được thời tiết", "Trời không mưa"],
        "correct": "D", "explanation": "Quy tắc suy diễn đảo ngược Modus Tollens: $P \\to Q$ và $\\neg Q \\Rightarrow \\neg P$."
    },
    {
        "domain_id": "dom_logic", "skill_id": "sk_order", "bloom": 3,
        "content": "Có 5 bạn An, Bình, Cúc, Dũng, Em xếp hàng. An đứng trước Bình nhưng sau Cúc. Dũng đứng trước Cúc. Em đứng cuối cùng. Người đứng thứ hai là:",
        "options": ["Cúc", "An", "Dũng", "Bình"],
        "correct": "A", "explanation": "Thứ tự từ đầu hàng đến cuối hàng: Dũng (1) $\\to$ Cúc (2) $\\to$ An (3) $\\to$ Bình (4) $\\to$ Em (5)."
    },
    {
        "domain_id": "dom_logic", "skill_id": "sk_data_logic", "bloom": 4,
        "content": "Doanh số của 4 chi nhánh A, B, C, D lần lượt tăng trưởng: 12%, 18%, -5%, 25%. Chi nhánh nào có tốc độ tăng trưởng cao nhất và đóng góp ổn định?",
        "options": ["Chi nhánh B", "Chi nhánh A", "Chi nhánh D", "Chi nhánh C"],
        "correct": "C", "explanation": "Chi nhánh D có mức tăng trưởng 25%, cao nhất trong tất cả các chi nhánh."
    },
    {
        "domain_id": "dom_logic", "skill_id": "sk_eval_arg", "bloom": 5,
        "content": "'Tất cả các thiên nga từng thấy đều màu trắng, do đó mọi con thiên nga trên thế giới đều màu trắng'. Lập luận trên mắc lỗi tư duy gì?",
        "options": ["Lập luận vòng vo (Circular Reasoning)", "Khái quát hóa vội vã (Hasty Generalization)", "Công kích cá nhân (Ad Hominem)", "Ngụy biện bù nhìn (Straw Man)"],
        "correct": "B", "explanation": "Lỗi suy diễn quy nạp chưa đủ cỡ mẫu đại diện dẫn đến kết luận vội vã."
    },
    {
        "domain_id": "dom_logic", "skill_id": "sk_model_logic", "bloom": 6,
        "content": "Cho hệ thống 3 công tắc A, B, C điều khiển đèn S theo logic: $S = (A \\text{ AND } B) \\text{ OR } (\\text{NOT } C)$. Trạng thái nào của (A, B, C) làm đèn tắt?",
        "options": ["(1, 1, 0)", "(0, 0, 0)", "(1, 1, 1)", "(0, 1, 1)"],
        "correct": "D", "explanation": "Khi $A=0, B=1, C=1$: $A \\text{ AND } B = 0$, $\\text{NOT } C = 0 \\Rightarrow S = 0$ (Đèn tắt)."
    },

    # Language Templates
    {
        "domain_id": "dom_lang", "skill_id": "sk_spelling", "bloom": 1,
        "content": "Từ nào sau đây viết ĐÚNG chính tả tiếng Việt?",
        "options": ["Chuẩn đoán", "Trẩn đoán", "Chẩn đoán", "Chẩn đón"],
        "correct": "C", "explanation": "'Chẩn đoán' (nghĩa là xem xét để xác định tình trạng bệnh) là từ viết đúng chính tả."
    },
    {
        "domain_id": "dom_lang", "skill_id": "sk_rhetoric", "bloom": 2,
        "content": "Câu thơ 'Bàn tay ta làm nên tất cả / Có sức người sỏi đá cũng thành cơm' sử dụng biện pháp tu từ nào?",
        "options": ["Hoán dụ", "Ẩn dụ", "So sánh", "Nói quá"],
        "correct": "A", "explanation": "'Bàn tay' là bộ phận cơ thể đại diện cho người lao động, đây là phép hoán dụ lấy bộ phận chỉ toàn thể."
    },
    {
        "domain_id": "dom_lang", "skill_id": "sk_syntax", "bloom": 3,
        "content": "Xác định câu mắc lỗi ngữ pháp cấu tạo:",
        "options": ["Tác phẩm Tắt đèn cho thấy số phận người nông dân.", "Qua tác phẩm Tắt đèn cho thấy số phận người nông dân.", "Qua tác phẩm Tắt đèn, tác giả làm nổi bật số phận người nông dân.", "Người nông dân trong tác phẩm Tắt đèn có số phận cay đắng."],
        "correct": "B", "explanation": "Câu có trạng ngữ 'Qua tác phẩm Tắt đèn' nhưng thiếu chủ ngữ độc lập trước vị ngữ 'cho thấy'."
    },
    {
        "domain_id": "dom_lang", "skill_id": "sk_reading", "bloom": 4,
        "content": "Trong văn bản nghị luận xã hội, thao tác lập luận nào nhằm làm sáng tỏ bản chất của đối tượng bằng cách chia nhỏ các khía cạnh?",
        "options": ["Chứng minh", "Bình luận", "So sánh", "Phân tích"],
        "correct": "D", "explanation": "Phân tích là thao tác chia nhỏ đối tượng ra các bộ phận cấu thành để xem xét toàn diện."
    },
    {
        "domain_id": "dom_lang", "skill_id": "sk_critique", "bloom": 5,
        "content": "Đánh giá về giá trị nhân đạo cốt lõi của tác phẩm 'Vợ nhặt' (Kim Lân), khẳng định nào sau đây là sâu sắc nhất?",
        "options": ["Tố cáo tội ác tày trời của thực dân và phát xít", "Miêu tả hiện thực bi thảm của nạn đói năm 1945", "Phát hiện khát vọng sống và tình thương giữa bờ vực cái chết", "Ca ngợi vẻ đẹp ngoại hình của người lao động nghèo"],
        "correct": "C", "explanation": "Giá trị nhân đạo đỉnh cao là nhìn thấy ánh sáng khát vọng gia đình và phẩm giá con người nơi tăm tối nhất."
    },
    {
        "domain_id": "dom_lang", "skill_id": "sk_synth_lang", "bloom": 6,
        "content": "Hãy xác định phong cách chức năng ngôn ngữ phù hợp nhất để soạn thảo bản thỏa thuận hợp tác nghiên cứu giữa hai trường đại học:",
        "options": ["Phong cách ngôn ngữ hành chính - công vụ", "Phong cách ngôn ngữ khoa học", "Phong cách ngôn ngữ báo chí", "Phong cách ngôn ngữ chính luận"],
        "correct": "A", "explanation": "Văn bản thỏa thuận hợp tác là văn bản pháp quy, thuộc phong cách hành chính - công vụ."
    },

    # Natural Sciences Templates
    {
        "domain_id": "dom_nat_sci", "skill_id": "sk_physics", "bloom": 1,
        "content": "Chu kỳ dao động điều hòa của con lắc lò xo độ cứng $k$, khối lượng $m$ được tính bởi:",
        "options": ["$T = 2\\pi\\sqrt{\\frac{k}{m}}$", "$T = 2\\pi\\sqrt{\\frac{m}{k}}$", "$T = \\frac{1}{2\\pi}\\sqrt{\\frac{m}{k}}$", "$T = 2\\pi\\sqrt{\\frac{l}{g}}$"],
        "correct": "B", "explanation": "Công thức chuẩn chu kỳ con lắc lò xo: $T = 2\\pi\\sqrt{\\frac{m}{k}}$."
    },
    {
        "domain_id": "dom_nat_sci", "skill_id": "sk_chem", "bloom": 2,
        "content": "Chất nào sau đây làm đổi màu quỳ tím ẩm sang màu đỏ?",
        "options": ["Dung dịch NaOH", "Khí NH3", "Dung dịch NaCl", "Khí HCl"],
        "correct": "D", "explanation": "Khí HCl tan vào nước trong quỳ ẩm tạo dung dịch axit clohiđric làm quỳ hóa đỏ."
    },
    {
        "domain_id": "dom_nat_sci", "skill_id": "sk_bio", "bloom": 3,
        "content": "Ở đậu Hà Lan, gen A hạt vàng trội hoàn toàn so với gen a hạt xanh. Phép lai $Aa \\times Aa$ cho tỷ lệ kiểu hình ở $F_1$ là:",
        "options": ["1 hạt vàng : 1 hạt xanh", "3 hạt vàng : 1 hạt xanh", "100% hạt vàng", "1 vàng : 2 đốm : 1 xanh"],
        "correct": "B", "explanation": "Tỷ lệ kiểu gen là $1AA : 2Aa : 1aa$, kiểu hình gồm 3 trội (vàng) : 1 lặn (xanh)."
    },
    {
        "domain_id": "dom_nat_sci", "skill_id": "sk_physics", "bloom": 4,
        "content": "Đoạn mạch $RLC$ nối tiếp đang có hiện tượng cộng hưởng điện. Nếu tăng tần số góc $\\omega$ của dòng điện thì hệ số công suất $\\cos\\varphi$ sẽ:",
        "options": ["Giảm", "Tăng", "Không đổi", "Bằng 0"],
        "correct": "A", "explanation": "Khi cộng hưởng $\\cos\\varphi = 1$ (cực đại). Thay đổi $\\omega$ ra khỏi tần số cộng hưởng thì hệ số công suất chắc chắn giảm."
    },
    {
        "domain_id": "dom_nat_sci", "skill_id": "sk_chem", "bloom": 5,
        "content": "Cho phản ứng: $\\text{N}_2(k) + 3\\text{H}_2(k) \\rightleftharpoons 2\\text{NH}_3(k)$, $\\Delta H < 0$. Để cân bằng chuyển dịch theo chiều thuận, biện pháp tối ưu là:",
        "options": ["Giảm áp suất và tăng nhiệt độ", "Tăng nhiệt độ và giữ nguyên áp suất", "Tăng áp suất và giảm nhiệt độ", "Thêm chất xúc tác mà không đổi áp suất"],
        "correct": "C", "explanation": "Phản ứng tỏa nhiệt (giảm nhiệt độ để chuyển thuận) và giảm số mol khí (tăng áp suất để chuyển thuận)."
    },
    {
        "domain_id": "dom_nat_sci", "skill_id": "sk_bio_tech", "bloom": 6,
        "content": "Thiết kế phương án lai tạo để thu được giống cây trồng tam bội ($3n$) không hạt từ các dòng lưỡng bội ($2n$) ban đầu:",
        "options": ["Nuôi cấy hạt phấn của dòng 2n rồi lưỡng bội hóa", "Chiếu xạ dòng 2n bằng tia phóng xạ gamma", "Dung hợp tế bào trần giữa hai dòng 2n khác loài", "Đa bội hóa dòng 2n thành 4n bằng Colchicine, sau đó lai giữa 4n với 2n"],
        "correct": "D", "explanation": "Cây $4n$ cho giao tử $2n$, lai với cây $2n$ cho giao tử $n \\Rightarrow F_1: 3n$ bất thụ không hạt."
    },

    # Social Sciences Templates
    {
        "domain_id": "dom_soc_sci", "skill_id": "sk_hist", "bloom": 1,
        "content": "Chiến dịch lịch sử nào đã kết thúc thắng lợi cuộc kháng chiến chống thực dân Pháp của nhân dân Việt Nam (1954)?",
        "options": ["Chiến dịch Điện Biên Phủ", "Chiến dịch Việt Bắc", "Chiến dịch Biên giới", "Chiến dịch Hồ Chí Minh"],
        "correct": "A", "explanation": "Chiến thắng Điện Biên Phủ ngày 7/5/1954 đập tan tập đoàn cứ điểm Pháp, buộc Pháp ký Hiệp định Genève."
    },
    {
        "domain_id": "dom_soc_sci", "skill_id": "sk_geo", "bloom": 2,
        "content": "Đặc điểm cơ bản của khí hậu miền Bắc và Đông Bắc Bắc Bộ nước ta là:",
        "options": ["Nhiệt đới gió mùa có mùa đông ấm áp quanh năm", "Mùa đông lạnh, ít mưa; mùa hạ nóng ẩm, mưa nhiều", "Cận xích đạo gió mùa với hai mùa mưa - khô rõ rệt", "Khí hậu ôn đới lục địa khô hạn"],
        "correct": "B", "explanation": "Đặc trưng nổi bật là mùa đông lạnh chịu ảnh hưởng của gió mùa Đông Bắc, hạ nóng ẩm."
    },
    {
        "domain_id": "dom_soc_sci", "skill_id": "sk_hist", "bloom": 3,
        "content": "Điểm tương đồng mang tính quy luật giữa Cách mạng tháng Tám (1945) và Kháng chiến chống Mỹ (1975) là:",
        "options": ["Sử dụng chiến tranh du kích làm nòng cốt từ đầu đến cuối", "Dựa hoàn toàn vào viện trợ quân sự của các nước đồng minh", "Kết hợp chặt chẽ giữa đấu tranh chính trị và đấu tranh vũ trang", "Tổ chức tổng tiến công và nổi dậy đồng loạt tại nông thôn trước tiên"],
        "correct": "C", "explanation": "Bài học kết hợp chặt chẽ hai lực lượng chính trị và vũ trang là nghệ thuật chiến tranh cách mạng Việt Nam."
    },
    {
        "domain_id": "dom_soc_sci", "skill_id": "sk_geo", "bloom": 4,
        "content": "Nguyên nhân chủ yếu khiến vùng Đông Nam Bộ có mật độ tập trung khu công nghiệp cao nhất cả nước là:",
        "options": ["Tài nguyên khoáng sản kim loại và than đá dồi dào nhất", "Lực lượng lao động nông nghiệp đông đảo nhất cả nước", "Địa hình cao nguyên thuận lợi cho việc san lấp mặt bằng", "Hạ tầng phát triển, vị trí địa lý thuận lợi và thu hút mạnh vốn FDI"],
        "correct": "D", "explanation": "Hạ tầng đồng bộ (cảng biển, sân bay, cao tốc), vị trí chiến lược và năng động thu hút FDI là yếu tố then chốt."
    },
    {
        "domain_id": "dom_soc_sci", "skill_id": "sk_hist_eval", "bloom": 5,
        "content": "Đánh giá tác động quốc tế lớn nhất của chiến thắng Điện Biên Phủ (1954):",
        "options": ["Cổ vũ mạnh mẽ phong trào giải phóng dân tộc thuộc địa trên toàn thế giới", "Làm sụp đổ hoàn toàn hệ thống tư bản chủ nghĩa", "Chấm dứt hoàn toàn sự can thiệp của Mỹ vào khu vực Đông Nam Á", "Buộc Liên Hợp Quốc phải giải thể các khối quân sự"],
        "correct": "A", "explanation": "Chiến thắng cổ vũ các dân tộc bị áp bức (đặc biệt là châu Phi như Algérie) vùng lên giành độc lập."
    },
    {
        "domain_id": "dom_soc_sci", "skill_id": "sk_geo_strat", "bloom": 6,
        "content": "Đề xuất giải pháp chiến lược dài hạn nhằm thích ứng biến đổi khí hậu và xâm nhập mặn tại vùng Đồng bằng sông Cửu Long:",
        "options": ["Đắp đê ngăn mặn khép kín toàn bộ dải bờ biển phía Tây và phía Đông", "Chuyển toàn bộ đất trồng lúa sang khai thác khoáng sản", "Chuyển đổi cơ cấu mùa vụ theo mô hình 'thuận thiên' (nước mặn, lợ, ngọt) và quy hoạch hồ trữ nước ngọt", "Bơm nước ngọt ngầm liên tục với công suất tối đa để đẩy mặn"],
        "correct": "C", "explanation": "Nghị quyết 120/NQ-CP chủ trương phát triển thuận thiên, tôn trọng quy luật tự nhiên, thích ứng mặn - ngọt."
    },
]


GEMINI_EXAM_SCHEMA = {
    "type": "ARRAY",
    "items": {
        "type": "OBJECT",
        "properties": {
            "id": {"type": "STRING"},
            "domain_id": {"type": "STRING"},
            "domain_name": {"type": "STRING"},
            "skill_id": {"type": "STRING"},
            "skill_name": {"type": "STRING"},
            "bloom": {"type": "INTEGER"},
            "content": {"type": "STRING"},
            "options": {
                "type": "ARRAY",
                "items": {"type": "STRING"}
            },
            "correct": {"type": "STRING"},
            "explanation": {"type": "STRING"}
        },
        "required": [
            "id", "domain_id", "domain_name", "skill_id", "skill_name",
            "bloom", "content", "options", "correct", "explanation"
        ]
    }
}


def _call_gemini_exam_generator(
    prompt: str,
    domain_id: str,
    target_count: int,
    bloom_level: Optional[int]
) -> list[GeneratedQuestionItem] | None:
    """Call Google Gemini API using structured responseSchema to generate questions."""
    api_key = GOOGLE_API_KEY or os.environ.get("GOOGLE_API_KEY", "")

    model = "gemini-flash-lite-latest"
    url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={api_key}"

    if domain_id != "ALL" and domain_id in DOMAIN_CATALOG:
        target_domain_name = DOMAIN_CATALOG[domain_id]["name"]
        domain_spec = (
            f"BẮT BUỘC 100% TẤT CẢ CÁC CÂU HỎI PHẢI THUỘC LĨNH VỰC: '{target_domain_name}'. "
            f"TUYỆT ĐỐI KHÔNG ĐƯỢC sinh bất kỳ câu hỏi nào thuộc môn khác (Ví dụ: Nếu yêu cầu Ngữ văn/Văn học thì 100% {target_count} câu hỏi phải là Ngữ văn, Tiếng Việt, Đọc hiểu; CẤM TUYỆT ĐỐI đưa câu hỏi Toán, Logic, Lý, Hóa, Sử, Địa vào). "
            f"Tất cả các câu đều phải có domain_id là '{domain_id}'."
        )
    else:
        domain_spec = "Bao quát toàn diện 5 lĩnh vực ĐGNL: Toán học & Phân tích số liệu, Tư duy Logic & Suy luận, Ngôn ngữ & Văn học, KHTN (Lý - Hóa - Sinh), KHXH (Sử - Địa)."

    bloom_spec = (
        f"Mức độ Bloom Taxonomy yêu cầu: Cấp độ {bloom_level}/6."
        if bloom_level and 1 <= bloom_level <= 6
        else "Phân bổ độ khó trải đều theo 6 cấp độ Bloom Taxonomy: Mức 1 (Nhận biết) đến Mức 6 (Sáng tạo)."
    )

    sys_prompt = f"""BẠN LÀ CHUYÊN GIA KHẢO THÍ VÀ BIÊN SOẠN ĐỀ THI ĐÁNH GIÁ NĂNG LỰC V-ACT CHUẨN HÓA.
Nhiệm vụ: Tạo đúng {target_count} câu hỏi trắc nghiệm tiếng Việt chất lượng cao theo yêu cầu sau:
- Định hướng Prompt của giáo viên: "{prompt}" (ƯU TIÊN TUYỆT ĐỐI)
- {domain_spec}
- {bloom_spec}

QUY CHUẨN BIÊN SOẠN BẮT BUỘC:
1. ĐÚNG 100% CHỦ ĐỀ & MÔN HỌC ĐƯỢC YÊU CẦU:
   - Nếu giáo viên yêu cầu Ngữ văn / Tiếng Việt: 100% {target_count} câu hỏi phải là các dạng bài: chính tả tiếng Việt, từ vựng, ngữ pháp, phong cách ngôn ngữ, biện pháp tu từ (so sánh, nhân hóa, ẩn dụ, hoán dụ, điệp từ...), đọc hiểu đoạn thơ/văn xuôi trích dẫn từ tác phẩm văn học nổi tiếng, phân tích văn học và nghị luận xã hội. CẤM TUYỆT ĐỐI chèn câu hỏi Toán, Lý, Hóa, Logic!
   - Nếu giáo viên yêu cầu Toán học: 100% là Toán học & Giải tích/Hình học/Xác suất.
   - Nếu giáo viên yêu cầu Logic: 100% là Tư duy suy luận và logic hình thức.
2. CHÍNH XÁC, NHẤT QUÁN & SƯ PHẠM:
   - Mỗi câu hỏi phải có đúng 1 phương án đúng ('correct') duy nhất trong 4 phương án ['A', 'B', 'C', 'D'].
   - 3 phương án còn lại là phương án nhiễu sai hợp lý.
   - Lời giải ('explanation') phải giải thích rõ ràng, mạch lạc, trực tiếp chỉ ra vì sao đáp án đó đúng. TUYỆT ĐỐI KHÔNG xin lỗi, KHÔNG nói 'giả sử đề bài sửa thành...', KHÔNG tự nhận sai sót trong lời giải.
3. KÝ HIỆU & CÔNG THỨC: Nếu có công thức toán/lý/hóa (nếu môn tự nhiên), BẮT BUỘC bọc trong dấu dollar: $...$.
4. MỖI CÂU HỎI gồm đúng 4 phương án trong mảng 'options'.
5. PHÂN BỔ ĐÁP ÁN ĐÚNG CÂN BẰNG: Phân bổ đáp án đúng 'correct' rải đều giữa A, B, C, D (khoảng 25% mỗi chữ cái), không dồn toàn bộ vào A hay B.
6. 'bloom' là số nguyên từ 1 đến 6. Phân bổ theo thang Bloom đã yêu cầu."""

    payload = {
        "contents": [{"parts": [{"text": sys_prompt}]}],
        "generationConfig": {
            "temperature": 0.7,
            "responseMimeType": "application/json",
            "responseSchema": GEMINI_EXAM_SCHEMA
        }
    }

    try:
        resp = requests.post(url, json=payload, timeout=40)
        if resp.status_code != 200:
            logger.warning("Gemini API returned status %s: %s", resp.status_code, resp.text[:200])
            return None

        result_data = resp.json()
        raw_text = result_data["candidates"][0]["content"]["parts"][0]["text"]
        items = json.loads(raw_text)

        out_questions: list[GeneratedQuestionItem] = []
        for idx, it in enumerate(items[:target_count]):
            q_id = f"q{idx + 1:02d}"
            opts = it.get("options", [])
            while len(opts) < 4:
                opts.append(f"Phương án {chr(65 + len(opts))}")
            opts = opts[:4]

            correct_char = str(it.get("correct", "A")).strip().upper()
            if correct_char not in ["A", "B", "C", "D"]:
                correct_char = ["A", "B", "C", "D"][idx % 4]

            b_val = int(it.get("bloom", 2))
            b_val = min(max(b_val, 1), 6)

            dom_id = it.get("domain_id") or domain_id if domain_id != "ALL" else "dom_math"
            dom_name = DOMAIN_CATALOG.get(dom_id, {}).get("name", "Toán học & Phân tích số liệu")
            sk_id = it.get("skill_id") or "sk_general"
            sk_name = it.get("skill_name") or "Kỹ năng chuyên môn"

            out_questions.append(
                GeneratedQuestionItem(
                    id=q_id,
                    domain_id=dom_id,
                    domain_name=dom_name,
                    skill_id=sk_id,
                    skill_name=sk_name,
                    bloom=b_val,
                    content=it.get("content", f"Câu hỏi {q_id}"),
                    options=opts,
                    correct=correct_char,
                    explanation=it.get("explanation", ""),
                )
            )
        return out_questions
    except Exception as exc:
        logger.warning("Gemini call failed with exception: %s", exc)
        return None


@router.post(
    "/generate-exam",
    response_model=GenerateExamResponse,
    summary="Generate tailored exam questions based on prompt and Bloom difficulty",
    description="Generates dynamic diagnostic exam questions with balanced Bloom levels, diversified keys, and LaTeX math.",
)
async def generate_exam(payload: GenerateExamRequest) -> GenerateExamResponse:
    """Generate dynamic exam questions using Gemini LLM Cloud or calibrated psychometric bank."""
    start_time = time.time()
    prompt_used = payload.prompt or "Đề thi khảo sát năng lực chuẩn hóa V-ACT 5 lĩnh vực"
    domain_id = payload.domain_id or "ALL"
    target_count = min(max(payload.question_count, 5), 50)
    bloom_level = payload.bloom_level
    generator_mode = (payload.generator_mode or "gemini").lower()

    # Intelligent Domain Detection from Teacher Prompt:
    # If teacher explicitly selected a specific domain from dropdown (e.g. dom_soc_sci), ALWAYS respect it!
    # ONLY infer domain from prompt if domain_id is "ALL":
    if domain_id == "ALL":
        p_lower = prompt_used.lower()
        if any(k in p_lower for k in [
            "full văn", "full van", "chuyên sâu văn", "chuyen sau van", "chuyên sâu ngữ văn", "môn văn", "mon van", "môn ngữ văn"
        ]):
            domain_id = "dom_lang"
        elif any(k in p_lower for k in [
            "full toán", "full toan", "chuyên sâu toán", "chuyen sau toan", "môn toán", "mon toan", "giải tích", "hình học không gian"
        ]):
            domain_id = "dom_math"
        elif any(k in p_lower for k in [
            "full logic", "chuyên sâu logic", "chuyen sau logic", "môn logic", "tư duy suy luận"
        ]):
            domain_id = "dom_logic"
        elif any(k in p_lower for k in [
            "full khtn", "chuyên sâu khtn", "chuyen sau khtn", "khoa học tự nhiên"
        ]):
            domain_id = "dom_nat_sci"
        elif any(k in p_lower for k in [
            "full khxh", "chuyên sâu khxh", "chuyen sau khxh", "khoa học xã hội", "địa lý", "dia ly", "địa lí", "dia li", "lịch sử", "lich su"
        ]):
            domain_id = "dom_soc_sci"

    questions_out: list[GeneratedQuestionItem] = []
    engine_used = "gemini_cloud"

    # 1. Attempt Gemini Cloud generation if requested
    if generator_mode == "gemini":
        gemini_result = _call_gemini_exam_generator(
            prompt=prompt_used,
            domain_id=domain_id,
            target_count=target_count,
            bloom_level=bloom_level,
        )
        if gemini_result and len(gemini_result) > 0:
            questions_out = gemini_result
            logger.info("Successfully generated %d questions via Gemini Cloud API", len(questions_out))
        else:
            logger.warning("Gemini generation failed or timed out. Falling back to calibrated question bank.")
            engine_used = "fast_calibrated"
    else:
        engine_used = "fast_calibrated"

    # 2. If questions not populated (fast mode or Gemini fallback), use Calibrated Bank
    if not questions_out:
        pool = QUESTION_TEMPLATES[:]
        if domain_id != "ALL" and domain_id in DOMAIN_CATALOG:
            pool = [q for q in pool if q["domain_id"] == domain_id]
            if not pool:
                pool = QUESTION_TEMPLATES[:]

        if bloom_level and 1 <= bloom_level <= 6:
            bloom_pool = [q for q in pool if q["bloom"] == bloom_level]
            if bloom_pool:
                pool = bloom_pool

        for idx in range(target_count):
            base_item = pool[idx % len(pool)]
            q_id = f"q{idx + 1:02d}"

            dom_meta = DOMAIN_CATALOG.get(base_item["domain_id"], {})
            domain_name = dom_meta.get("name", "Toán học & Phân tích số liệu")
            skill_name = "Kỹ năng chuyên môn"
            for s_id, s_name in dom_meta.get("skills", []):
                if s_id == base_item["skill_id"]:
                    skill_name = s_name
                    break

            questions_out.append(
                GeneratedQuestionItem(
                    id=q_id,
                    domain_id=base_item["domain_id"],
                    domain_name=domain_name,
                    skill_id=base_item["skill_id"],
                    skill_name=skill_name,
                    bloom=base_item["bloom"],
                    content=base_item["content"],
                    options=base_item["options"],
                    correct=base_item["correct"],
                    explanation=base_item.get("explanation", ""),
                )
            )

    # 3. Calculate distributions
    option_counts = {"A": 0, "B": 0, "C": 0, "D": 0}
    bloom_counts = {str(i): 0 for i in range(1, 7)}
    for q in questions_out:
        c_key = q.correct.upper()
        option_counts[c_key] = option_counts.get(c_key, 0) + 1
        b_str = str(q.bloom)
        bloom_counts[b_str] = bloom_counts.get(b_str, 0) + 1

    elapsed_ms = int((time.time() - start_time) * 1000)

    title_prefix = "🧠 Gemini AI Sinh Mới" if engine_used == "gemini_cloud" else "⚡ Đề Chuẩn Hóa Siêu Tốc"
    title = f"{title_prefix} ({len(questions_out)} câu) • {DOMAIN_CATALOG.get(domain_id, {}).get('name', 'Toàn Diện 5 Lĩnh Vực')}"

    return GenerateExamResponse(
        title=title,
        prompt_used=prompt_used,
        total_questions=len(questions_out),
        questions=questions_out,
        bloom_distribution=bloom_counts,
        option_distribution=option_counts,
        engine_used=engine_used,
        generation_time_ms=elapsed_ms,
    )

