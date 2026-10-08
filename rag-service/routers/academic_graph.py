"""
academic_graph.py — Academic Knowledge Graph Router (Core Flow 4 / Phase 2)
============================================================================
REST endpoints used by the Academic team (and by V-Eval-Ai_Engine .NET after
``GeminiExamParserService`` finishes Vision OCR) to:

1. Run novelty detection on parsed exam questions (single list or full ParsedExamDto).
2. List novel pattern proposals waiting for review.
3. Record the Academic decision on a proposal (APPROVED / MERGED / REJECTED).

Endpoints are synchronous ``def`` so FastAPI runs the blocking psycopg/Gemini calls
in its worker threadpool without blocking the event loop.
"""

from __future__ import annotations

import logging
from typing import Any, Literal, Optional

from fastapi import APIRouter, HTTPException, Query
from pydantic import BaseModel, Field

from graph.graph_db import GRAPH_SCHEMA, graph_connection
from graph.novelty_detector import (
    DEFAULT_SIMILARITY_THRESHOLD,
    NoveltyDetector,
    flatten_parsed_exam,
    summarize,
)

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/v1/academic-graph", tags=["Academic Knowledge Graph"])


# ---------------------------------------------------------------------------
# Schemas (mirror .NET ParsedQuestionDto / ParsedExamDto snake_case JSON)
# ---------------------------------------------------------------------------


class ParsedQuestionIn(BaseModel):
    question_number: Optional[int] = None
    page_number: Optional[int] = None
    content: str = Field(..., min_length=1, description="Question stem in LaTeX ($...$)")
    suggested_skill_name: Optional[str] = None
    options: dict[str, str] = Field(default_factory=dict)
    correct_option: Optional[str] = None
    explanation: Optional[str] = None
    difficulty_level: Optional[int] = None
    image_url: Optional[str] = None


class NoveltyCheckRequest(BaseModel):
    source_exam_name: str = Field(..., min_length=1, examples=["Đề thi thử ĐGNL Sở GD Hà Nội 2026"])
    questions: list[ParsedQuestionIn] = Field(default_factory=list)
    parsed_exam: Optional[dict[str, Any]] = Field(
        default=None, description="Optional full ParsedExamDto; flattened and merged with `questions`."
    )
    threshold: float = Field(default=DEFAULT_SIMILARITY_THRESHOLD, gt=0.0, lt=1.0)
    persist: bool = Field(default=True, description="Stage novel candidates into novel_pattern_proposals")


class ProposalReviewRequest(BaseModel):
    decision: Literal["APPROVED", "MERGED", "REJECTED"]
    academic_feedback: Optional[str] = None
    merged_into_archetype_id: Optional[str] = Field(
        default=None, description="Required when decision = MERGED"
    )


MAX_QUESTIONS_PER_CALL = 200


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------


@router.post("/novelty/check", summary="Detect novel archetype patterns in parsed exam questions")
def check_novelty(req: NoveltyCheckRequest) -> dict[str, Any]:
    questions: list[dict[str, Any]] = [q.model_dump(exclude_none=True) for q in req.questions]
    if req.parsed_exam:
        questions.extend(flatten_parsed_exam(req.parsed_exam))
    if not questions:
        raise HTTPException(status_code=400, detail="No questions provided.")
    if len(questions) > MAX_QUESTIONS_PER_CALL:
        raise HTTPException(
            status_code=413, detail=f"Too many questions (max {MAX_QUESTIONS_PER_CALL} per call)."
        )

    try:
        detector = NoveltyDetector(threshold=req.threshold)
        results = detector.check_questions(questions, req.source_exam_name, persist=req.persist)
    except Exception as exc:  # embedding quota, DB outage...
        logger.exception("Novelty detection failed")
        raise HTTPException(status_code=502, detail=f"Novelty detection failed: {exc}") from exc

    items = []
    for q, r in zip(questions, results):
        d = r.to_dict()
        d["question_number"] = q.get("question_number")
        items.append(d)

    return {
        "source_exam_name": req.source_exam_name,
        "threshold": req.threshold,
        "summary": summarize(results),
        "results": items,
    }


@router.get("/proposals", summary="List novel pattern proposals")
def list_proposals(
    status: Literal["PENDING_REVIEW", "APPROVED", "MERGED", "REJECTED"] = "PENDING_REVIEW",
    limit: int = Query(default=50, ge=1, le=200),
    offset: int = Query(default=0, ge=0),
) -> dict[str, Any]:
    with graph_connection() as conn:
        rows = conn.execute(
            f"""
            SELECT p.id::text AS id, p.source_exam_name, p.raw_question_latex, p.image_urls,
                   p.extracted_metadata, p.max_similarity_score, p.status, p.academic_feedback,
                   p.created_at, p.nearest_archetype_id::text AS nearest_archetype_id,
                   a.pattern_code AS nearest_pattern_code, a.pattern_name AS nearest_pattern_name
            FROM {GRAPH_SCHEMA}.novel_pattern_proposals p
            LEFT JOIN {GRAPH_SCHEMA}.archetype_patterns a ON a.id = p.nearest_archetype_id
            WHERE p.status = %s
            ORDER BY p.max_similarity_score ASC NULLS FIRST, p.created_at DESC
            LIMIT %s OFFSET %s;
            """,
            (status, limit, offset),
        ).fetchall()
        total = conn.execute(
            f"SELECT count(*) AS n FROM {GRAPH_SCHEMA}.novel_pattern_proposals WHERE status = %s;",
            (status,),
        ).fetchone()["n"]
    return {"status": status, "total": total, "items": rows}


@router.patch("/proposals/{proposal_id}/review", summary="Record the Academic decision on a proposal")
def review_proposal(proposal_id: str, req: ProposalReviewRequest) -> dict[str, Any]:
    if req.decision == "MERGED" and not req.merged_into_archetype_id:
        raise HTTPException(status_code=400, detail="merged_into_archetype_id is required for MERGED.")

    with graph_connection() as conn:
        if req.merged_into_archetype_id:
            exists = conn.execute(
                f"SELECT 1 FROM {GRAPH_SCHEMA}.archetype_patterns WHERE id = %s::uuid;",
                (req.merged_into_archetype_id,),
            ).fetchone()
            if not exists:
                raise HTTPException(status_code=404, detail="Target archetype not found.")

        row = conn.execute(
            f"""
            UPDATE {GRAPH_SCHEMA}.novel_pattern_proposals
            SET status = %s,
                academic_feedback = %s,
                nearest_archetype_id = COALESCE(%s::uuid, nearest_archetype_id)
            WHERE id = %s::uuid AND status = 'PENDING_REVIEW'
            RETURNING id::text AS id, status, academic_feedback,
                      nearest_archetype_id::text AS nearest_archetype_id;
            """,
            (req.decision, req.academic_feedback, req.merged_into_archetype_id, proposal_id),
        ).fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="Proposal not found or already reviewed.")
        conn.commit()
    return row
