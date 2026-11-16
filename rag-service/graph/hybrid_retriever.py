"""
hybrid_retriever.py â€” Hybrid GraphRAG Retriever (Core Flow 4 / Phase 3)
========================================================================
When a student taps "Ask AI Tutor" on a wrong answer, this module extracts the
grounded *Socratic subgraph* from the Knowledge Graph (schema ``v_eval_ai``):

    Hop 1 (vector anchor)  : question embedding --cosine--> archetype_patterns (top-k)
                             skill-scoped first, global fallback.
    Hop 2 (graph expansion): anchor archetype --1:N--> pattern_exemplars
                                              --1:N--> pattern_traps

Design decisions
----------------
* **Fail closed**: if the best archetype similarity is below ``min_similarity`` the
  question is not covered by the curated graph. We return ``LOW_CONFIDENCE`` with
  NO exemplars/traps so the tutor cannot ground itself on the wrong theorems.
* **Trap alignment**: ``pattern_traps.wrong_option`` is the option letter *inside the
  exemplar*. A different question of the same archetype shuffles its distractors,
  so letter matching is only trusted when the student's question is (near-)identical
  to an exemplar (``EXEMPLAR_OPTION_LETTER``). Otherwise every trap of the archetype is
  returned as a candidate (``ARCHETYPE_CANDIDATES``) for the Phase 4 LLM to align with
  the text of the option the student picked.
* Same embedding input/space as ``NoveltyDetector`` (stem + sorted options, ``embed_query``).
* Synchronous psycopg: call through ``run_in_threadpool`` from async endpoints.
"""

from __future__ import annotations

import difflib
import logging
import os
import re
import time
import uuid
from dataclasses import asdict, dataclass, field
from typing import Any, Callable, Optional, Sequence

from graph.graph_db import GRAPH_SCHEMA, get_graph_embeddings, graph_connection, to_pgvector
from graph.novelty_detector import build_question_text

logger = logging.getLogger(__name__)

DEFAULT_MIN_SIMILARITY: float = float(os.environ.get("RETRIEVAL_MIN_SIMILARITY", "0.75"))
DEFAULT_TOP_K: int = 3
DEFAULT_MAX_EXEMPLARS: int = 2
EXEMPLAR_IDENTITY_RATIO: float = 0.90  # text ratio above which a question "is" the exemplar

STATUS_GROUNDED = "GROUNDED"
STATUS_LOW_CONFIDENCE = "LOW_CONFIDENCE"
STATUS_NOT_FOUND = "NOT_FOUND"

TRAP_BY_EXEMPLAR_LETTER = "EXEMPLAR_OPTION_LETTER"
TRAP_ARCHETYPE_CANDIDATES = "ARCHETYPE_CANDIDATES"
TRAP_NONE = "NONE"

SCOPE_SKILL = "SKILL"
SCOPE_GLOBAL = "GLOBAL"

EmbedFn = Callable[[str], list[float]]


# ---------------------------------------------------------------------------
# Subgraph data contracts
# ---------------------------------------------------------------------------


@dataclass
class ArchetypeNode:
    id: str
    pattern_code: str
    pattern_name: str
    skill_id: str
    core_theorems: str
    fast_solving_heuristics: Optional[str]
    similarity: float


@dataclass
class ExemplarNode:
    id: str
    question_latex: str
    golden_solution: str
    correct_option: str
    explanation_steps: list[Any]
    text_match_ratio: float = 0.0


@dataclass
class TrapNode:
    id: str
    trap_code: str
    trap_name: str
    wrong_option: Optional[str]
    misconception_explanation: str
    socratic_hint: str


@dataclass
class SocraticSubgraph:
    status: str
    scope: Optional[str] = None
    anchor: Optional[ArchetypeNode] = None
    alternatives: list[ArchetypeNode] = field(default_factory=list)
    exemplars: list[ExemplarNode] = field(default_factory=list)
    matched_trap: Optional[TrapNode] = None
    candidate_traps: list[TrapNode] = field(default_factory=list)
    trap_match_strategy: str = TRAP_NONE
    student_selected_option: Optional[str] = None
    student_selected_option_text: Optional[str] = None
    min_similarity: float = DEFAULT_MIN_SIMILARITY
    retrieval_ms: float = 0.0

    @property
    def is_grounded(self) -> bool:
        return self.status == STATUS_GROUNDED

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["is_grounded"] = self.is_grounded
        return data


# ---------------------------------------------------------------------------
# Pure helpers (unit-testable without DB / network)
# ---------------------------------------------------------------------------

_LATEX_DELIM_RE = re.compile(r"[$\\{}]")
_WS_RE = re.compile(r"\s+")


def normalize_for_match(text: str) -> str:
    """Loose normalization for exemplar identity checks (drops LaTeX delimiters, collapses spaces)."""
    return _WS_RE.sub(" ", _LATEX_DELIM_RE.sub("", (text or "").lower())).strip()


def text_match_ratio(a: str, b: str) -> float:
    return round(difflib.SequenceMatcher(None, normalize_for_match(a), normalize_for_match(b)).ratio(), 4)


def classify_anchor(similarity: Optional[float], min_similarity: float) -> str:
    if similarity is None:
        return STATUS_NOT_FOUND
    return STATUS_GROUNDED if similarity >= min_similarity else STATUS_LOW_CONFIDENCE


def resolve_traps(
    traps: Sequence[TrapNode],
    exemplars: Sequence[ExemplarNode],
    selected_option: Optional[str],
    correct_option: Optional[str],
    identity_ratio: float = EXEMPLAR_IDENTITY_RATIO,
) -> tuple[Optional[TrapNode], list[TrapNode], str]:
    """Pick the trap the student fell into, or return candidates for LLM alignment."""
    if not traps:
        return None, [], TRAP_NONE
    sel = (selected_option or "").strip().upper()
    if sel and correct_option and sel == correct_option.strip().upper():
        return None, [], TRAP_NONE  # student was actually right: no misconception to address

    is_exemplar = any(e.text_match_ratio >= identity_ratio for e in exemplars)
    if sel and is_exemplar:
        for t in traps:
            if (t.wrong_option or "").strip().upper() == sel:
                return t, [], TRAP_BY_EXEMPLAR_LETTER
    return None, list(traps), TRAP_ARCHETYPE_CANDIDATES


def _default_embed(text: str) -> list[float]:
    return get_graph_embeddings().embed_query(text)


# ---------------------------------------------------------------------------
# Retriever
# ---------------------------------------------------------------------------


class HybridGraphRetriever:
    """Two-hop retrieval: pgvector anchor -> relational graph expansion."""

    def __init__(
        self,
        min_similarity: float = DEFAULT_MIN_SIMILARITY,
        top_k: int = DEFAULT_TOP_K,
        max_exemplars: int = DEFAULT_MAX_EXEMPLARS,
        embed_fn: Optional[EmbedFn] = None,
    ) -> None:
        if not 0.0 < min_similarity < 1.0:
            raise ValueError("min_similarity must be within (0, 1).")
        if top_k < 1 or max_exemplars < 1:
            raise ValueError("top_k and max_exemplars must be >= 1.")
        self.min_similarity = min_similarity
        self.top_k = top_k
        self.max_exemplars = max_exemplars
        self._embed = embed_fn or _default_embed

    # -- Hop 1: vector anchor ----------------------------------------------------

    def _anchor_candidates(self, cur, vector_literal: str, skill_id: Optional[str]) -> list[ArchetypeNode]:
        cur.execute(
            f"""
            SELECT id::text AS id, pattern_code, pattern_name, skill_id::text AS skill_id,
                   core_theorems, fast_solving_heuristics,
                   1 - (embedding <=> %(v)s::vector) AS similarity
            FROM {GRAPH_SCHEMA}.archetype_patterns
            WHERE embedding IS NOT NULL
              AND is_verified = TRUE
              AND (%(skill)s::uuid IS NULL OR skill_id = %(skill)s::uuid)
            ORDER BY embedding <=> %(v)s::vector ASC
            LIMIT %(k)s;
            """,
            {"v": vector_literal, "skill": skill_id, "k": self.top_k},
        )
        return [
            ArchetypeNode(**{**r, "similarity": round(float(r["similarity"]), 6)})
            for r in cur.fetchall()
        ]

    # -- Hop 2: graph expansion --------------------------------------------------

    def _expand(self, cur, archetype_id: str, question_text: str) -> tuple[list[ExemplarNode], list[TrapNode]]:
        cur.execute(
            f"""
            SELECT id::text AS id, question_latex, golden_solution, correct_option, explanation_steps
            FROM {GRAPH_SCHEMA}.pattern_exemplars
            WHERE archetype_pattern_id = %s::uuid
            ORDER BY created_at ASC;
            """,
            (archetype_id,),
        )
        exemplars = [ExemplarNode(**r) for r in cur.fetchall()]
        for e in exemplars:
            e.text_match_ratio = text_match_ratio(question_text, e.question_latex)
        exemplars.sort(key=lambda e: e.text_match_ratio, reverse=True)

        cur.execute(
            f"""
            SELECT id::text AS id, trap_code, trap_name, wrong_option,
                   misconception_explanation, socratic_hint
            FROM {GRAPH_SCHEMA}.pattern_traps
            WHERE archetype_pattern_id = %s::uuid
            ORDER BY wrong_option ASC NULLS LAST, trap_code ASC;
            """,
            (archetype_id,),
        )
        traps = [TrapNode(**r) for r in cur.fetchall()]
        return exemplars[: self.max_exemplars], traps

    # -- Federated Content Retrieval (v_eval_content."Materials" & "Questions") -

    def _fetch_material_from_content(
        self, cur, question_id: Optional[str], skill_id: Optional[str]
    ) -> Optional[dict[str, Any]]:
        """Zero-hardcode federated lookup: retrieve official textbook lesson & golden explanation from v_eval_content."""
        def _is_valid_uuid(val: Optional[str]) -> bool:
            if not val:
                return False
            try:
                uuid.UUID(str(val))
                return True
            except (ValueError, AttributeError, TypeError):
                return False

        # 1. Try relational join: question_id -> Questions.skill_id -> Materials
        if question_id and _is_valid_uuid(question_id):
            try:
                cur.execute(
                    """
                    SELECT m.material_id::text, m.title, m.content, q.explanation, q.skill_id::text
                    FROM v_eval_content."Questions" q
                    JOIN v_eval_content."Materials" m ON m.skill_id = q.skill_id
                    WHERE q.question_id = %(qid)s::uuid
                    LIMIT 1;
                    """,
                    {"qid": str(question_id)},
                )
                row = cur.fetchone()
                if row and row.get("content"):
                    return dict(row)

                # Fallback: Check if Questions has explanation text directly
                cur.execute(
                    """
                    SELECT q.question_id::text AS material_id,
                           'Phương pháp giải bài tập khảo thí' AS title,
                           q.explanation AS content,
                           q.explanation,
                           q.skill_id::text
                    FROM v_eval_content."Questions" q
                    WHERE q.question_id = %(qid)s::uuid
                      AND q.explanation IS NOT NULL
                      AND length(trim(q.explanation)) > 0
                    LIMIT 1;
                    """,
                    {"qid": str(question_id)},
                )
                row = cur.fetchone()
                if row and row.get("content"):
                    return dict(row)
            except Exception as exc:
                logger.debug("Content material lookup by question_id failed (%s)", exc)

        # 2. Try direct lookup via skill_id in Materials
        if skill_id and _is_valid_uuid(skill_id):
            try:
                cur.execute(
                    """
                    SELECT m.material_id::text, m.title, m.content, NULL AS explanation, m.skill_id::text
                    FROM v_eval_content."Materials" m
                    WHERE m.skill_id = %(skill)s::uuid
                    LIMIT 1;
                    """,
                    {"skill": str(skill_id)},
                )
                row = cur.fetchone()
                if row and row.get("content"):
                    return dict(row)
            except Exception as exc:
                logger.debug("Content material lookup by skill_id failed (%s)", exc)

        return None

    # -- public API ------------------------------------------------------------

    def retrieve(
        self,
        question_latex: str,
        options: Optional[dict[str, str]] = None,
        student_selected_option: Optional[str] = None,
        correct_option: Optional[str] = None,
        skill_id: Optional[str] = None,
        question_id: Optional[str] = None,
    ) -> SocraticSubgraph:
        """Return the grounded Socratic subgraph for a question.

        Resolution Priority:
        1. Dynamic Federated Curriculum Material (v_eval_content."Materials")
        2. Vector Similarity against curated Knowledge Graph (v_eval_ai.archetype_patterns >= min_similarity)
        3. Fail-closed to STATUS_LOW_CONFIDENCE (never hallucinate unrelated topics)
        """
        def _is_valid_uuid(val: Optional[str]) -> bool:
            if not val:
                return False
            try:
                uuid.UUID(str(val))
                return True
            except (ValueError, AttributeError, TypeError):
                return False

        started = time.perf_counter()
        question_text = build_question_text(question_latex, options)
        sel = (student_selected_option or "").strip().upper() or None

        result = SocraticSubgraph(
            status=STATUS_NOT_FOUND,
            student_selected_option=sel,
            student_selected_option_text=(options or {}).get(sel) if sel else None,
            min_similarity=self.min_similarity,
        )

        with graph_connection() as conn:
            with conn.cursor() as cur:
                # Priority 1: Dynamic Federated Material Retrieval from v_eval_content
                material = self._fetch_material_from_content(cur, question_id, skill_id)
                if material and material.get("content"):
                    mat_id = material["material_id"]
                    mat_skill = material.get("skill_id") or (skill_id or "")
                    result.anchor = ArchetypeNode(
                        id=mat_id,
                        pattern_code=f"CURRICULUM_{mat_id[:8]}",
                        pattern_name=material.get("title") or "Tài liệu học tập ĐGNL",
                        skill_id=mat_skill,
                        core_theorems=material["content"],
                        fast_solving_heuristics=None,
                        similarity=0.98,
                    )
                    result.scope = "CURRICULUM_MATERIAL"
                    result.status = STATUS_GROUNDED
                    if material.get("explanation"):
                        result.exemplars = [
                            ExemplarNode(
                                id=f"EXP_{question_id[:8]}" if question_id else "EXP_CURR",
                                question_latex=question_latex,
                                golden_solution=material["explanation"],
                                correct_option=correct_option or "",
                                explanation_steps=[],
                                text_match_ratio=1.0,
                            )
                        ]
                    if mat_skill and _is_valid_uuid(mat_skill):
                        cur.execute(
                            f"""
                            SELECT id::text FROM {GRAPH_SCHEMA}.archetype_patterns
                            WHERE skill_id = %s::uuid LIMIT 1;
                            """,
                            (mat_skill,),
                        )
                        arch_row = cur.fetchone()
                        if arch_row:
                            _, traps = self._expand(cur, arch_row["id"], question_text)
                            result.matched_trap, result.candidate_traps, result.trap_match_strategy = resolve_traps(
                                traps, result.exemplars, sel, correct_option
                            )
                else:
                    # Priority 2: Vector Similarity against curated Knowledge Graph
                    vector_literal = None
                    try:
                        vector = self._embed(question_text)
                        if vector:
                            vector_literal = to_pgvector(vector)
                    except Exception as embed_err:
                        logger.warning("Vector embedding failed (%s).", embed_err)

                    candidates, scope = [], None
                    if vector_literal:
                        if skill_id and _is_valid_uuid(skill_id):
                            candidates, scope = self._anchor_candidates(cur, vector_literal, skill_id), SCOPE_SKILL
                        if not candidates or candidates[0].similarity < self.min_similarity:
                            global_candidates = self._anchor_candidates(cur, vector_literal, None)
                            if global_candidates and (
                                not candidates or global_candidates[0].similarity > candidates[0].similarity
                            ):
                                candidates, scope = global_candidates, SCOPE_GLOBAL

                    if candidates and candidates[0].similarity >= self.min_similarity:
                        result.scope = scope
                        result.anchor, result.alternatives = candidates[0], candidates[1:]
                        result.status = STATUS_GROUNDED
                        exemplars, traps = self._expand(cur, result.anchor.id, question_text)
                        result.exemplars = exemplars
                        result.matched_trap, result.candidate_traps, result.trap_match_strategy = resolve_traps(
                            traps, exemplars, sel, correct_option
                        )
                    else:
                        result.status = STATUS_LOW_CONFIDENCE

        result.retrieval_ms = round((time.perf_counter() - started) * 1000, 1)
        logger.info(
            "Subgraph retrieval | status=%s scope=%s anchor=%s sim=%s trap=%s (%s) | %.1fms",
            result.status, result.scope,
            result.anchor.pattern_code if result.anchor else None,
            result.anchor.similarity if result.anchor else None,
            result.matched_trap.trap_code if result.matched_trap else None,
            result.trap_match_strategy, result.retrieval_ms,
        )
        return result

