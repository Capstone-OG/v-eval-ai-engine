"""
tests/test_diagnostic.py — Unit and Integration Tests for Diagnostic Engine
=============================================================================
Tests IRT 2PL theta estimation, BKT prior calculations, domain aggregation,
radar chart coordinate generation, placement logic, and API endpoints.
"""

from __future__ import annotations

from diagnostic_engine import (
    AnswerItem,
    _clamp_bkt,
    _irt_probability,
    _sigmoid,
    build_radar_chart,
    calculate_skill_priors,
    classify_placement,
    estimate_domain_thetas,
    estimate_theta,
    run_diagnostic_analysis,
)


def test_irt_prob_correct():
    """Verify 2PL probability calculation with guessing."""
    # When ability equals difficulty: P = c + (1-c)/(1+1) = 0.25 + 0.75 * 0.5 = 0.625
    p = _irt_probability(theta=0.0, a=1.2, b=0.0, c=0.25)
    assert abs(p - 0.625) < 1e-4

    # High ability should approach 1.0
    p_high = _irt_probability(theta=3.0, a=1.2, b=-1.2, c=0.25)
    assert p_high > 0.98

    # Low ability should approach guessing param c=0.25
    p_low = _irt_probability(theta=-3.0, a=1.2, b=1.6, c=0.25)
    assert abs(p_low - 0.25) < 0.05


def test_bkt_prior_sigmoid_clamping():
    """Verify BKT prior clamping between [0.05, 0.95]."""
    # Extreme high ability
    assert _clamp_bkt(_sigmoid(10.0)) == 0.95

    # Extreme low ability
    assert _clamp_bkt(_sigmoid(-10.0)) == 0.05

    # Neutral ability theta = 0.0 -> Sigmoid(0) = 0.5
    assert abs(_clamp_bkt(_sigmoid(0.0)) - 0.5) < 1e-4


def test_estimate_theta_all_correct():
    """All correct answers should estimate theta near upper bound (> 2.0)."""
    answers = [
        AnswerItem(
            question_id=f"q_{i}",
            skill_id=f"skill_{i % 5}",
            domain_id="domain_1",
            difficulty_level=(i % 4) + 1,
            is_correct=True,
            time_spent_seconds=60,
        )
        for i in range(30)
    ]
    theta = estimate_theta(answers)
    assert theta > 2.0


def test_estimate_theta_all_incorrect():
    """All incorrect answers should estimate theta near lower bound (< -2.0)."""
    answers = [
        AnswerItem(
            question_id=f"q_{i}",
            skill_id=f"skill_{i % 5}",
            domain_id="domain_1",
            difficulty_level=(i % 4) + 1,
            is_correct=False,
            time_spent_seconds=60,
        )
        for i in range(30)
    ]
    theta = estimate_theta(answers)
    assert theta < -2.0


def test_rapid_guess_penalty():
    """Fast responses (< 5s) should be treated as pure guesses."""
    # Student answered correctly but in 2 seconds (guessed)
    answers_fast = [
        AnswerItem(
            question_id=f"q_{i}",
            skill_id="skill_1",
            domain_id="domain_1",
            difficulty_level=2,
            is_correct=True,
            time_spent_seconds=2,
        )
        for i in range(10)
    ]
    theta_fast = estimate_theta(answers_fast)

    # Same student answered in 60s
    answers_normal = [
        AnswerItem(
            question_id=f"q_{i}",
            skill_id="skill_1",
            domain_id="domain_1",
            difficulty_level=2,
            is_correct=True,
            time_spent_seconds=60,
        )
        for i in range(10)
    ]
    theta_normal = estimate_theta(answers_normal)

    assert theta_normal > theta_fast


def test_placement_classification():
    """Verify placement classification tiers."""
    assert classify_placement(-1.2) == "FOUNDATION"
    assert classify_placement(-0.51) == "FOUNDATION"
    assert classify_placement(-0.5) == "ACCELERATION"
    assert classify_placement(0.0) == "ACCELERATION"
    assert classify_placement(0.5) == "ACCELERATION"
    assert classify_placement(0.51) == "BREAKTHROUGH"
    assert classify_placement(2.1) == "BREAKTHROUGH"


def test_missing_skills_inference():
    """Verify untested skills inherit prior from parent domain."""
    answers = [
        AnswerItem(
            question_id="q1",
            skill_id="tested_skill_1",
            domain_id="dom_math",
            difficulty_level=2,
            is_correct=True,
            time_spent_seconds=45,
        ),
        AnswerItem(
            question_id="q2",
            skill_id="tested_skill_1",
            domain_id="dom_math",
            difficulty_level=3,
            is_correct=True,
            time_spent_seconds=50,
        ),
    ]
    domain_scores = estimate_domain_thetas(answers, {"dom_math": "Toán"})
    all_skill_ids = {
        "tested_skill_1": "dom_math",
        "untested_skill_2": "dom_math",
    }
    priors = calculate_skill_priors(answers, domain_scores, all_skill_ids)

    prior_dict = {p.skill_id: p for p in priors}
    assert "tested_skill_1" in prior_dict
    assert prior_dict["tested_skill_1"].source == "measured"

    assert "untested_skill_2" in prior_dict
    assert prior_dict["untested_skill_2"].source == "inferred_from_domain"
    # Inferred prior should equal domain's sigmoid
    math_domain = next(d for d in domain_scores if d.domain_id == "dom_math")
    expected_p = _clamp_bkt(_sigmoid(math_domain.theta_domain))
    assert abs(prior_dict["untested_skill_2"].p_l0 - expected_p) < 1e-4


def test_run_diagnostic_analysis_end_to_end():
    """Verify full end-to-end pipeline."""
    sample_answers = []
    domains = ["dom_math", "dom_lang", "dom_science"]
    for i in range(30):
        d_id = domains[i % 3]
        sample_answers.append(
            AnswerItem(
                question_id=f"q_{i}",
                skill_id=f"skill_{i % 6}",
                domain_id=d_id,
                difficulty_level=(i % 4) + 1,
                is_correct=(i % 2 == 0),  # 50% correct
                time_spent_seconds=40,
            )
        )

    domain_names = {
        "dom_math": "Toán học",
        "dom_lang": "Ngôn ngữ",
        "dom_science": "Khoa học tự nhiên",
    }

    result = run_diagnostic_analysis(
        answers=sample_answers,
        domain_names=domain_names,
        target_score=900,
    )

    assert -3.0 <= result.theta_0 <= 3.0
    assert result.placement_class in ["FOUNDATION", "ACCELERATION", "BREAKTHROUGH"]
    assert len(result.domain_scores) == 3
    assert len(result.radar_chart) == 3
    for axis in result.radar_chart:
        assert axis.benchmark_pct == 75.0  # 900 / 1200 * 100 = 75%


def test_api_diagnostic_config():
    """Verify GET /api/v1/diagnostic/config returns valid parameters."""
    from fastapi.testclient import TestClient
    from main import app

    client = TestClient(app, raise_server_exceptions=False)
    resp = client.get("/api/v1/diagnostic/config")
    assert resp.status_code == 200
    data = resp.json()
    assert data["max_score"] == 1200
    assert "FOUNDATION" in data["placement_thresholds"]["classes"]
    assert data["placement_thresholds"]["foundation_max_theta"] == -0.5
    assert data["placement_thresholds"]["breakthrough_min_theta"] == 0.5


def test_api_diagnostic_analyze_endpoint():
    """Verify POST /api/v1/diagnostic/analyze end-to-end HTTP request."""
    from fastapi.testclient import TestClient
    from main import app

    client = TestClient(app, raise_server_exceptions=False)
    payload = {
        "student_id": "std_12345",
        "submission_id": "sub_67890",
        "target_score": 850,
        "domain_names": [
            {"domain_id": "dom_1", "domain_name": "Toán học"},
            {"domain_id": "dom_2", "domain_name": "Ngôn ngữ"},
        ],
        "answers": [
            {
                "question_id": f"q_{i}",
                "skill_id": f"skill_{i % 4}",
                "domain_id": f"dom_{(i % 2) + 1}",
                "difficulty_level": (i % 4) + 1,
                "is_correct": (i % 2 == 0),
                "time_spent_seconds": 30,
            }
            for i in range(30)
        ],
        "all_skill_ids": {
            "skill_0": "dom_1",
            "skill_1": "dom_2",
            "skill_2": "dom_1",
            "skill_3": "dom_2",
            "skill_untested": "dom_1",
        },
    }

    resp = client.post("/api/v1/diagnostic/analyze", json=payload)
    assert resp.status_code == 200, resp.text
    data = resp.json()

    assert data["student_id"] == "std_12345"
    assert data["submission_id"] == "sub_67890"
    assert -3.0 <= data["theta_0"] <= 3.0
    assert data["placement_class"] in ["FOUNDATION", "ACCELERATION", "BREAKTHROUGH"]
    assert len(data["domain_scores"]) == 2
    assert len(data["radar_chart"]) == 2
    assert len(data["skill_priors"]) == 5  # 4 tested + 1 untested inferred
    assert data["ai_commentary"] != ""

