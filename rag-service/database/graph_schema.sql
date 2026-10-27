-- =============================================================================
-- V-EVAL CAPSTONE SYSTEM: KNOWLEDGE GRAPH & RAG SCHEMA (PHASE 1)
-- Schema: v_eval_ai
-- Target: PostgreSQL with pgvector (Unified 768 dimensions for Gemini text-embedding-004)
-- =============================================================================

-- 1. Ensure extension & schema exist
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE SCHEMA IF NOT EXISTS v_eval_ai;

-- 2. Textbook Knowledge Sources
CREATE TABLE IF NOT EXISTS v_eval_ai."KnowledgeSources" (
    "source_id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "domain_id" VARCHAR(100),
    "skill_id" VARCHAR(100),
    "title" VARCHAR(255) NOT NULL,
    "file_url" VARCHAR(500),
    "file_hash" VARCHAR(64),
    "document_type" VARCHAR(50) DEFAULT 'TEXTBOOK', -- 'TEXTBOOK', 'CURRICULUM', 'GENERAL_KNOWLEDGE'
    "total_pages" INTEGER DEFAULT 0,
    "total_chars" INTEGER DEFAULT 0,
    "total_chunks" INTEGER DEFAULT 0,
    "vector_status" VARCHAR(50) DEFAULT 'COMPLETED', -- 'PENDING', 'PROCESSING', 'COMPLETED', 'FAILED'
    "created_at" TIMESTAMP WITHOUT TIME ZONE DEFAULT now(),
    "updated_at" TIMESTAMP WITHOUT TIME ZONE DEFAULT now()
);

-- 3. Textbook Knowledge Vector Chunks (768-dimensional embeddings)
CREATE TABLE IF NOT EXISTS v_eval_ai."KnowledgeVectorChunks" (
    "chunk_id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "source_id" UUID NOT NULL REFERENCES v_eval_ai."KnowledgeSources"("source_id") ON DELETE CASCADE,
    "domain_id" VARCHAR(100),
    "skill_id" VARCHAR(100),
    "chunk_index" INTEGER NOT NULL,
    "page_number" INTEGER DEFAULT 1,
    "content_snippet" TEXT NOT NULL,
    "embedding" vector(768),
    "document_type" VARCHAR(50) DEFAULT 'TEXTBOOK',
    "char_count" INTEGER DEFAULT 0,
    "metadata" JSONB,
    "created_at" TIMESTAMP WITHOUT TIME ZONE DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_knowledge_vector_hnsw 
ON v_eval_ai."KnowledgeVectorChunks" USING hnsw ("embedding" vector_cosine_ops);

-- 4. Archetype Patterns (Taxonomy Level 4 Knowledge Graph)
CREATE TABLE IF NOT EXISTS v_eval_ai.archetype_patterns (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    skill_id UUID NOT NULL, -- Logical link to v_eval_content."Skills"(skill_id)
    pattern_code VARCHAR(100) UNIQUE NOT NULL,
    pattern_name VARCHAR(255) NOT NULL,
    description TEXT,
    core_theorems TEXT NOT NULL,
    fast_solving_heuristics TEXT,
    embedding vector(768), -- Unified 768 dimensions, compatible with pgvector HNSW (< 2000d limit)
    is_verified BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_archetype_patterns_hnsw 
ON v_eval_ai.archetype_patterns USING hnsw (embedding vector_cosine_ops);

-- 5. Pattern Golden Exemplars (Standard solutions)
CREATE TABLE IF NOT EXISTS v_eval_ai.pattern_exemplars (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    archetype_pattern_id UUID NOT NULL REFERENCES v_eval_ai.archetype_patterns(id) ON DELETE CASCADE,
    question_latex TEXT NOT NULL,
    golden_solution TEXT NOT NULL,
    correct_option VARCHAR(10) NOT NULL,
    explanation_steps JSONB NOT NULL DEFAULT '[]'::jsonb,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 6. Pattern Common Traps & Misconceptions
CREATE TABLE IF NOT EXISTS v_eval_ai.pattern_traps (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    archetype_pattern_id UUID NOT NULL REFERENCES v_eval_ai.archetype_patterns(id) ON DELETE CASCADE,
    trap_code VARCHAR(100) NOT NULL,
    trap_name VARCHAR(255) NOT NULL,
    wrong_option VARCHAR(10),
    misconception_explanation TEXT NOT NULL,
    socratic_hint TEXT NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 7. Novel Pattern Proposals (Staging for unclassified questions)
CREATE TABLE IF NOT EXISTS v_eval_ai.novel_pattern_proposals (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_exam_name VARCHAR(255) NOT NULL,
    raw_question_latex TEXT NOT NULL,
    image_urls JSONB DEFAULT '[]'::jsonb,
    extracted_metadata JSONB DEFAULT '{}'::jsonb,
    max_similarity_score DOUBLE PRECISION,
    nearest_archetype_id UUID REFERENCES v_eval_ai.archetype_patterns(id) ON DELETE SET NULL,
    status VARCHAR(30) DEFAULT 'PENDING_REVIEW', -- PENDING_REVIEW, APPROVED, MERGED, REJECTED
    academic_feedback TEXT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 8. AI Tutor Interaction Logs & Human-In-The-Loop Audit
CREATE TABLE IF NOT EXISTS v_eval_ai.ai_tutor_interaction_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id UUID NOT NULL,
    question_id UUID NOT NULL,
    archetype_pattern_id UUID REFERENCES v_eval_ai.archetype_patterns(id) ON DELETE SET NULL,
    student_selected_option VARCHAR(10) NOT NULL,
    correct_option VARCHAR(10) NOT NULL,
    matched_trap_id UUID REFERENCES v_eval_ai.pattern_traps(id) ON DELETE SET NULL,
    ai_guidance_transcript JSONB NOT NULL DEFAULT '[]'::jsonb,
    validation_status VARCHAR(20) DEFAULT 'PASSED', -- PASSED, OVERRIDDEN, REJECTED
    student_reported BOOLEAN DEFAULT FALSE,
    mentor_resolution TEXT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 9. Theoretical Documents (Instructional theory papers ingested for RAG)
CREATE TABLE IF NOT EXISTS v_eval_ai."TheoreticalDocuments" (
    "document_id" VARCHAR(100) PRIMARY KEY,
    "title" VARCHAR(255) NOT NULL,
    "content" TEXT NOT NULL,
    "skill_id" VARCHAR(100) NOT NULL,
    "created_at" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- 10. Socratic Tutor Live Dialogue Sessions
CREATE TABLE IF NOT EXISTS v_eval_ai."TutorSessions" (
    "session_id" VARCHAR(100) PRIMARY KEY,
    "student_id" VARCHAR(100) NOT NULL,
    "question_id" VARCHAR(100) NOT NULL,
    "is_active" BOOLEAN NOT NULL DEFAULT true,
    "messages" JSONB NOT NULL DEFAULT '[]'::jsonb,
    "created_at" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);
