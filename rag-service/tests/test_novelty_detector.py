"""Offline unit tests for graph/novelty_detector.py (no DB / network required)."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest

from graph.graph_db import to_pgvector
from graph.novelty_detector import (
    STATUS_MATCHED,
    STATUS_NOVEL,
    NoveltyDetector,
    NoveltyResult,
    build_question_text,
    classify_similarity,
    flatten_parsed_exam,
    summarize,
)


def test_classify_similarity_threshold_boundary():
    assert classify_similarity(0.80, 0.75) == STATUS_MATCHED
    assert classify_similarity(0.75, 0.75) == STATUS_MATCHED  # inclusive lower bound
    assert classify_similarity(0.7499, 0.75) == STATUS_NOVEL
    assert classify_similarity(0.0, 0.75) == STATUS_NOVEL


def test_build_question_text_includes_sorted_options_and_normalizes_ws():
    text = build_question_text("Tìm  $m$\n\n để ...", {"B": "$m>1$", "A": "$m<1$", "C": ""})
    assert text == "Tìm $m$ để ... A. $m<1$ B. $m>1$"


def test_build_question_text_rejects_empty():
    with pytest.raises(ValueError):
        build_question_text("   ")


def test_flatten_parsed_exam_merges_passages_and_sorts():
    exam = {
        "single_questions": [{"question_number": 3, "content": "Q3"}, {"question_number": 1, "content": ""}],
        "passages": [{"start_question": 1, "end_question": 2,
                      "questions": [{"question_number": 2, "content": "Q2"}]}],
    }
    flat = flatten_parsed_exam(exam)
    assert [q["question_number"] for q in flat] == [2, 3]  # empty Q1 dropped
    assert flat[0]["passage_range"] == "1-2"
    assert "passage_range" not in flat[1]


def test_summarize_counts():
    rs = [
        NoveltyResult(0, STATUS_MATCHED, False, 0.9, 0.75),
        NoveltyResult(1, STATUS_NOVEL, True, 0.5, 0.75, proposal_id="p1"),
        NoveltyResult(2, STATUS_NOVEL, True, 0.4, 0.75, proposal_id="p2", duplicate_proposal=True),
    ]
    assert summarize(rs) == {"total": 3, "matched_existing": 1, "novel_candidates": 2, "new_proposals": 1}


def test_detector_threshold_validation_and_empty_batch():
    with pytest.raises(ValueError):
        NoveltyDetector(threshold=1.5, embed_fn=lambda t: [])
    assert NoveltyDetector(embed_fn=lambda t: []).check_questions([], "x") == []


def test_to_pgvector_format():
    assert to_pgvector([0.5, -1]) == "[0.50000000,-1.00000000]"
    with pytest.raises(ValueError):
        to_pgvector([])
