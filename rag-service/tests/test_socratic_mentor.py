"""
test_socratic_mentor.py — Unit Tests for Mentor Human-in-the-Loop & Resolution APIs
=====================================================================================
Validates report flagging, interaction querying, exemplar override, and novel trap creation.
"""

from __future__ import annotations

import os
import sys
from unittest.mock import MagicMock, patch
from uuid import uuid4

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest
from fastapi.testclient import TestClient

from main import app

client = TestClient(app)


def test_report_interaction_success():
    interaction_id = uuid4()
    mock_cursor = MagicMock()
    mock_cursor.fetchone.return_value = {
        "id": str(interaction_id),
        "student_reported": True,
        "mentor_resolution": "REPORTED: AI gợi ý sai công thức",
    }
    mock_conn = MagicMock()
    mock_conn.cursor.return_value.__enter__.return_value = mock_cursor

    with patch("routers.socratic_tutor.graph_connection") as mock_ctx:
        mock_ctx.return_value.__enter__.return_value = mock_conn

        response = client.post(
            f"/api/v1/socratic/interactions/{interaction_id}/report",
            json={"reason": "AI gợi ý sai công thức"},
        )

        assert response.status_code == 200
        data = response.json()
        assert data["id"] == str(interaction_id)
        assert data["student_reported"] is True
        assert "REPORTED" in data["mentor_resolution"]


def test_report_interaction_not_found():
    interaction_id = uuid4()
    mock_cursor = MagicMock()
    mock_cursor.fetchone.return_value = None
    mock_conn = MagicMock()
    mock_conn.cursor.return_value.__enter__.return_value = mock_cursor

    with patch("routers.socratic_tutor.graph_connection") as mock_ctx:
        mock_ctx.return_value.__enter__.return_value = mock_conn

        response = client.post(
            f"/api/v1/socratic/interactions/{interaction_id}/report",
            json={"reason": "Gợi ý sai"},
        )

        assert response.status_code == 404
        assert "not found" in response.json()["detail"]


def test_list_interactions_success():
    mock_cursor = MagicMock()
    mock_cursor.fetchall.return_value = [
        {
            "id": str(uuid4()),
            "student_id": str(uuid4()),
            "question_id": str(uuid4()),
            "archetype_pattern_id": str(uuid4()),
            "student_selected_option": "B",
            "correct_option": "A",
            "matched_trap_id": str(uuid4()),
            "ai_guidance_transcript": [{"role": "tutor", "text": "Gợi ý"}],
            "validation_status": "PASSED",
            "student_reported": True,
            "mentor_resolution": "REPORTED_BY_STUDENT",
            "created_at": "2026-10-12T10:00:00Z",
            "pattern_code": "MATH_01",
            "pattern_name": "Tiệm cận",
            "core_theory_latex": "y = a/c",
            "trap_code": "TRAP_01",
            "trap_name": "Quên mẫu",
            "misconception_explanation": "Mẫu số khác không",
        }
    ]
    mock_conn = MagicMock()
    mock_conn.cursor.return_value.__enter__.return_value = mock_cursor

    with patch("routers.socratic_tutor.graph_connection") as mock_ctx:
        mock_ctx.return_value.__enter__.return_value = mock_conn

        response = client.get("/api/v1/socratic/interactions?reported_only=true&limit=10")

        assert response.status_code == 200
        items = response.json()
        assert len(items) == 1
        assert items[0]["pattern_code"] == "MATH_01"
        assert items[0]["student_reported"] is True


def test_override_exemplar_success():
    interaction_id = uuid4()
    archetype_id = uuid4()
    new_exemplar_id = uuid4()

    mock_cursor = MagicMock()
    # First fetch interaction
    mock_cursor.fetchone.side_effect = [
        {
            "id": str(interaction_id),
            "archetype_pattern_id": str(archetype_id),
            "question_id": str(uuid4()),
            "correct_option": "A",
            "ai_guidance_transcript": [],
        },
        # Second fetch inserted exemplar
        {"id": str(new_exemplar_id)},
    ]
    mock_conn = MagicMock()
    mock_conn.cursor.return_value.__enter__.return_value = mock_cursor

    with patch("routers.socratic_tutor.graph_connection") as mock_ctx:
        mock_ctx.return_value.__enter__.return_value = mock_conn

        payload = {
            "golden_solution": "Lời giải chuẩn của mentor: $x = \\frac{1}{2}$",
            "explanation_steps": ["Bước 1: Tìm TXĐ", "Bước 2: Giải nghiệm"],
        }
        response = client.post(
            f"/api/v1/socratic/interactions/{interaction_id}/override-exemplar",
            json=payload,
        )

        assert response.status_code == 200
        data = response.json()
        assert data["exemplar_id"] == str(new_exemplar_id)
        assert data["archetype_pattern_id"] == str(archetype_id)


def test_add_new_trap_success():
    interaction_id = uuid4()
    archetype_id = uuid4()
    new_trap_id = uuid4()

    mock_cursor = MagicMock()
    mock_cursor.fetchone.side_effect = [
        {
            "id": str(interaction_id),
            "archetype_pattern_id": str(archetype_id),
            "student_selected_option": "D",
        },
        {"id": str(new_trap_id), "trap_code": "TRAP_MENTOR_CUSTOM_01"},
    ]
    mock_conn = MagicMock()
    mock_conn.cursor.return_value.__enter__.return_value = mock_cursor

    with patch("routers.socratic_tutor.graph_connection") as mock_ctx:
        mock_ctx.return_value.__enter__.return_value = mock_conn

        payload = {
            "trap_name": "Nhầm dấu trừ thành cộng",
            "misconception_explanation": "Học sinh quên đổi dấu khi chuyển vế",
            "socratic_hint": "Khi chuyển số hạng qua dấu bằng thì dấu của nó thay đổi như thế nào?",
            "wrong_option": "D",
            "trap_code": "TRAP_MENTOR_CUSTOM_01",
        }
        response = client.post(
            f"/api/v1/socratic/interactions/{interaction_id}/add-trap",
            json=payload,
        )

        assert response.status_code == 200
        data = response.json()
        assert data["trap_id"] == str(new_trap_id)
        assert data["trap_code"] == "TRAP_MENTOR_CUSTOM_01"
