"""
ingestion.py — Document Ingestion Pipeline with Versioning & Graceful Swap
==========================================================================
Handles loading, chunking, SHA-256 hash checking, background processing,
and zero-downtime graceful version swap in PostgreSQL + pgvector.
"""

from __future__ import annotations

import hashlib
import logging
import os
import shutil
import uuid
from typing import Any, Literal

import psycopg
from psycopg.rows import dict_row
from langchain_community.document_loaders import Docx2txtLoader, PyPDFLoader
from langchain_core.documents import Document
from langchain_text_splitters import RecursiveCharacterTextSplitter

from config import (
    CATALOG_TABLE_NAME,
    CHUNK_OVERLAP,
    CHUNK_SIZE,
    COLLECTION_NAME,
    get_connection_string,
    get_vector_store,
)

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Text Splitter (module-level, reusable)
# ---------------------------------------------------------------------------
_text_splitter = RecursiveCharacterTextSplitter(
    chunk_size=CHUNK_SIZE,
    chunk_overlap=CHUNK_OVERLAP,
    length_function=len,
    is_separator_regex=False,
)


# ---------------------------------------------------------------------------
# Document Loaders
# ---------------------------------------------------------------------------


def _load_pdf(file_path: str) -> list[Document]:
    """Load pages from a PDF file."""
    if not os.path.isfile(file_path):
        raise FileNotFoundError(f"PDF file not found: {file_path}")
    try:
        loader = PyPDFLoader(file_path)
        documents = loader.load()
        logger.info("Loaded %d page(s) from PDF: %s", len(documents), file_path)
        return documents
    except Exception as exc:
        raise ValueError(f"Failed to parse PDF '{file_path}': {exc}") from exc


def _load_docx(file_path: str) -> list[Document]:
    """Load content from a DOCX file."""
    if not os.path.isfile(file_path):
        raise FileNotFoundError(f"DOCX file not found: {file_path}")
    try:
        loader = Docx2txtLoader(file_path)
        documents = loader.load()
        logger.info("Loaded DOCX document: %s", file_path)
        return documents
    except Exception as exc:
        raise ValueError(f"Failed to parse DOCX '{file_path}': {exc}") from exc


def _load_text(content: str) -> list[Document]:
    """Wrap a plain-text string into a LangChain Document."""
    if not content or not content.strip():
        raise ValueError("Text content is empty.")
    doc = Document(
        page_content=content.strip(),
        metadata={"source": "user_input", "type": "text"},
    )
    return [doc]


_LOADERS = {
    "pdf": _load_pdf,
    "docx": _load_docx,
    "text": _load_text,
}


# ---------------------------------------------------------------------------
# Hash Calculation & Version Check
# ---------------------------------------------------------------------------


def calculate_sha256(content_bytes: bytes) -> str:
    """Calculate SHA-256 checksum of raw content bytes."""
    return hashlib.sha256(content_bytes).hexdigest()


def check_existing_document(file_name: str, file_hash: str) -> dict[str, Any]:
    """Inspect the catalog to determine whether to skip, create v1, or create a new version.

    Returns
    -------
    dict with keys:
        - action: "UNCHANGED" | "NEW" | "NEW_VERSION"
        - document_id: UUID str (existing or newly generated)
        - document_group_id: UUID str
        - version: int
    """
    conn_str = get_connection_string()
    with psycopg.connect(conn_str, row_factory=dict_row) as conn:
        with conn.cursor() as cur:
            # Check if an active version exists
            cur.execute(
                f"""
                SELECT id, document_group_id, version, file_hash, status, is_current
                FROM {CATALOG_TABLE_NAME}
                WHERE file_name = %s AND is_current = TRUE
                ORDER BY version DESC
                LIMIT 1;
                """,
                (file_name,),
            )
            active_doc = cur.fetchone()

            if active_doc:
                # Compare SHA-256 hash
                if active_doc["file_hash"] == file_hash:
                    return {
                        "action": "UNCHANGED",
                        "document_id": str(active_doc["id"]),
                        "document_group_id": str(active_doc["document_group_id"]),
                        "version": active_doc["version"],
                        "message": f"Document '{file_name}' content is identical to active version {active_doc['version']}. Skipping.",
                    }
                else:
                    # New version needed
                    new_doc_id = str(uuid.uuid4())
                    new_version = active_doc["version"] + 1
                    return {
                        "action": "NEW_VERSION",
                        "document_id": new_doc_id,
                        "document_group_id": str(active_doc["document_group_id"]),
                        "version": new_version,
                        "message": f"New version {new_version} detected for '{file_name}'.",
                    }

            # If no active doc, check if any previous version exists (e.g. soft-deleted)
            cur.execute(
                f"""
                SELECT document_group_id, MAX(version) as max_version
                FROM {CATALOG_TABLE_NAME}
                WHERE file_name = %s
                GROUP BY document_group_id
                ORDER BY max_version DESC
                LIMIT 1;
                """,
                (file_name,),
            )
            prev_doc = cur.fetchone()

            if prev_doc:
                new_doc_id = str(uuid.uuid4())
                new_version = prev_doc["max_version"] + 1
                return {
                    "action": "NEW_VERSION",
                    "document_id": new_doc_id,
                    "document_group_id": str(prev_doc["document_group_id"]),
                    "version": new_version,
                    "message": f"Creating version {new_version} for '{file_name}'.",
                }

            # Completely new document
            new_doc_id = str(uuid.uuid4())
            new_group_id = str(uuid.uuid4())
            return {
                "action": "NEW",
                "document_id": new_doc_id,
                "document_group_id": new_group_id,
                "version": 1,
                "message": f"Creating initial version (v1) for '{file_name}'.",
            }


def register_pending_document(
    document_id: str,
    document_group_id: str,
    file_name: str,
    version: int,
    file_hash: str,
) -> None:
    """Insert a PENDING/PROCESSING record into the documents catalog."""
    conn_str = get_connection_string()
    with psycopg.connect(conn_str) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                INSERT INTO {CATALOG_TABLE_NAME} 
                    (id, document_group_id, file_name, version, file_hash, status, is_current)
                VALUES 
                    (%s, %s, %s, %s, %s, 'PROCESSING', FALSE);
                """,
                (document_id, document_group_id, file_name, version, file_hash),
            )
            conn.commit()


# ---------------------------------------------------------------------------
# Background Ingestion & Graceful Swap
# ---------------------------------------------------------------------------


def process_document_background(
    document_id: str,
    document_group_id: str,
    file_name: str,
    version: int,
    source: str,
    doc_type: Literal["pdf", "docx", "text"],
    cleanup_path: str | None = None,
) -> None:
    """Run chunking, embedding, vector storage, and zero-downtime graceful swap.

    Executed in a background thread by FastAPI BackgroundTasks.
    """
    conn_str = get_connection_string()
    logger.info("Starting background processing for '%s' (v%d, id=%s)...", file_name, version, document_id)

    try:
        # 1. Load documents
        loader_fn = _LOADERS[doc_type]
        raw_documents = loader_fn(source)
        if not raw_documents:
            raise ValueError(f"No content could be extracted from '{file_name}'.")

        # 2. Split chunks
        chunks = _text_splitter.split_documents(raw_documents)
        if not chunks:
            raise ValueError(f"Document produced 0 chunks: '{file_name}'.")

        # 3. Enrich chunk metadata
        for idx, chunk in enumerate(chunks):
            chunk.metadata["document_id"] = str(document_id)
            chunk.metadata["document_group_id"] = str(document_group_id)
            chunk.metadata["file_name"] = file_name
            chunk.metadata["version"] = version
            chunk.metadata["is_current"] = False  # Set to False until graceful swap
            chunk.metadata["source"] = file_name
            chunk.metadata["type"] = doc_type
            chunk.metadata["chunk_index"] = idx

        # 4. Generate embeddings and store into vector database
        logger.info("Generating embeddings for %d chunk(s) of '%s' (v%d)...", len(chunks), file_name, version)
        vector_store = get_vector_store()
        vector_store.add_documents(chunks)
        logger.info("Successfully stored %d chunk(s) in pgvector.", len(chunks))

        # 5. Graceful Swap Database Transaction
        with psycopg.connect(conn_str) as conn:
            with conn.cursor() as cur:
                # 5A. De-activate old active version in catalog
                cur.execute(
                    f"""
                    UPDATE {CATALOG_TABLE_NAME}
                    SET is_current = FALSE, updated_at = NOW()
                    WHERE document_group_id = %s AND is_current = TRUE;
                    """,
                    (document_group_id,),
                )

                # 5B. De-activate old active chunks in vector table
                cur.execute(
                    f"""
                    UPDATE {COLLECTION_NAME}
                    SET langchain_metadata = jsonb_set(langchain_metadata::jsonb, '{{is_current}}', 'false'::jsonb)::json
                    WHERE langchain_metadata->>'document_group_id' = %s;
                    """,
                    (document_group_id,),
                )

                # 5C. Activate new document version in catalog
                cur.execute(
                    f"""
                    UPDATE {CATALOG_TABLE_NAME}
                    SET is_current = TRUE, status = 'READY', total_chunks = %s, updated_at = NOW()
                    WHERE id = %s;
                    """,
                    (len(chunks), document_id),
                )

                # 5D. Activate new chunks in vector table
                cur.execute(
                    f"""
                    UPDATE {COLLECTION_NAME}
                    SET langchain_metadata = jsonb_set(langchain_metadata::jsonb, '{{is_current}}', 'true'::jsonb)::json
                    WHERE langchain_metadata->>'document_id' = %s;
                    """,
                    (document_id,),
                )
                conn.commit()

        logger.info("Graceful swap completed: '%s' (v%d) is now ACTIVE.", file_name, version)

    except Exception as exc:
        logger.error("Background ingestion failed for '%s' (id=%s): %s", file_name, document_id, exc)
        try:
            with psycopg.connect(conn_str) as conn:
                with conn.cursor() as cur:
                    cur.execute(
                        f"""
                        UPDATE {CATALOG_TABLE_NAME}
                        SET status = 'FAILED', error_message = %s, updated_at = NOW()
                        WHERE id = %s;
                        """,
                        (str(exc), document_id),
                    )
                    conn.commit()
        except Exception as db_exc:
            logger.error("Failed to record FAILED status: %s", db_exc)
    finally:
        if cleanup_path and os.path.exists(cleanup_path):
            try:
                if os.path.isdir(cleanup_path):
                    shutil.rmtree(cleanup_path, ignore_errors=True)
                else:
                    os.remove(cleanup_path)
            except Exception as clean_exc:
                logger.warning("Failed to clean up temp file '%s': %s", cleanup_path, clean_exc)
