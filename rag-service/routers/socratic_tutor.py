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
from uuid import UUID

from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse
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
