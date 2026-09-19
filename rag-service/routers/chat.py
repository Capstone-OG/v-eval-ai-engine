"""
routers/chat.py — Conversational Chat Endpoints
================================================
Handles question answering with context retrieval, conversational memory,
and token streaming.
"""

from __future__ import annotations

import json
import logging
from typing import Iterator

from fastapi import APIRouter, HTTPException, status
from fastapi.responses import StreamingResponse

from rag_engine import ask, ask_stream, clear_session, get_session_ids
from schemas import (
    ChatRequest,
    ChatResponse,
    DocumentSource,
    SessionClearResponse,
    SessionListResponse,
)

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/v1/chat", tags=["Conversational Chat"])


@router.post(
    "",
    response_model=ChatResponse,
    summary="Ask a question with conversational memory",
    description=(
        "Sends a question to the Conversational RAG pipeline. "
        "The system reformulates the question based on session chat history, "
        "retrieves relevant knowledge from pgvector, and generates a grounded response."
    ),
)
def chat_endpoint(payload: ChatRequest) -> ChatResponse:
    try:
        result = ask(question=payload.question, session_id=payload.session_id)
        answer = result.get("answer", "")
        raw_sources = result.get("context", [])

        sources = [
            DocumentSource(
                source=doc.metadata.get("source", "unknown"),
                file_name=doc.metadata.get("file_name", doc.metadata.get("source")),
                version=doc.metadata.get("version"),
                document_id=doc.metadata.get("document_id"),
                doc_type=doc.metadata.get("type"),
                page=doc.metadata.get("page"),
                chunk_index=doc.metadata.get("chunk_index"),
                content_snippet=doc.page_content[:250].strip().replace("\n", " "),
            )
            for doc in raw_sources
        ]

        return ChatResponse(
            session_id=payload.session_id,
            question=payload.question,
            answer=answer,
            sources=sources,
        )
    except Exception as exc:
        logger.error("Chat invocation failed: %s", exc)
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"Chat processing failed: {exc}",
        ) from exc


@router.post(
    "/stream",
    summary="Stream conversational answer token-by-token",
    description="Streams the AI response as Server-Sent Events (SSE).",
)
def chat_stream_endpoint(payload: ChatRequest):
    def event_generator() -> Iterator[str]:
        try:
            for token in ask_stream(question=payload.question, session_id=payload.session_id):
                data = json.dumps({"token": token}, ensure_ascii=False)
                yield f"data: {data}\n\n"
            yield "data: [DONE]\n\n"
        except Exception as exc:
            logger.error("Streaming error: %s", exc)
            err_data = json.dumps({"error": str(exc)}, ensure_ascii=False)
            yield f"data: {err_data}\n\n"

    return StreamingResponse(
        event_generator(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "Content-Type": "text/event-stream",
        },
    )


@router.get(
    "/sessions",
    response_model=SessionListResponse,
    summary="List active conversation sessions",
)
def list_sessions_endpoint() -> SessionListResponse:
    return SessionListResponse(active_sessions=get_session_ids())


@router.delete(
    "/sessions/{session_id}",
    response_model=SessionClearResponse,
    summary="Clear conversation history for a session",
)
def clear_session_endpoint(session_id: str) -> SessionClearResponse:
    clear_session(session_id)
    return SessionClearResponse(
        session_id=session_id,
        message=f"Session '{session_id}' chat history cleared successfully.",
    )
