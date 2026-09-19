"""
config.py — Configuration & Model Initialization
==================================================
Central configuration module for the Conversational RAG system.

Responsibilities:
- Load environment variables (.env) for API keys and DB connection.
- Initialize Google Gemini LLM and Embedding models.
- Provide a PGEngine and PGVectorStore factory for pgvector access.
"""

from __future__ import annotations

import asyncio
import os
import sys
import logging
from functools import lru_cache

# Fix for Windows: psycopg async requires WindowsSelectorEventLoopPolicy
if sys.platform == "win32":
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

from dotenv import load_dotenv
from langchain_google_genai import ChatGoogleGenerativeAI, GoogleGenerativeAIEmbeddings
from langchain_postgres import PGEngine, PGVectorStore

# ---------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------
load_dotenv()  # reads .env in project root

logger = logging.getLogger(__name__)

# Required env vars
GOOGLE_API_KEY: str = os.environ.get("GOOGLE_API_KEY", "")
DATABASE_URL: str = os.environ.get("DATABASE_URL", "")

if not GOOGLE_API_KEY:
    raise EnvironmentError(
        "GOOGLE_API_KEY is not set. "
        "Copy .env.example → .env and fill in your Google API key."
    )
if not DATABASE_URL:
    raise EnvironmentError(
        "DATABASE_URL is not set. "
        "Copy .env.example → .env and provide a PostgreSQL+psycopg connection string."
    )

# Server settings
API_HOST: str = os.environ.get("API_HOST", "0.0.0.0")
API_PORT: int = int(os.environ.get("API_PORT", "8000"))

# ---------------------------------------------------------------------------
# Model Constants
# ---------------------------------------------------------------------------
LLM_MODEL: str = "gemini-3.5-flash"
EMBEDDING_MODEL: str = "models/gemini-embedding-001"
EMBEDDING_DIMENSIONS: int = 3072  # gemini-embedding-001 default output size

# ---------------------------------------------------------------------------
# Chunking Constants
# ---------------------------------------------------------------------------
CHUNK_SIZE: int = 1000
CHUNK_OVERLAP: int = 200

# ---------------------------------------------------------------------------
# Database & Table Constants
# ---------------------------------------------------------------------------
CATALOG_TABLE_NAME: str = "documents"
COLLECTION_NAME: str = "document_chunks"


def get_connection_string() -> str:
    """Return standard libpq connection string for psycopg."""
    return DATABASE_URL.replace("postgresql+psycopg://", "postgresql://")

# ---------------------------------------------------------------------------
# Model Factories (cached singletons)
# ---------------------------------------------------------------------------


@lru_cache(maxsize=1)
def get_llm() -> ChatGoogleGenerativeAI:
    """Return a cached ChatGoogleGenerativeAI instance.

    Uses ``gemini-3.5-flash`` with temperature=0 for deterministic,
    context-grounded answers.
    """
    logger.info("Initializing LLM: %s", LLM_MODEL)
    return ChatGoogleGenerativeAI(
        model=LLM_MODEL,
        api_key=GOOGLE_API_KEY,
        temperature=0,
    )


@lru_cache(maxsize=1)
def get_embeddings() -> GoogleGenerativeAIEmbeddings:
    """Return a cached GoogleGenerativeAIEmbeddings instance.

    Uses ``models/gemini-embedding-001`` which outputs 3072-dim vectors.
    """
    logger.info("Initializing Embeddings: %s", EMBEDDING_MODEL)
    return GoogleGenerativeAIEmbeddings(
        model=EMBEDDING_MODEL,
        api_key=GOOGLE_API_KEY,
    )


# ---------------------------------------------------------------------------
# Database / Vector Store Factories
# ---------------------------------------------------------------------------


@lru_cache(maxsize=1)
def get_pg_engine() -> PGEngine:
    """Create and cache a PGEngine connection pool from DATABASE_URL.

    Raises
    ------
    ConnectionError
        If the database is unreachable.
    """
    logger.info("Creating PGEngine from DATABASE_URL")
    try:
        engine = PGEngine.from_connection_string(url=DATABASE_URL)
        return engine
    except Exception as exc:
        raise ConnectionError(
            f"Failed to connect to PostgreSQL: {exc}"
        ) from exc


def init_catalog_tables() -> None:
    """Ensure catalog and vector extension exist."""
    import psycopg

    conn_str = get_connection_string()
    try:
        with psycopg.connect(conn_str) as conn:
            with conn.cursor() as cur:
                cur.execute("CREATE EXTENSION IF NOT EXISTS vector;")
                cur.execute(f"""
                    CREATE TABLE IF NOT EXISTS {CATALOG_TABLE_NAME} (
                        id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                        document_group_id UUID NOT NULL,
                        file_name VARCHAR(255) NOT NULL,
                        version INT NOT NULL DEFAULT 1,
                        file_hash VARCHAR(64) NOT NULL,
                        status VARCHAR(50) DEFAULT 'PENDING',
                        is_current BOOLEAN DEFAULT FALSE,
                        total_chunks INT DEFAULT 0,
                        error_message TEXT,
                        created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
                        updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS idx_docs_group_current 
                        ON {CATALOG_TABLE_NAME}(document_group_id, is_current);
                    CREATE INDEX IF NOT EXISTS idx_docs_filename 
                        ON {CATALOG_TABLE_NAME}(file_name);
                """)
                conn.commit()
                logger.info("Catalog table '%s' is ready.", CATALOG_TABLE_NAME)
    except Exception as exc:
        logger.error("Failed to initialize catalog table '%s': %s", CATALOG_TABLE_NAME, exc)
        raise


def init_vector_store_table() -> None:
    """Ensure the pgvector table and catalog table exist."""
    init_catalog_tables()
    engine = get_pg_engine()
    try:
        engine.init_vectorstore_table(
            table_name=COLLECTION_NAME,
            vector_size=EMBEDDING_DIMENSIONS,
        )
        logger.info("Created vector store table '%s'", COLLECTION_NAME)
    except Exception as exc:
        # Table already exists — that's fine.
        if "42P07" in str(exc) or "already exists" in str(exc).lower():
            logger.debug("Vector store table '%s' already exists.", COLLECTION_NAME)
        else:
            raise


def get_vector_store() -> PGVectorStore:
    """Return a ready-to-use PGVectorStore instance.

    Ensures the underlying table exists before returning.
    """
    init_vector_store_table()
    engine = get_pg_engine()
    return PGVectorStore.create_sync(
        engine=engine,
        table_name=COLLECTION_NAME,
        embedding_service=get_embeddings(),
    )
