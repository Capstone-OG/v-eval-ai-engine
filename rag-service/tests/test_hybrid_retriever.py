"""Offline unit tests for graph/hybrid_retriever.py (no DB / network required)."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest

from graph.hybrid_retriever import (
    STATUS_GROUNDED,
    STATUS_LOW_CONFIDENCE,
    STATUS_NOT_FOUND,
    TRAP_ARCHETYPE_CANDIDATES,
    TRAP_BY_EXEMPLAR_LETTER,
    TRAP_NONE,
    ArchetypeNode,
    ExemplarNode,
    HybridGraphRetriever,
    SocraticSubgraph,
    TrapNode,
    classify_anchor,
    normalize_for_match,
    resolve_traps,
    text_match_ratio,
)


def _trap(code, letter):
    return TrapNode(id=code, trap_code=code, trap_name=code, wrong_option=letter,
                    misconception_explanation="m", socratic_hint="h")


def _exemplar(ratio):
    return ExemplarNode(id="e", question_latex="q", golden_solution="s", correct_option="A",
                        explanation_steps=[], text_match_ratio=ratio)


TRAPS = [_trap("T_B", "B"), _trap("T_C", "C"), _trap("T_D", "D")]


def test_classify_anchor():
    assert classify_anchor(None, 0.75) == STATUS_NOT_FOUND
    assert classify_anchor(0.75, 0.75) == STATUS_GROUNDED
    assert classify_anchor(0.749, 0.75) == STATUS_LOW_CONFIDENCE


def test_letter_match_only_when_question_is_the_exemplar():
    trap, cands, strategy = resolve_traps(TRAPS, [_exemplar(0.97)], "c", "A")
    assert (trap.trap_code, cands, strategy) == ("T_C", [], TRAP_BY_EXEMPLAR_LETTER)


def test_variant_question_returns_all_candidates_instead_of_trusting_letter():
    trap, cands, strategy = resolve_traps(TRAPS, [_exemplar(0.70)], "B", "A")
    assert trap is None and strategy == TRAP_ARCHETYPE_CANDIDATES
    assert [t.trap_code for t in cands] == ["T_B", "T_C", "T_D"]


def test_exemplar_without_trap_for_letter_falls_back_to_candidates():
    trap, cands, strategy = resolve_traps([_trap("T_B", "B")], [_exemplar(1.0)], "D", "A")
    assert trap is None and strategy == TRAP_ARCHETYPE_CANDIDATES and len(cands) == 1


def test_correct_answer_or_no_traps_yields_no_trap():
    assert resolve_traps(TRAPS, [_exemplar(1.0)], "a", "A") == (None, [], TRAP_NONE)
    assert resolve_traps([], [_exemplar(1.0)], "B", "A") == (None, [], TRAP_NONE)


def test_text_match_ignores_latex_delimiters_and_spacing():
    assert normalize_for_match("  $20\\pi$  cm/s ") == "20pi cm/s"
    assert text_match_ratio("T = $0{,}5$ s", "T = 0,5 s") == 1.0
    assert text_match_ratio("abc", "xyz") < 0.5


def test_subgraph_serialization_flags_grounding():
    anchor = ArchetypeNode("a", "CODE", "name", "s", "thm", None, 0.8)
    data = SocraticSubgraph(status=STATUS_GROUNDED, anchor=anchor).to_dict()
    assert data["is_grounded"] is True and data["anchor"]["pattern_code"] == "CODE"
    assert SocraticSubgraph(status=STATUS_LOW_CONFIDENCE).to_dict()["is_grounded"] is False


def test_retriever_parameter_validation():
    with pytest.raises(ValueError):
        HybridGraphRetriever(min_similarity=0, embed_fn=lambda t: [])
    with pytest.raises(ValueError):
        HybridGraphRetriever(top_k=0, embed_fn=lambda t: [])
