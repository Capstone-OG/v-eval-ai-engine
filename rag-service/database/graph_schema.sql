-- =============================================================================
-- V-EVAL CAPSTONE SYSTEM: KNOWLEDGE GRAPH SCHEMA (PHASE 1)
-- Schema: v_eval_ai
-- Target: PostgreSQL with pgvector (3072 dimensions for gemini-embedding-001)
-- =============================================================================

-- 1. Ensure extension & schema exist
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE SCHEMA IF NOT EXISTS v_eval_ai;

-- 2. Archetype Patterns (Taxonomy Level 4)
CREATE TABLE IF NOT EXISTS v_eval_ai.archetype_patterns (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    skill_id UUID NOT NULL, -- Logical link to v_eval_content."Skills"(skill_id)
    pattern_code VARCHAR(100) UNIQUE NOT NULL,
    pattern_name VARCHAR(255) NOT NULL,
    description TEXT,
    core_theorems TEXT NOT NULL,
    fast_solving_heuristics TEXT,
    embedding vector(3072),
    is_verified BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Note: pgvector HNSW index has a 2000 dimension limit.
-- Since archetype_patterns has ~100-200 domain patterns, exact cosine distance (<=>)
-- performs in < 1ms without requiring an approximate index.
-- If HNSW indexing is needed in the future, use 1536 dimensions or halfvec.

-- 3. Pattern Golden Exemplars (Standard solutions)
CREATE TABLE IF NOT EXISTS v_eval_ai.pattern_exemplars (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    archetype_pattern_id UUID NOT NULL REFERENCES v_eval_ai.archetype_patterns(id) ON DELETE CASCADE,
    question_latex TEXT NOT NULL,
    golden_solution TEXT NOT NULL,
    correct_option VARCHAR(10) NOT NULL,
    explanation_steps JSONB NOT NULL DEFAULT '[]'::jsonb,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 4. Pattern Common Traps & Misconceptions
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

-- 5. Novel Pattern Proposals (Staging for unclassified questions)
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

-- 6. AI Tutor Interaction Logs & Human-In-The-Loop Audit
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
