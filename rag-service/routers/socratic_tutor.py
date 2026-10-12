"""
socratic_tutor.py — Socratic AI Tutor REST & SSE Streaming Router (Core Flow 4 / Phase 4)
==========================================================================================
Provides student-facing conversational endpoints for real-time Socratic learning:

1. `POST /api/v1/socratic/ask`: Direct JSON response for testing / non-streaming clients.
2. `POST /api/v1/socratic/ask-stream`: Real-time Server-Sent Events (SSE) streaming
   (`text/event-stream`) with automatic database interaction logging in
   `v_eval_ai.ai_tutor_interaction_logs`.
"""

from __future__ import annotations

import json
import logging
from typing import Any, Optional
from uuid import UUID, uuid4

from fastapi import APIRouter, HTTPException, Query
from fastapi.responses import HTMLResponse, StreamingResponse
from pydantic import BaseModel, Field

from graph.graph_db import GRAPH_SCHEMA, graph_connection
from graph.hybrid_retriever import HybridGraphRetriever
from socratic.socratic_engine import (
    generate_socratic_guidance,
    generate_socratic_guidance_stream,
)

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/v1/socratic", tags=["Socratic AI Tutor"])


# ---------------------------------------------------------------------------
# Request & Response Contracts
# ---------------------------------------------------------------------------


class SocraticAskRequest(BaseModel):
    student_id: UUID = Field(..., description="Unique student account UUID")
    question_id: UUID = Field(..., description="Target exam question UUID")
    question_latex: str = Field(..., min_length=1, description="Question stem text in LaTeX")
    options: dict[str, str] = Field(default_factory=dict, description="Option dictionary {'A': '...', 'B': '...'}")
    student_selected_option: str = Field(..., min_length=1, max_length=10, description="Option letter picked by student (e.g. 'B')")
    correct_option: str = Field(..., min_length=1, max_length=10, description="Ground truth correct option (e.g. 'A')")
    skill_id: Optional[UUID] = Field(default=None, description="Optional taxonomy skill UUID for search scoping")


class SocraticAskResponse(BaseModel):
    interaction_id: Optional[str] = None
    guidance_text: str
    is_fallback: bool
    validation_status: str
    rejection_reason: str = ""
    archetype_code: Optional[str] = None
    matched_trap_code: Optional[str] = None
    subgraph_status: str


class ReportInteractionRequest(BaseModel):
    reason: Optional[str] = Field(default=None, description="Reason for reporting wrong answer")
    student_id: Optional[UUID] = Field(default=None, description="Optional student ID verification")


class ReportInteractionResponse(BaseModel):
    id: str
    student_reported: bool
    mentor_resolution: Optional[str] = None
    message: str


class OverrideExemplarRequest(BaseModel):
    golden_solution: str = Field(..., min_length=5, description="Standard golden solution text/steps in LaTeX")
    question_latex: Optional[str] = Field(default=None, description="Override or verified question LaTeX")
    correct_option: Optional[str] = Field(default=None, description="Correct option letter (e.g. 'A')")
    explanation_steps: list[str] = Field(default_factory=list, description="List of step-by-step LaTeX derivations")
    archetype_pattern_id: Optional[UUID] = Field(default=None, description="Target archetype UUID")


class OverrideExemplarResponse(BaseModel):
    exemplar_id: str
    archetype_pattern_id: str
    message: str


class AddNewTrapRequest(BaseModel):
    trap_name: str = Field(..., min_length=2, description="Human readable name of the misconception")
    misconception_explanation: str = Field(..., min_length=5, description="Detailed pedagogical analysis of the error")
    socratic_hint: str = Field(..., min_length=5, description="Guiding question to nudge students")
    wrong_option: Optional[str] = Field(default=None, description="Option letter associated with this trap (e.g. 'B')")
    trap_code: Optional[str] = Field(default=None, description="Unique trap code (auto-generated if omitted)")
    archetype_pattern_id: Optional[UUID] = Field(default=None, description="Target archetype UUID")


class AddNewTrapResponse(BaseModel):
    trap_id: str
    trap_code: str
    archetype_pattern_id: str
    message: str


class TutorInteractionItem(BaseModel):
    id: str
    student_id: str
    question_id: str
    archetype_pattern_id: Optional[str] = None
    student_selected_option: str
    correct_option: str
    matched_trap_id: Optional[str] = None
    ai_guidance_transcript: list[dict[str, Any]] = Field(default_factory=list)
    validation_status: str
    student_reported: bool
    mentor_resolution: Optional[str] = None
    created_at: str
    pattern_code: Optional[str] = None
    pattern_name: Optional[str] = None
    core_theory_latex: Optional[str] = None
    trap_code: Optional[str] = None
    trap_name: Optional[str] = None
    misconception_explanation: Optional[str] = None



# ---------------------------------------------------------------------------
# Database Logging Helper
# ---------------------------------------------------------------------------


def log_interaction(
    student_id: str,
    question_id: str,
    archetype_pattern_id: Optional[str],
    student_selected_option: str,
    correct_option: str,
    matched_trap_id: Optional[str],
    ai_guidance_transcript: list[dict[str, Any]],
    validation_status: str,
) -> Optional[str]:
    """Persist student tutor session into v_eval_ai.ai_tutor_interaction_logs."""
    try:
        with graph_connection() as conn:
            with conn.cursor() as cur:
                cur.execute(
                    f"""
                    INSERT INTO {GRAPH_SCHEMA}.ai_tutor_interaction_logs
                    (student_id, question_id, archetype_pattern_id, student_selected_option,
                     correct_option, matched_trap_id, ai_guidance_transcript, validation_status)
                    VALUES (%s::uuid, %s::uuid, %s::uuid, %s, %s, %s::uuid, %s::jsonb, %s)
                    RETURNING id::text AS id;
                    """,
                    (
                        student_id,
                        question_id,
                        archetype_pattern_id,
                        student_selected_option,
                        correct_option,
                        matched_trap_id,
                        json.dumps(ai_guidance_transcript, ensure_ascii=False),
                        validation_status,
                    ),
                )
                row = cur.fetchone()
                conn.commit()
                return row["id"] if row else None
    except Exception as exc:
        logger.error("Failed to log AI tutor interaction: %s", exc)
        return None


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------


@router.post("/ask", response_model=SocraticAskResponse, summary="Ask Socratic AI Tutor (JSON response)")
async def ask_socratic_json(req: SocraticAskRequest) -> SocraticAskResponse:
    """Synchronous JSON Socratic tutor endpoint."""
    try:
        retriever = HybridGraphRetriever()
        subgraph = retriever.retrieve(
            question_latex=req.question_latex,
            options=req.options,
            student_selected_option=req.student_selected_option,
            correct_option=req.correct_option,
            skill_id=str(req.skill_id) if req.skill_id else None,
        )
    except Exception as exc:
        logger.exception("Retrieval failed in ask_socratic_json")
        raise HTTPException(status_code=502, detail=f"Knowledge retrieval error: {exc}") from exc

    selected_text = req.options.get(req.student_selected_option.strip().upper())
    guidance_text, judge_result, is_fallback = await generate_socratic_guidance(
        question_latex=req.question_latex,
        student_selected_option=req.student_selected_option,
        correct_option=req.correct_option,
        subgraph_context=subgraph.to_dict(),
        options=req.options,
        student_selected_option_text=selected_text,
    )

    val_status = "PASSED" if judge_result.is_passed else "OVERRIDDEN"
    transcript = [
        {"role": "student", "option": req.student_selected_option, "option_text": selected_text},
        {"role": "tutor", "text": guidance_text, "is_fallback": is_fallback},
    ]

    interaction_id = log_interaction(
        student_id=str(req.student_id),
        question_id=str(req.question_id),
        archetype_pattern_id=subgraph.anchor.id if subgraph.anchor else None,
        student_selected_option=req.student_selected_option,
        correct_option=req.correct_option,
        matched_trap_id=subgraph.matched_trap.id if subgraph.matched_trap else None,
        ai_guidance_transcript=transcript,
        validation_status=val_status,
    )

    return SocraticAskResponse(
        interaction_id=interaction_id,
        guidance_text=guidance_text,
        is_fallback=is_fallback,
        validation_status=val_status,
        rejection_reason=judge_result.rejection_reason,
        archetype_code=subgraph.anchor.pattern_code if subgraph.anchor else None,
        matched_trap_code=subgraph.matched_trap.trap_code if subgraph.matched_trap else None,
        subgraph_status=subgraph.status,
    )


@router.post("/ask-stream", summary="Ask Socratic AI Tutor via real-time SSE stream")
async def ask_socratic_stream(req: SocraticAskRequest) -> StreamingResponse:
    """Real-time SSE token stream (text/event-stream) with pedagogical validation."""
    try:
        retriever = HybridGraphRetriever()
        subgraph = retriever.retrieve(
            question_latex=req.question_latex,
            options=req.options,
            student_selected_option=req.student_selected_option,
            correct_option=req.correct_option,
            skill_id=str(req.skill_id) if req.skill_id else None,
        )
    except Exception as exc:
        logger.exception("Retrieval failed in ask_socratic_stream")
        raise HTTPException(status_code=502, detail=f"Knowledge retrieval error: {exc}") from exc

    selected_text = req.options.get(req.student_selected_option.strip().upper())
    subgraph_dict = subgraph.to_dict()

    async def event_generator():
        # 1. Emit metadata event
        meta_event = {
            "type": "metadata",
            "archetype_code": subgraph.anchor.pattern_code if subgraph.anchor else None,
            "matched_trap_code": subgraph.matched_trap.trap_code if subgraph.matched_trap else None,
            "subgraph_status": subgraph.status,
        }
        yield f"event: metadata\ndata: {json.dumps(meta_event, ensure_ascii=False)}\n\n"

        # 2. Stream tokens
        full_text_parts = []
        try:
            async for chunk in generate_socratic_guidance_stream(
                question_latex=req.question_latex,
                student_selected_option=req.student_selected_option,
                correct_option=req.correct_option,
                subgraph_context=subgraph_dict,
                options=req.options,
                student_selected_option_text=selected_text,
            ):
                full_text_parts.append(chunk)
                payload = {"type": "token", "token": chunk}
                yield f"event: token\ndata: {json.dumps(payload, ensure_ascii=False)}\n\n"
        except Exception as stream_err:
            logger.error("Error during stream generation: %s", stream_err)
            err_payload = {"type": "error", "error": str(stream_err)}
            yield f"event: error\ndata: {json.dumps(err_payload, ensure_ascii=False)}\n\n"

        # 3. Save log and emit completion event
        full_text = "".join(full_text_parts)
        transcript = [
            {"role": "student", "option": req.student_selected_option, "option_text": selected_text},
            {"role": "tutor", "text": full_text},
        ]
        interaction_id = log_interaction(
            student_id=str(req.student_id),
            question_id=str(req.question_id),
            archetype_pattern_id=subgraph.anchor.id if subgraph.anchor else None,
            student_selected_option=req.student_selected_option,
            correct_option=req.correct_option,
            matched_trap_id=subgraph.matched_trap.id if subgraph.matched_trap else None,
            ai_guidance_transcript=transcript,
            validation_status="PASSED",
        )

        done_payload = {
            "type": "done",
            "interaction_id": interaction_id,
        }
        yield f"event: done\ndata: {json.dumps(done_payload, ensure_ascii=False)}\n\n"

    return StreamingResponse(
        event_generator(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


# ---------------------------------------------------------------------------
# Human-in-the-Loop & Mentor Resolution Endpoints (Core Flow 4 - Phase 6)
# ---------------------------------------------------------------------------


@router.post(
    "/interactions/{interaction_id}/report",
    response_model=ReportInteractionResponse,
    summary="Report inaccurate/misleading AI tutor response",
)
def report_interaction(interaction_id: UUID, req: ReportInteractionRequest) -> ReportInteractionResponse:
    """Record student feedback on flawed AI guidance and flag for mentor review."""
    resolution_note = f"REPORTED: {req.reason.strip()}" if req.reason else "REPORTED_BY_STUDENT"
    try:
        with graph_connection() as conn:
            with conn.cursor() as cur:
                cur.execute(
                    f"""
                    UPDATE {GRAPH_SCHEMA}.ai_tutor_interaction_logs
                    SET student_reported = TRUE,
                        mentor_resolution = COALESCE(mentor_resolution, %s)
                    WHERE id = %s::uuid
                    RETURNING id::text AS id, student_reported, mentor_resolution;
                    """,
                    (resolution_note, str(interaction_id)),
                )
                row = cur.fetchone()
                if not row:
                    raise HTTPException(status_code=404, detail=f"Interaction log {interaction_id} not found")
                conn.commit()
                return ReportInteractionResponse(
                    id=row["id"],
                    student_reported=row["student_reported"],
                    mentor_resolution=row["mentor_resolution"],
                    message="Báo cáo câu trả lời sai đã được ghi nhận. Ban chuyên môn sẽ thẩm định lời giải.",
                )
    except HTTPException:
        raise
    except Exception as exc:
        logger.exception("Failed to report interaction %s", interaction_id)
        raise HTTPException(status_code=500, detail=f"Database error reporting interaction: {exc}") from exc


@router.get(
    "/interactions",
    response_model=list[TutorInteractionItem],
    summary="List student AI tutor interaction logs for mentor audit",
)
def list_interactions(
    reported_only: bool = Query(default=False, description="Filter only interactions flagged by students"),
    limit: int = Query(default=50, ge=1, le=200, description="Page limit"),
    offset: int = Query(default=0, ge=0, description="Offset for pagination"),
) -> list[TutorInteractionItem]:
    """Retrieve interaction history enriched with Knowledge Graph Subgraph metadata."""
    try:
        with graph_connection() as conn:
            with conn.cursor() as cur:
                cur.execute(
                    f"""
                    SELECT 
                        l.id::text AS id,
                        l.student_id::text AS student_id,
                        l.question_id::text AS question_id,
                        l.archetype_pattern_id::text AS archetype_pattern_id,
                        l.student_selected_option,
                        l.correct_option,
                        l.matched_trap_id::text AS matched_trap_id,
                        l.ai_guidance_transcript,
                        l.validation_status,
                        l.student_reported,
                        l.mentor_resolution,
                        to_char(l.created_at, 'YYYY-MM-DD"T"HH24:MI:SS"Z"') AS created_at,
                        p.pattern_code,
                        p.pattern_name,
                        p.core_theory_latex,
                        t.trap_code,
                        t.trap_name,
                        t.misconception_explanation
                    FROM {GRAPH_SCHEMA}.ai_tutor_interaction_logs l
                    LEFT JOIN {GRAPH_SCHEMA}.archetype_patterns p ON l.archetype_pattern_id = p.id
                    LEFT JOIN {GRAPH_SCHEMA}.pattern_traps t ON l.matched_trap_id = t.id
                    WHERE (%s = FALSE OR l.student_reported = TRUE)
                    ORDER BY l.created_at DESC
                    LIMIT %s OFFSET %s;
                    """,
                    (reported_only, limit, offset),
                )
                rows = cur.fetchall()
                results = []
                for r in rows:
                    transcript_raw = r["ai_guidance_transcript"]
                    if isinstance(transcript_raw, str):
                        try:
                            transcript_list = json.loads(transcript_raw)
                        except Exception:
                            transcript_list = []
                    elif isinstance(transcript_raw, list):
                        transcript_list = transcript_raw
                    else:
                        transcript_list = []

                    results.append(
                        TutorInteractionItem(
                            id=r["id"],
                            student_id=r["student_id"],
                            question_id=r["question_id"],
                            archetype_pattern_id=r["archetype_pattern_id"],
                            student_selected_option=r["student_selected_option"],
                            correct_option=r["correct_option"],
                            matched_trap_id=r["matched_trap_id"],
                            ai_guidance_transcript=transcript_list,
                            validation_status=r["validation_status"] or "PASSED",
                            student_reported=bool(r["student_reported"]),
                            mentor_resolution=r["mentor_resolution"],
                            created_at=r["created_at"] or "",
                            pattern_code=r["pattern_code"],
                            pattern_name=r["pattern_name"],
                            core_theory_latex=r["core_theory_latex"],
                            trap_code=r["trap_code"],
                            trap_name=r["trap_name"],
                            misconception_explanation=r["misconception_explanation"],
                        )
                    )
                return results
    except Exception as exc:
        logger.exception("Failed to query interaction logs")
        raise HTTPException(status_code=500, detail=f"Database error querying interactions: {exc}") from exc


@router.post(
    "/interactions/{interaction_id}/override-exemplar",
    response_model=OverrideExemplarResponse,
    summary="Mentor Action 1: Override solution and save as golden PatternExemplar",
)
def override_exemplar(
    interaction_id: UUID,
    req: OverrideExemplarRequest,
) -> OverrideExemplarResponse:
    """Save an approved mentor solution as a new golden exemplar in the Knowledge Graph."""
    try:
        with graph_connection() as conn:
            with conn.cursor() as cur:
                # 1. Fetch interaction details
                cur.execute(
                    f"""
                    SELECT id, archetype_pattern_id, question_id, correct_option, ai_guidance_transcript
                    FROM {GRAPH_SCHEMA}.ai_tutor_interaction_logs
                    WHERE id = %s::uuid;
                    """,
                    (str(interaction_id),),
                )
                log_row = cur.fetchone()
                if not log_row:
                    raise HTTPException(status_code=404, detail=f"Interaction log {interaction_id} not found")

                target_archetype_id = (
                    str(req.archetype_pattern_id)
                    if req.archetype_pattern_id
                    else (str(log_row["archetype_pattern_id"]) if log_row["archetype_pattern_id"] else None)
                )
                if not target_archetype_id:
                    raise HTTPException(
                        status_code=400,
                        detail="Cannot attach exemplar: no archetype_pattern_id specified or found in log",
                    )

                question_latex = req.question_latex or f"Question {log_row['question_id']}"
                correct_opt = req.correct_option or log_row["correct_option"] or "A"

                # 2. Insert into pattern_exemplars
                cur.execute(
                    f"""
                    INSERT INTO {GRAPH_SCHEMA}.pattern_exemplars
                    (archetype_pattern_id, question_latex, golden_solution, correct_option, explanation_steps)
                    VALUES (%s::uuid, %s, %s, %s, %s::jsonb)
                    RETURNING id::text AS id;
                    """,
                    (
                        target_archetype_id,
                        question_latex,
                        req.golden_solution,
                        correct_opt,
                        json.dumps(req.explanation_steps, ensure_ascii=False),
                    ),
                )
                exemplar_row = cur.fetchone()
                exemplar_id = exemplar_row["id"] if exemplar_row else str(uuid4())

                # 3. Update interaction log
                cur.execute(
                    f"""
                    UPDATE {GRAPH_SCHEMA}.ai_tutor_interaction_logs
                    SET mentor_resolution = 'OVERRIDDEN_EXEMPLAR_SAVED'
                    WHERE id = %s::uuid;
                    """,
                    (str(interaction_id),),
                )
                conn.commit()

                return OverrideExemplarResponse(
                    exemplar_id=exemplar_id,
                    archetype_pattern_id=target_archetype_id,
                    message="Lời giải mẫu chuẩn đã được lưu vào Knowledge Graph thành công.",
                )
    except HTTPException:
        raise
    except Exception as exc:
        logger.exception("Failed to override exemplar for %s", interaction_id)
        raise HTTPException(status_code=500, detail=f"Database error overriding exemplar: {exc}") from exc


@router.post(
    "/interactions/{interaction_id}/add-trap",
    response_model=AddNewTrapResponse,
    summary="Mentor Action 2: Add novel cognitive trap to Knowledge Graph",
)
def add_new_trap(
    interaction_id: UUID,
    req: AddNewTrapRequest,
) -> AddNewTrapResponse:
    """Register a new student misconception/trap identified by the mentor."""
    try:
        with graph_connection() as conn:
            with conn.cursor() as cur:
                # 1. Fetch interaction details
                cur.execute(
                    f"""
                    SELECT id, archetype_pattern_id, student_selected_option
                    FROM {GRAPH_SCHEMA}.ai_tutor_interaction_logs
                    WHERE id = %s::uuid;
                    """,
                    (str(interaction_id),),
                )
                log_row = cur.fetchone()
                if not log_row:
                    raise HTTPException(status_code=404, detail=f"Interaction log {interaction_id} not found")

                target_archetype_id = (
                    str(req.archetype_pattern_id)
                    if req.archetype_pattern_id
                    else (str(log_row["archetype_pattern_id"]) if log_row["archetype_pattern_id"] else None)
                )
                if not target_archetype_id:
                    raise HTTPException(
                        status_code=400,
                        detail="Cannot attach trap: no archetype_pattern_id specified or found in log",
                    )

                trap_code = req.trap_code or f"TRAP_MENTOR_{uuid4().hex[:8].upper()}"
                wrong_opt = req.wrong_option or log_row["student_selected_option"] or "B"

                # 2. Insert into pattern_traps
                cur.execute(
                    f"""
                    INSERT INTO {GRAPH_SCHEMA}.pattern_traps
                    (archetype_pattern_id, trap_code, trap_name, wrong_option, misconception_explanation, socratic_hint)
                    VALUES (%s::uuid, %s, %s, %s, %s, %s)
                    RETURNING id::text AS id, trap_code;
                    """,
                    (
                        target_archetype_id,
                        trap_code,
                        req.trap_name,
                        wrong_opt,
                        req.misconception_explanation,
                        req.socratic_hint,
                    ),
                )
                trap_row = cur.fetchone()
                trap_id = trap_row["id"] if trap_row else str(uuid4())

                # 3. Update interaction log
                cur.execute(
                    f"""
                    UPDATE {GRAPH_SCHEMA}.ai_tutor_interaction_logs
                    SET mentor_resolution = 'NEW_TRAP_ADDED'
                    WHERE id = %s::uuid;
                    """,
                    (str(interaction_id),),
                )
                conn.commit()

                return AddNewTrapResponse(
                    trap_id=trap_id,
                    trap_code=trap_code,
                    archetype_pattern_id=target_archetype_id,
                    message=f"Bẫy tư duy mới ({trap_code}) đã được bổ sung vào Knowledge Graph thành công.",
                )
    except HTTPException:
        raise
    except Exception as exc:
        logger.exception("Failed to add new trap for %s", interaction_id)
        raise HTTPException(status_code=500, detail=f"Database error adding trap: {exc}") from exc

