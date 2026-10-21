"""
graph_db.py — Knowledge Graph Database Access Layer
====================================================
Shared, lightweight helpers for the GraphRAG modules:
- Resolve the Supabase PostgreSQL connection string (schema ``v_eval_ai``).
- Open synchronous psycopg connections (cross-platform, no event-loop issues on Windows).
- Serialize Python float lists into pgvector literals.
- Provide a cached Gemini embedding client (3072-dim ``gemini-embedding-001``).
"""

from __future__ import annotations

import os
from contextlib import contextmanager
from functools import lru_cache
from typing import Iterator, Sequence

import psycopg
from dotenv import load_dotenv
from psycopg.rows import dict_row

_RAG_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(_RAG_ROOT, ".env"))

GRAPH_SCHEMA: str = "v_eval_ai"
EMBEDDING_MODEL: str = "models/gemini-embedding-001"
EMBEDDING_DIMENSIONS: int = 3072


def get_graph_connection_uri() -> str:
    """Return a libpq URI for the Knowledge Graph database.

    Priority: ``GRAPH_DATABASE_URL`` > ``SUPABASE_DATABASE_URL`` > ``DATABASE_URL``.
    SQLAlchemy-style prefixes (``postgresql+psycopg://``) are normalized.
    """
    uri = (
        os.environ.get("GRAPH_DATABASE_URL")
        or os.environ.get("SUPABASE_DATABASE_URL")
        or os.environ.get("DATABASE_URL")
        or ""
    )
    if not uri:
        raise EnvironmentError(
            "No database URL configured. Set GRAPH_DATABASE_URL or DATABASE_URL in rag-service/.env."
        )
    return uri.replace("postgresql+psycopg://", "postgresql://")


@contextmanager
def graph_connection(autocommit: bool = False) -> Iterator[psycopg.Connection]:
    """Open a synchronous psycopg connection returning rows as dicts."""
    conn = psycopg.connect(
        get_graph_connection_uri(),
        autocommit=autocommit,
        row_factory=dict_row,
        prepare_threshold=None,  # Supabase pooler (pgbouncer) does not support prepared statements
        connect_timeout=15,
    )
    try:
        yield conn
    finally:
        conn.close()


def to_pgvector(values: Sequence[float]) -> str:
    """Serialize a float sequence into a pgvector literal: ``[0.1,0.2,...]``."""
    if not values:
        raise ValueError("Cannot serialize an empty vector.")
    return "[" + ",".join(f"{float(v):.8f}" for v in values) + "]"


@lru_cache(maxsize=1)
def get_graph_embeddings():
    """Return a cached Gemini embedding client (3072 dimensions)."""
    from langchain_google_genai import GoogleGenerativeAIEmbeddings

    api_key = os.environ.get("GOOGLE_API_KEY", "")
    if not api_key:
        raise EnvironmentError("GOOGLE_API_KEY is not set in rag-service/.env.")
    return GoogleGenerativeAIEmbeddings(model=EMBEDDING_MODEL, google_api_key=api_key)
