"""
novelty_detector.py — Novel Archetype Pattern Detector (Core Flow 4 / Phase 2)
===============================================================================
Compares every question extracted by ``GeminiExamParserService`` (.NET Vision OCR,
LaTeX ``$...$``) against the curated Knowledge Graph (``v_eval_ai.archetype_patterns``)
using pgvector cosine similarity.

Decision rule
-------------
    similarity = 1 - (question_embedding <=> archetype_embedding)

    similarity >= threshold (0.75) -> MATCHED_EXISTING        (auto-linked to taxonomy)
    similarity <  threshold        -> NOVEL_PATTERN_CANDIDATE (staged for Academic review
                                                               in ``novel_pattern_proposals``)

Why sequential scan: 3072-dim vectors exceed pgvector's 2000-dim HNSW limit and the
archetype bank is small (~50-200 rows/subject), so exact scan is < 1 ms and 100% recall.
"""

from __future__ import annotations

import json
import logging
import os
import re
from dataclasses import asdict, dataclass, field
from typing import Any, Callable, Optional, Sequence

from graph.graph_db import GRAPH_SCHEMA, get_graph_embeddings, graph_connection, to_pgvector

logger = logging.getLogger(__name__)

DEFAULT_SIMILARITY_THRESHOLD: float = float(os.environ.get("NOVELTY_SIMILARITY_THRESHOLD", "0.75"))

STATUS_MATCHED = "MATCHED_EXISTING"
STATUS_NOVEL = "NOVEL_PATTERN_CANDIDATE"

EmbedFn = Callable[[Sequence[str]], list[list[float]]]


# ---------------------------------------------------------------------------
# Data contracts
# ---------------------------------------------------------------------------


@dataclass
class NearestArchetype:
    id: str
    pattern_code: str
    pattern_name: str
    skill_id: str
    similarity: float


@dataclass
class NoveltyResult:
    question_index: int
    status: str
    is_novel: bool
    max_similarity: float
    threshold: float
    nearest_archetype: Optional[NearestArchetype] = None
    proposal_id: Optional[str] = None
    duplicate_proposal: bool = False
    extra: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


# ---------------------------------------------------------------------------
# Pure helpers (unit-testable without DB / network)
# ---------------------------------------------------------------------------


def classify_similarity(similarity: float, threshold: float = DEFAULT_SIMILARITY_THRESHOLD) -> str:
    """Map a cosine similarity score to a novelty status."""
    return STATUS_NOVEL if similarity < threshold else STATUS_MATCHED


_WS_RE = re.compile(r"\s+")


def build_question_text(content: str, options: Optional[dict[str, str]] = None) -> str:
    """Normalize a parsed question (stem + options) into a single embedding input.

    Options are included because the distractors often define the archetype
    (e.g. "m < 1" vs "m > 1" in parameter-range problems).
    """
    if not content or not content.strip():
        raise ValueError("Question content must not be empty.")
    parts = [content.strip()]
    if options:
        for key in sorted(options):
            value = (options.get(key) or "").strip()
            if value:
                parts.append(f"{key}. {value}")
    return _WS_RE.sub(" ", "\n".join(parts)).strip()


def _default_embed(texts: Sequence[str]) -> list[list[float]]:
    """Embed texts with Gemini using the same task type as the seeded archetypes."""
    client = get_graph_embeddings()
    return [client.embed_query(t) for t in texts]


# ---------------------------------------------------------------------------
# Detector
# ---------------------------------------------------------------------------


class NoveltyDetector:
    """Detect questions whose archetype is not yet covered by the Knowledge Graph."""

    def __init__(
        self,
        threshold: float = DEFAULT_SIMILARITY_THRESHOLD,
        embed_fn: Optional[EmbedFn] = None,
    ) -> None:
        if not 0.0 < threshold < 1.0:
            raise ValueError("threshold must be within (0, 1).")
        self.threshold = threshold
        self._embed = embed_fn or _default_embed

    # -- database primitives -------------------------------------------------

    @staticmethod
    def find_nearest_archetype(cur, vector_literal: str) -> Optional[NearestArchetype]:
        cur.execute(
            f"""
            SELECT id::text AS id, pattern_code, pattern_name, skill_id::text AS skill_id,
                   1 - (embedding <=> %(v)s::vector) AS similarity
            FROM {GRAPH_SCHEMA}.archetype_patterns
            WHERE embedding IS NOT NULL
            ORDER BY embedding <=> %(v)s::vector ASC
            LIMIT 1;
            """,
            {"v": vector_literal},
        )
        row = cur.fetchone()
        if not row:
            return None
        return NearestArchetype(
            id=row["id"],
            pattern_code=row["pattern_code"],
            pattern_name=row["pattern_name"],
            skill_id=row["skill_id"],
            similarity=round(float(row["similarity"]), 6),
        )

    @staticmethod
    def stage_proposal(
        cur,
        *,
        source_exam: str,
        question_latex: str,
        similarity: float,
        nearest_id: Optional[str],
        image_urls: Optional[list[str]] = None,
        metadata: Optional[dict[str, Any]] = None,
    ) -> tuple[str, bool]:
        """Insert a PENDING_REVIEW proposal; reuse an existing pending one (idempotent)."""
        cur.execute(
            f"""
            SELECT id::text AS id FROM {GRAPH_SCHEMA}.novel_pattern_proposals
            WHERE raw_question_latex = %s AND status = 'PENDING_REVIEW'
            LIMIT 1;
            """,
            (question_latex,),
        )
        existing = cur.fetchone()
        if existing:
            return existing["id"], True

        cur.execute(
            f"""
            INSERT INTO {GRAPH_SCHEMA}.novel_pattern_proposals
                (source_exam_name, raw_question_latex, image_urls, extracted_metadata,
                 max_similarity_score, nearest_archetype_id, status)
            VALUES (%s, %s, %s::jsonb, %s::jsonb, %s, %s, 'PENDING_REVIEW')
            RETURNING id::text AS id;
            """,
            (
                source_exam,
                question_latex,
                json.dumps(image_urls or [], ensure_ascii=False),
                json.dumps(metadata or {}, ensure_ascii=False),
                similarity,
                nearest_id,
            ),
        )
        return cur.fetchone()["id"], False

    # -- public API ------------------------------------------------------------

    def check_questions(
        self,
        questions: Sequence[dict[str, Any]],
        source_exam: str,
        persist: bool = True,
    ) -> list[NoveltyResult]:
        """Evaluate a batch of parsed questions.

        Each question dict accepts the ``ExamParseResultDto`` fields:
        ``content`` (required), ``options`` (dict A-D), ``imageUrls``, ``suggestedSkillName``,
        ``correctOption``, ``questionNumber``.
        """
        if not questions:
            return []
        texts = [build_question_text(q.get("content", ""), q.get("options")) for q in questions]
        vectors = self._embed(texts)
        if len(vectors) != len(texts):
            raise RuntimeError("Embedding count mismatch.")

        results: list[NoveltyResult] = []
        with graph_connection() as conn:
            with conn.cursor() as cur:
                for idx, (q, text, vec) in enumerate(zip(questions, texts, vectors)):
                    nearest = self.find_nearest_archetype(cur, to_pgvector(vec))
                    max_sim = nearest.similarity if nearest else 0.0
                    status = classify_similarity(max_sim, self.threshold)
                    result = NoveltyResult(
                        question_index=idx,
                        status=status,
                        is_novel=status == STATUS_NOVEL,
                        max_similarity=max_sim,
                        threshold=self.threshold,
                        nearest_archetype=nearest,
                    )
                    if result.is_novel and persist:
                        metadata = {
                            k: q.get(k)
                            for k in ("questionNumber", "suggestedSkillName", "correctOption", "options")
                            if q.get(k) is not None
                        }
                        result.proposal_id, result.duplicate_proposal = self.stage_proposal(
                            cur,
                            source_exam=source_exam,
                            question_latex=text,
                            similarity=max_sim,
                            nearest_id=nearest.id if nearest else None,
                            image_urls=q.get("imageUrls"),
                            metadata=metadata,
                        )
                        logger.warning(
                            "Novel pattern candidate | exam=%s | q=%s | sim=%.4f | nearest=%s",
                            source_exam, idx, max_sim, nearest.pattern_code if nearest else None,
                        )
                    results.append(result)
            if persist:
                conn.commit()
        return results

    def check_novelty(
        self,
        question_latex: str,
        source_exam: str,
        options: Optional[dict[str, str]] = None,
        persist: bool = True,
    ) -> NoveltyResult:
        """Convenience wrapper for a single question."""
        return self.check_questions(
            [{"content": question_latex, "options": options}], source_exam, persist
        )[0]


def summarize(results: Sequence[NoveltyResult]) -> dict[str, Any]:
    """Aggregate batch statistics for dashboards / API responses."""
    novel = [r for r in results if r.is_novel]
    return {
        "total": len(results),
        "matched_existing": len(results) - len(novel),
        "novel_candidates": len(novel),
        "new_proposals": sum(1 for r in novel if r.proposal_id and not r.duplicate_proposal),
    }
