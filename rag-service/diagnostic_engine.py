"""
diagnostic_engine.py — IRT & BKT Diagnostic Analysis Engine
=============================================================
Core mathematical module for V-Eval Core Flow 1 (Step 4):

1. Estimate student ability theta_0 using Item Response Theory (IRT 2PL model)
   via Maximum Likelihood Estimation (MLE) with scipy.
2. Calculate Bayesian Knowledge Tracing (BKT) prior P(L0) per skill
   using Logistic Sigmoid with safe boundary clamping [0.05, 0.95].
3. Handle unhappy cases: skills with no questions fall back to parent
   domain-level theta.
4. Build radar chart coordinates normalized to the student's target score.
"""

from __future__ import annotations

import logging
import math
from dataclasses import dataclass, field

try:
    from scipy.optimize import minimize_scalar  # type: ignore
except ImportError:  # pragma: no cover
    minimize_scalar = None

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Constants: Difficulty Level → IRT b-parameter mapping
# ---------------------------------------------------------------------------

# Maps Content Service difficulty_level (1–6 Bloom's Taxonomy) to IRT difficulty (b) on the
# theta scale [-3.0, +3.0].
DIFFICULTY_TO_B: dict[int, float] = {
    1: -1.8,   # Mức 1: Nhận biết (Remembering)
    2: -1.0,   # Mức 2: Thông hiểu (Understanding)
    3: -0.2,   # Mức 3: Vận dụng (Applying)
    4:  0.6,   # Mức 4: Phân tích (Analyzing)
    5:  1.4,   # Mức 5: Đánh giá (Evaluating)
    6:  2.2,   # Mức 6: Sáng tạo (Creating)
}

# Default IRT discrimination parameter (a) — uniform across items for the
# diagnostic test to keep the model simple and robust.
DEFAULT_DISCRIMINATION: float = 1.2

# Guessing parameter (c) for 4-choice multiple choice questions.
GUESSING_PARAM: float = 0.25

# If a student answered in fewer than this many seconds, the response is
# considered a random guess and penalized by setting c = 1.0 (pure chance).
RAPID_GUESS_THRESHOLD_SECONDS: int = 5

# Theta search boundaries
THETA_MIN: float = -3.0
THETA_MAX: float = 3.0

# BKT prior clamping boundaries — prevents BKT from getting stuck at 0 or 1
BKT_CLAMP_MIN: float = 0.05
BKT_CLAMP_MAX: float = 0.95

# V-ACT exam maximum possible score
VACT_MAX_SCORE: int = 1200

# Placement class thresholds
PLACEMENT_THRESHOLD_FOUNDATION: float = -0.5
PLACEMENT_THRESHOLD_BREAKTHROUGH: float = 0.5


# ---------------------------------------------------------------------------
# Data Classes
# ---------------------------------------------------------------------------


@dataclass
class AnswerItem:
    """A single student answer for the diagnostic test."""

    question_id: str
    skill_id: str
    domain_id: str
    difficulty_level: int
    is_correct: bool
    time_spent_seconds: int


@dataclass
class SkillPrior:
    """BKT prior probability P(L0) for one skill."""

    skill_id: str
    domain_id: str
    theta_skill: float
    p_l0: float
    source: str  # "measured" or "inferred_from_domain"


@dataclass
class DomainScore:
    """Aggregated score for one competency domain."""

    domain_id: str
    domain_name: str
    theta_domain: float
    total_questions: int
    correct_count: int
    accuracy_pct: float


@dataclass
class RadarAxis:
    """One axis of the radar chart."""

    domain_id: str
    domain_name: str
    student_pct: float      # Student's actual percentage [0–100]
    benchmark_pct: float    # Target benchmark percentage [0–100]


@dataclass
class DiagnosticResult:
    """Complete output of the diagnostic analysis engine."""

    theta_0: float
    placement_class: str    # "FOUNDATION", "ACCELERATION", "BREAKTHROUGH"
    domain_scores: list[DomainScore] = field(default_factory=list)
    skill_priors: list[SkillPrior] = field(default_factory=list)
    radar_chart: list[RadarAxis] = field(default_factory=list)


# ---------------------------------------------------------------------------
# IRT 2PL Probability Function
# ---------------------------------------------------------------------------


def _irt_probability(theta: float, b: float, a: float = DEFAULT_DISCRIMINATION,
                     c: float = GUESSING_PARAM) -> float:
    """Compute the IRT 2PL probability of a correct response.

    P(X=1 | theta) = c + (1-c) / (1 + exp(-a * (theta - b)))

    Parameters
    ----------
    theta : float
        Student ability parameter.
    b : float
        Item difficulty parameter.
    a : float
        Item discrimination parameter.
    c : float
        Pseudo-guessing parameter (0.25 for 4-choice MCQ).

    Returns
    -------
    float
        Probability of a correct answer, in range [c, 1.0].
    """
    exponent = -a * (theta - b)
    # Clamp exponent to prevent overflow in exp()
    exponent = max(-500.0, min(500.0, exponent))
    return c + (1.0 - c) / (1.0 + math.exp(exponent))


# ---------------------------------------------------------------------------
# Maximum Likelihood Estimation for theta
# ---------------------------------------------------------------------------


def _neg_log_likelihood(theta: float, answers: list[AnswerItem]) -> float:
    """Compute the negative log-posterior (MAP loss) for a given candidate theta.

    Minimizing this yields the MAP estimate of theta_0, incorporating a weakly
    informative Gaussian prior N(0, 2.0^2) to regularize extreme scores and
    prevent numerical instability.

    Parameters
    ----------
    theta : float
        Candidate ability value.
    answers : list[AnswerItem]
        The student's responses with item metadata.

    Returns
    -------
    float
        Negative log-posterior (to be minimized).
    """
    nll = 0.0
    for ans in answers:
        b = DIFFICULTY_TO_B.get(ans.difficulty_level, 0.0)

        # Penalize rapid guesses: if the student answered too quickly (< 5s),
        # heavily discount item discrimination (a -> 0.1), treating the response
        # as carrying virtually no ability information.
        a = 0.1 if ans.time_spent_seconds < RAPID_GUESS_THRESHOLD_SECONDS else DEFAULT_DISCRIMINATION

        p = _irt_probability(theta, b, a=a, c=GUESSING_PARAM)

        # Clamp p to avoid log(0)
        p = max(1e-10, min(1.0 - 1e-10, p))

        if ans.is_correct:
            nll -= math.log(p)
        else:
            nll -= math.log(1.0 - p)

    # MAP Gaussian prior N(0, 2.0^2)
    prior_penalty = 0.5 * (theta / 2.0) ** 2
    return nll + prior_penalty


def _golden_section_search(f, a: float, b: float, tol: float = 1e-4) -> tuple[float, float]:
    """Find local minimum of 1D unimodal function f on [a, b] using Golden Section Search."""
    invphi = (math.sqrt(5.0) - 1.0) / 2.0
    invphi2 = (3.0 - math.sqrt(5.0)) / 2.0

    h = b - a
    if h <= tol:
        mid = (a + b) / 2.0
        return mid, f(mid)

    c = a + invphi2 * h
    d = a + invphi * h
    yc = f(c)
    yd = f(d)

    while h > tol:
        if yc < yd:
            b = d
            d = c
            yd = yc
            h = invphi * h
            c = a + invphi2 * h
            yc = f(c)
        else:
            a = c
            c = d
            yc = yd
            h = invphi * h
            d = a + invphi * h
            yd = f(d)

    if yc < yd:
        return c, yc
    return d, yd


def estimate_theta(answers: list[AnswerItem]) -> float:
    """Estimate overall student ability theta_0 via MLE.

    Uses Brent's method (scalar optimization) via scipy if installed,
    or falls back to a pure Python Golden Section Search on [THETA_MIN, THETA_MAX].

    Parameters
    ----------
    answers : list[AnswerItem]
        Student's diagnostic test responses.

    Returns
    -------
    float
        Estimated theta_0, clamped to [THETA_MIN, THETA_MAX].
    """
    if not answers:
        logger.warning("No answers provided for theta estimation, returning 0.0")
        return 0.0

    if minimize_scalar is not None:
        result = minimize_scalar(
            _neg_log_likelihood,
            bounds=(THETA_MIN, THETA_MAX),
            method="bounded",
            args=(answers,),
        )
        theta_0 = float(result.x)
        converged = bool(result.success)
        nll = float(result.fun)
    else:
        theta_0, nll = _golden_section_search(
            lambda th: _neg_log_likelihood(th, answers),
            THETA_MIN,
            THETA_MAX,
        )
        converged = True

    theta_0 = max(THETA_MIN, min(THETA_MAX, theta_0))

    logger.info("MLE theta_0 = %.4f (converged=%s, nll=%.4f)",
                theta_0, converged, nll)
    return round(theta_0, 4)


# ---------------------------------------------------------------------------
# Domain-Level Theta Estimation
# ---------------------------------------------------------------------------


def estimate_domain_thetas(
    answers: list[AnswerItem],
    domain_names: dict[str, str],
) -> list[DomainScore]:
    """Estimate theta per competency domain.

    Groups answers by domain_id and runs MLE independently on each group.
    If a domain has too few items (< 3), falls back to the overall theta.

    Parameters
    ----------
    answers : list[AnswerItem]
        All diagnostic answers.
    domain_names : dict[str, str]
        Mapping domain_id → display name.

    Returns
    -------
    list[DomainScore]
        Per-domain ability estimates.
    """
    from collections import defaultdict

    groups: dict[str, list[AnswerItem]] = defaultdict(list)
    for ans in answers:
        groups[ans.domain_id].append(ans)

    overall_theta = estimate_theta(answers)
    domain_scores: list[DomainScore] = []

    for domain_id, domain_answers in groups.items():
        correct = sum(1 for a in domain_answers if a.is_correct)
        total = len(domain_answers)
        accuracy = round((correct / total) * 100, 2) if total > 0 else 0.0

        # Need at least 3 items for a meaningful per-domain MLE
        if total >= 3:
            theta_d = estimate_theta(domain_answers)
        else:
            theta_d = overall_theta
            logger.info(
                "Domain %s has only %d items, using overall theta %.4f",
                domain_id, total, overall_theta,
            )

        domain_scores.append(DomainScore(
            domain_id=domain_id,
            domain_name=domain_names.get(domain_id, domain_id),
            theta_domain=theta_d,
            total_questions=total,
            correct_count=correct,
            accuracy_pct=accuracy,
        ))

    return domain_scores


# ---------------------------------------------------------------------------
# BKT Prior P(L0) Calculation
# ---------------------------------------------------------------------------


def _sigmoid(x: float) -> float:
    """Logistic sigmoid function with overflow protection."""
    x = max(-500.0, min(500.0, x))
    return 1.0 / (1.0 + math.exp(-x))


def _clamp_bkt(p: float) -> float:
    """Clamp BKT prior to safe boundaries [0.05, 0.95]."""
    return max(BKT_CLAMP_MIN, min(BKT_CLAMP_MAX, p))


def calculate_skill_priors(
    answers: list[AnswerItem],
    domain_scores: list[DomainScore],
    all_skill_ids: dict[str, str] | None = None,
) -> list[SkillPrior]:
    """Calculate BKT prior P(L0) for every skill.

    For skills that had questions in the diagnostic test, we compute a
    skill-level theta from the student's responses on those items, then
    apply the Logistic Sigmoid.

    For skills that had NO questions (unhappy case), we fall back to the
    parent domain theta to avoid leaving P(L0) = 0.

    Parameters
    ----------
    answers : list[AnswerItem]
        Diagnostic test answers.
    domain_scores : list[DomainScore]
        Per-domain theta values (from estimate_domain_thetas).
    all_skill_ids : dict[str, str] | None
        Optional mapping skill_id → domain_id for skills NOT covered by the
        test. If provided, these will receive inferred P(L0) values.

    Returns
    -------
    list[SkillPrior]
        P(L0) for every skill, both measured and inferred.
    """
    from collections import defaultdict

    # Build domain theta lookup
    domain_theta_map: dict[str, float] = {
        ds.domain_id: ds.theta_domain for ds in domain_scores
    }

    # Group answers by skill
    skill_groups: dict[str, list[AnswerItem]] = defaultdict(list)
    skill_domain: dict[str, str] = {}
    for ans in answers:
        skill_groups[ans.skill_id].append(ans)
        skill_domain[ans.skill_id] = ans.domain_id

    priors: list[SkillPrior] = []

    # Measured skills (had questions in the diagnostic test)
    for skill_id, skill_answers in skill_groups.items():
        correct = sum(1 for a in skill_answers if a.is_correct)
        total = len(skill_answers)

        if total >= 2:
            # Enough items for per-skill MLE
            theta_s = estimate_theta(skill_answers)
        else:
            # Single item: use a simple heuristic
            # correct → slightly above domain theta, wrong → slightly below
            d_theta = domain_theta_map.get(skill_answers[0].domain_id, 0.0)
            theta_s = d_theta + (0.3 if correct > 0 else -0.3)

        p_l0 = _clamp_bkt(_sigmoid(theta_s))
        priors.append(SkillPrior(
            skill_id=skill_id,
            domain_id=skill_domain[skill_id],
            theta_skill=round(theta_s, 4),
            p_l0=round(p_l0, 4),
            source="measured",
        ))

    # Inferred skills (no questions in test — unhappy case handling)
    if all_skill_ids:
        measured_ids = set(skill_groups.keys())
        for skill_id, domain_id in all_skill_ids.items():
            if skill_id not in measured_ids:
                theta_d = domain_theta_map.get(domain_id, 0.0)
                p_l0 = _clamp_bkt(_sigmoid(theta_d))
                priors.append(SkillPrior(
                    skill_id=skill_id,
                    domain_id=domain_id,
                    theta_skill=round(theta_d, 4),
                    p_l0=round(p_l0, 4),
                    source="inferred_from_domain",
                ))

    return priors


# ---------------------------------------------------------------------------
# Radar Chart Coordinates
# ---------------------------------------------------------------------------


def build_radar_chart(
    domain_scores: list[DomainScore],
    target_score: int = 800,
) -> list[RadarAxis]:
    """Build radar chart data with student vs benchmark comparison.

    Each axis represents a competency domain. The student's percentage is
    their accuracy on that domain. The benchmark is derived from the
    target_score as a proportion of VACT_MAX_SCORE.

    Parameters
    ----------
    domain_scores : list[DomainScore]
        Per-domain ability estimates.
    target_score : int
        Student's target score (0–1200).

    Returns
    -------
    list[RadarAxis]
        Ordered list of radar chart axes.
    """
    benchmark_pct = round((target_score / VACT_MAX_SCORE) * 100, 2)

    axes: list[RadarAxis] = []
    for ds in domain_scores:
        axes.append(RadarAxis(
            domain_id=ds.domain_id,
            domain_name=ds.domain_name,
            student_pct=ds.accuracy_pct,
            benchmark_pct=benchmark_pct,
        ))

    return axes


# ---------------------------------------------------------------------------
# Placement Classification
# ---------------------------------------------------------------------------


def classify_placement(theta_0: float) -> str:
    """Determine the recommended class level based on theta_0.

    Parameters
    ----------
    theta_0 : float
        Overall ability estimate.

    Returns
    -------
    str
        One of: "FOUNDATION", "ACCELERATION", "BREAKTHROUGH".
    """
    if theta_0 < PLACEMENT_THRESHOLD_FOUNDATION:
        return "FOUNDATION"
    elif theta_0 > PLACEMENT_THRESHOLD_BREAKTHROUGH:
        return "BREAKTHROUGH"
    else:
        return "ACCELERATION"


# ---------------------------------------------------------------------------
# Full Diagnostic Pipeline
# ---------------------------------------------------------------------------


def run_diagnostic_analysis(
    answers: list[AnswerItem],
    domain_names: dict[str, str],
    target_score: int = 800,
    all_skill_ids: dict[str, str] | None = None,
) -> DiagnosticResult:
    """Execute the complete diagnostic analysis pipeline.

    This is the main entry point called by the API router.

    Parameters
    ----------
    answers : list[AnswerItem]
        Student's 30 diagnostic test answers.
    domain_names : dict[str, str]
        Mapping domain_id → display name.
    target_score : int
        Student's target score (0–1200, default 800).
    all_skill_ids : dict[str, str] | None
        Optional full skill→domain mapping for inferring missing priors.

    Returns
    -------
    DiagnosticResult
        Complete analysis including theta_0, domain scores, BKT priors,
        radar chart, and placement recommendation.
    """
    logger.info(
        "Running diagnostic analysis: %d answers, target_score=%d",
        len(answers), target_score,
    )

    # Step 1: Overall theta_0
    theta_0 = estimate_theta(answers)

    # Step 2: Per-domain theta
    domain_scores = estimate_domain_thetas(answers, domain_names)

    # Step 3: BKT priors P(L0) per skill
    skill_priors = calculate_skill_priors(answers, domain_scores, all_skill_ids)

    # Step 4: Radar chart
    radar_chart = build_radar_chart(domain_scores, target_score)

    # Step 5: Placement classification
    placement_class = classify_placement(theta_0)

    logger.info(
        "Diagnostic complete: theta_0=%.4f, placement=%s, domains=%d, priors=%d",
        theta_0, placement_class, len(domain_scores), len(skill_priors),
    )

    return DiagnosticResult(
        theta_0=theta_0,
        placement_class=placement_class,
        domain_scores=domain_scores,
        skill_priors=skill_priors,
        radar_chart=radar_chart,
    )
