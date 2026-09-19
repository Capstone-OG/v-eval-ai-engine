"""
main.py — FastAPI Application Entrypoint
==========================================
Main REST API service for Conversational RAG with Google Gemini and pgvector.
"""

from __future__ import annotations

import logging
from contextlib import asynccontextmanager

import psycopg
import uvicorn
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import RedirectResponse

from config import (
    API_HOST,
    API_PORT,
    CATALOG_TABLE_NAME,
    COLLECTION_NAME,
    DATABASE_URL,
    EMBEDDING_MODEL,
    LLM_MODEL,
    init_vector_store_table,
)
from routers.chat import router as chat_router
from routers.documents import router as documents_router
from schemas import HealthResponse

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s | %(levelname)s | %(name)s | %(message)s",
)
logger = logging.getLogger("v-act-ai-service")


# ---------------------------------------------------------------------------
# Lifespan Event Handler
# ---------------------------------------------------------------------------


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifecycle manager: ensure vector store table exists on startup."""
    logger.info("Starting up V-Act AI Service...")
    try:
        init_vector_store_table()
        logger.info("PostgreSQL pgvector collection '%s' ready.", COLLECTION_NAME)
    except Exception as exc:
        logger.error("Failed to initialize vector store table: %s", exc)

    yield

    logger.info("Shutting down V-Act AI Service...")


# ---------------------------------------------------------------------------
# FastAPI App Initialization
# ---------------------------------------------------------------------------

app = FastAPI(
    title="V-Act AI Service — Conversational RAG API",
    description=(
        "Production-ready Conversational RAG backend powered by LangChain (LCEL), "
        "Google Gemini (LLM & Embeddings), and PostgreSQL + pgvector."
    ),
    version="1.0.0",
    lifespan=lifespan,
    docs_url="/docs",
    redoc_url="/redoc",
)

# ---------------------------------------------------------------------------
# CORS Middleware
# ---------------------------------------------------------------------------

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Open for development; adjust for production origins
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# ---------------------------------------------------------------------------
# Routers Mounting
# ---------------------------------------------------------------------------

app.include_router(chat_router)
app.include_router(documents_router)


# ---------------------------------------------------------------------------
# Root & Health Endpoints
# ---------------------------------------------------------------------------


@app.get("/", include_in_schema=False)
def root_redirect():
    """Redirect root path to interactive Swagger documentation."""
    return RedirectResponse(url="/docs")


@app.get(
    "/health",
    response_model=HealthResponse,
    tags=["System"],
    summary="Health check & system status",
)
def health_check() -> HealthResponse:
    """Verify database connectivity and return system status."""
    db_status = "connected"
    total_docs = 0
    active_chunks = 0
    conn_str = DATABASE_URL.replace("postgresql+psycopg://", "postgresql://")

    try:
        with psycopg.connect(conn_str) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    f"SELECT count(*) FROM {COLLECTION_NAME} WHERE (langchain_metadata->>'is_current')::boolean = true;"
                )
                active_chunks = cur.fetchone()[0]
                cur.execute(
                    f"SELECT count(DISTINCT document_group_id) FROM {CATALOG_TABLE_NAME};"
                )
                total_docs = cur.fetchone()[0]
    except Exception as exc:
        logger.warning("Database health check failed: %s", exc)
        db_status = f"disconnected: {exc}"

    overall_status = "healthy" if db_status == "connected" else "degraded"

    return HealthResponse(
        status=overall_status,
        database=db_status,
        llm_model=LLM_MODEL,
        embedding_model=EMBEDDING_MODEL,
        total_active_chunks=active_chunks,
        total_documents=total_docs,
    )


# ---------------------------------------------------------------------------
# Direct Execution Launcher
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    uvicorn.run(
        "main:app",
        host=API_HOST,
        port=API_PORT,
        reload=True,
    )
