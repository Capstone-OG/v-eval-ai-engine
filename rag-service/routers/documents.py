"""
routers/documents.py — Document Ingestion & Versioning Management
==================================================================
Handles document uploads with SHA-256 duplicate checking, background ingestion,
graceful zero-downtime swap, version history, rollback, and soft/hard deletes.
"""

from __future__ import annotations

import logging
import os
import tempfile
from pathlib import Path

import psycopg
from psycopg.rows import dict_row
from fastapi import APIRouter, BackgroundTasks, File, HTTPException, UploadFile, status

from config import (
    CATALOG_TABLE_NAME,
    COLLECTION_NAME,
    EMBEDDING_DIMENSIONS,
    get_connection_string,
)
from ingestion import (
    calculate_sha256,
    check_existing_document,
    process_document_background,
    register_pending_document,
)
from schemas import (
    ActionResponse,
    DocStatsResponse,
    DocumentGroupItem,
    DocumentVersionItem,
    IngestResponse,
    RollbackRequest,
    TextIngestRequest,
)

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/v1/documents", tags=["Document Versioning & Management"])

ALLOWED_EXTENSIONS = {
    ".pdf": "pdf",
    ".docx": "docx",
}


@router.post(
    "/upload",
    response_model=IngestResponse,
    summary="Upload document with SHA-256 hash checking & versioning",
    description=(
        "Upload a PDF or DOCX file. The system checks the file's SHA-256 hash:\n"
        "- If the file is identical to the current active version, ingestion is skipped.\n"
        "- If it is new or modified, a new version is registered and processed in the background.\n"
        "- Old versions remain active serving RAG queries until the new version is READY (Graceful Swap)."
    ),
)
async def upload_document_endpoint(
    background_tasks: BackgroundTasks,
    file: UploadFile = File(..., description="PDF or DOCX document to upload"),
) -> IngestResponse:
    if not file.filename:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Uploaded file must have a filename.",
        )

    file_ext = Path(file.filename).suffix.lower()
    if file_ext not in ALLOWED_EXTENSIONS:
        allowed = ", ".join(ALLOWED_EXTENSIONS.keys())
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Unsupported format '{file_ext}'. Allowed formats: {allowed}",
        )

    doc_type = ALLOWED_EXTENSIONS[file_ext]

    # Read bytes and compute hash
    content_bytes = await file.read()
    if not content_bytes:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Uploaded file is empty.",
        )

    file_hash = calculate_sha256(content_bytes)

    # 3-step check against database catalog
    decision = check_existing_document(file_name=file.filename, file_hash=file_hash)

    if decision["action"] == "UNCHANGED":
        return IngestResponse(
            message=decision["message"],
            status="UNCHANGED",
            document_id=decision["document_id"],
            document_group_id=decision["document_group_id"],
            file_name=file.filename,
            version=decision["version"],
            file_hash=file_hash,
            chunks_ingested=0,
        )

    # NEW or NEW_VERSION -> Register in catalog as PROCESSING
    doc_id = decision["document_id"]
    group_id = decision["document_group_id"]
    version = decision["version"]

    register_pending_document(
        document_id=doc_id,
        document_group_id=group_id,
        file_name=file.filename,
        version=version,
        file_hash=file_hash,
    )

    # Save to temp file for background processing
    temp_dir = tempfile.mkdtemp(prefix="rag_upload_")
    temp_file_path = os.path.join(temp_dir, file.filename)
    with open(temp_file_path, "wb") as f:
        f.write(content_bytes)

    # Add background task
    background_tasks.add_task(
        process_document_background,
        document_id=doc_id,
        document_group_id=group_id,
        file_name=file.filename,
        version=version,
        source=temp_file_path,
        doc_type=doc_type,
        cleanup_path=temp_dir,
    )

    return IngestResponse(
        message=decision["message"] + " Processing chunks in background with Graceful Swap.",
        status="PROCESSING",
        document_id=doc_id,
        document_group_id=group_id,
        file_name=file.filename,
        version=version,
        file_hash=file_hash,
        chunks_ingested=0,
    )


@router.post(
    "/text",
    response_model=IngestResponse,
    summary="Ingest plain text with versioning & hash check",
    description="Ingest plain text content with title-based version grouping and SHA-256 hash comparison.",
)
def ingest_text_endpoint(
    payload: TextIngestRequest,
    background_tasks: BackgroundTasks,
) -> IngestResponse:
    content_bytes = payload.content.strip().encode("utf-8")
    if not content_bytes:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Text content cannot be empty.",
        )

    file_name = f"{payload.title}.txt"
    file_hash = calculate_sha256(content_bytes)

    decision = check_existing_document(file_name=file_name, file_hash=file_hash)

    if decision["action"] == "UNCHANGED":
        return IngestResponse(
            message=decision["message"],
            status="UNCHANGED",
            document_id=decision["document_id"],
            document_group_id=decision["document_group_id"],
            file_name=file_name,
            version=decision["version"],
            file_hash=file_hash,
            chunks_ingested=0,
        )

    doc_id = decision["document_id"]
    group_id = decision["document_group_id"]
    version = decision["version"]

    register_pending_document(
        document_id=doc_id,
        document_group_id=group_id,
        file_name=file_name,
        version=version,
        file_hash=file_hash,
    )

    background_tasks.add_task(
        process_document_background,
        document_id=doc_id,
        document_group_id=group_id,
        file_name=file_name,
        version=version,
        source=payload.content.strip(),
        doc_type="text",
        cleanup_path=None,
    )

    return IngestResponse(
        message=decision["message"] + " Processing chunks in background with Graceful Swap.",
        status="PROCESSING",
        document_id=doc_id,
        document_group_id=group_id,
        file_name=file_name,
        version=version,
        file_hash=file_hash,
        chunks_ingested=0,
    )


@router.get(
    "",
    response_model=list[DocumentGroupItem],
    summary="List all logical documents",
    description="Retrieve all logical document groups with active version info and version count.",
)
def list_documents_endpoint() -> list[DocumentGroupItem]:
    conn_str = get_connection_string()
    with psycopg.connect(conn_str, row_factory=dict_row) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT 
                    document_group_id,
                    file_name,
                    COUNT(*) as total_versions,
                    MAX(CASE WHEN is_current THEN version ELSE NULL END) as active_version,
                    MAX(CASE WHEN is_current THEN id::text ELSE NULL END) as active_document_id,
                    MAX(status) as status,
                    MAX(updated_at)::text as updated_at
                FROM {CATALOG_TABLE_NAME}
                GROUP BY document_group_id, file_name
                ORDER BY updated_at DESC;
                """
            )
            rows = cur.fetchall()
            return [
                DocumentGroupItem(
                    document_group_id=str(row["document_group_id"]),
                    file_name=row["file_name"],
                    active_version=row["active_version"],
                    active_document_id=row["active_document_id"],
                    total_versions=row["total_versions"],
                    status="ACTIVE" if row["active_version"] is not None else "INACTIVE",
                    updated_at=row["updated_at"],
                )
                for row in rows
            ]


@router.get(
    "/{document_group_id}/versions",
    response_model=list[DocumentVersionItem],
    summary="Get full version history for a logical document",
)
def get_document_versions_endpoint(document_group_id: str) -> list[DocumentVersionItem]:
    conn_str = get_connection_string()
    with psycopg.connect(conn_str, row_factory=dict_row) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT 
                    id::text as id,
                    document_group_id::text as document_group_id,
                    file_name,
                    version,
                    file_hash,
                    status,
                    is_current,
                    total_chunks,
                    created_at::text as created_at,
                    error_message
                FROM {CATALOG_TABLE_NAME}
                WHERE document_group_id = %s
                ORDER BY version DESC;
                """,
                (document_group_id,),
            )
            rows = cur.fetchall()
            if not rows:
                raise HTTPException(
                    status_code=status.HTTP_404_NOT_FOUND,
                    detail=f"No document found for group ID '{document_group_id}'.",
                )
            return [DocumentVersionItem(**row) for row in rows]


@router.post(
    "/{document_group_id}/rollback",
    response_model=ActionResponse,
    summary="Rollback a document to a previous version",
    description="Instantly switch RAG serving back to an earlier version without recalculating embeddings.",
)
def rollback_document_version_endpoint(
    document_group_id: str,
    payload: RollbackRequest,
) -> ActionResponse:
    conn_str = get_connection_string()
    with psycopg.connect(conn_str, row_factory=dict_row) as conn:
        with conn.cursor() as cur:
            # Verify target version exists and is READY
            cur.execute(
                f"""
                SELECT id, version, status 
                FROM {CATALOG_TABLE_NAME}
                WHERE document_group_id = %s AND version = %s;
                """,
                (document_group_id, payload.version),
            )
            target_doc = cur.fetchone()
            if not target_doc:
                raise HTTPException(
                    status_code=status.HTTP_404_NOT_FOUND,
                    detail=f"Version {payload.version} not found for document group '{document_group_id}'.",
                )
            if target_doc["status"] != "READY":
                raise HTTPException(
                    status_code=status.HTTP_400_BAD_REQUEST,
                    detail=f"Version {payload.version} cannot be restored because its status is '{target_doc['status']}'.",
                )

            target_id = str(target_doc["id"])

            # Transaction: swap active flags
            # 1. De-activate all versions in catalog
            cur.execute(
                f"""
                UPDATE {CATALOG_TABLE_NAME}
                SET is_current = FALSE, updated_at = NOW()
                WHERE document_group_id = %s;
                """,
                (document_group_id,),
            )

            # 2. De-activate all chunks in vector table
            cur.execute(
                f"""
                UPDATE {COLLECTION_NAME}
                SET langchain_metadata = jsonb_set(langchain_metadata::jsonb, '{{is_current}}', 'false'::jsonb)::json
                WHERE langchain_metadata->>'document_group_id' = %s;
                """,
                (document_group_id,),
            )

            # 3. Activate target version in catalog
            cur.execute(
                f"""
                UPDATE {CATALOG_TABLE_NAME}
                SET is_current = TRUE, updated_at = NOW()
                WHERE id = %s;
                """,
                (target_id,),
            )

            # 4. Activate target chunks in vector table
            cur.execute(
                f"""
                UPDATE {COLLECTION_NAME}
                SET langchain_metadata = jsonb_set(langchain_metadata::jsonb, '{{is_current}}', 'true'::jsonb)::json
                WHERE langchain_metadata->>'document_id' = %s;
                """,
                (target_id,),
            )
            conn.commit()

    logger.info("Rolled back group '%s' to version %d.", document_group_id, payload.version)
    return ActionResponse(
        message=f"Document successfully rolled back to version {payload.version}.",
        document_group_id=document_group_id,
        action="rollback",
        target_version=payload.version,
    )


@router.delete(
    "/{document_group_id}",
    response_model=ActionResponse,
    summary="Soft delete a document (de-activate from RAG)",
    description="De-activates all versions and chunks from RAG queries while keeping history in database.",
)
def soft_delete_document_endpoint(document_group_id: str) -> ActionResponse:
    conn_str = get_connection_string()
    with psycopg.connect(conn_str) as conn:
        with conn.cursor() as cur:
            # De-activate in catalog
            cur.execute(
                f"""
                UPDATE {CATALOG_TABLE_NAME}
                SET is_current = FALSE, updated_at = NOW()
                WHERE document_group_id = %s;
                """,
                (document_group_id,),
            )
            if cur.rowcount == 0:
                raise HTTPException(
                    status_code=status.HTTP_404_NOT_FOUND,
                    detail=f"Document group '{document_group_id}' not found.",
                )

            # De-activate chunks
            cur.execute(
                f"""
                UPDATE {COLLECTION_NAME}
                SET langchain_metadata = jsonb_set(langchain_metadata::jsonb, '{{is_current}}', 'false'::jsonb)::json
                WHERE langchain_metadata->>'document_group_id' = %s;
                """,
                (document_group_id,),
            )
            conn.commit()

    return ActionResponse(
        message="Document soft-deleted successfully. Chunks are now hidden from RAG queries.",
        document_group_id=document_group_id,
        action="soft_delete",
    )


@router.delete(
    "/{document_group_id}/purge",
    response_model=ActionResponse,
    summary="Permanently hard-delete a document and all its versions",
)
def purge_document_endpoint(document_group_id: str) -> ActionResponse:
    conn_str = get_connection_string()
    with psycopg.connect(conn_str) as conn:
        with conn.cursor() as cur:
            # Delete chunks
            cur.execute(
                f"""
                DELETE FROM {COLLECTION_NAME}
                WHERE langchain_metadata->>'document_group_id' = %s;
                """,
                (document_group_id,),
            )
            # Delete catalog
            cur.execute(
                f"""
                DELETE FROM {CATALOG_TABLE_NAME}
                WHERE document_group_id = %s;
                """,
                (document_group_id,),
            )
            if cur.rowcount == 0:
                raise HTTPException(
                    status_code=status.HTTP_404_NOT_FOUND,
                    detail=f"Document group '{document_group_id}' not found.",
                )
            conn.commit()

    return ActionResponse(
        message="Document and all associated vector chunks purged permanently.",
        document_group_id=document_group_id,
        action="purge",
    )


@router.get(
    "/stats",
    response_model=DocStatsResponse,
    summary="Get document catalog and vector store statistics",
)
def get_document_stats_endpoint() -> DocStatsResponse:
    conn_str = get_connection_string()
    try:
        with psycopg.connect(conn_str, row_factory=dict_row) as conn:
            with conn.cursor() as cur:
                # Vector chunks count
                cur.execute(f"SELECT count(*) as total FROM {COLLECTION_NAME};")
                total_chunks = cur.fetchone()["total"]

                cur.execute(
                    f"SELECT count(*) as total FROM {COLLECTION_NAME} WHERE (langchain_metadata->>'is_current')::boolean = true;"
                )
                active_chunks = cur.fetchone()["total"]

                # Catalog counts
                cur.execute(f"SELECT count(DISTINCT document_group_id) as total_docs FROM {CATALOG_TABLE_NAME};")
                total_docs = cur.fetchone()["total_docs"]

                cur.execute(f"SELECT count(*) as total_versions FROM {CATALOG_TABLE_NAME};")
                total_versions = cur.fetchone()["total_versions"]

                return DocStatsResponse(
                    total_chunks=total_chunks,
                    active_chunks=active_chunks,
                    total_logical_documents=total_docs,
                    total_versions=total_versions,
                    table_name=COLLECTION_NAME,
                    embedding_dimensions=EMBEDDING_DIMENSIONS,
                )
    except Exception as exc:
        logger.error("Failed to query stats: %s", exc)
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"Failed to fetch database statistics: {exc}",
        ) from exc
