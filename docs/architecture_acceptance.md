# BÁO CÁO NGHIỆM THU VÀ THẤU HIỂU KIẾN TRÚC (ARCHITECTURE ACCEPTANCE) - AI ENGINE

## 1. TỔNG QUAN DỊCH VỤ
- **Tên Dịch Vụ**: V-Eval AI Engine (RAG Vector Search, Exam Ingestion & Adaptive Learning Path).
- **Cổng Dịch Vụ**: `5104`.
- **Kiến Trúc**: Modular .NET 9 API kết nối Vector Database (Qdrant).

## 2. KIẾN TRÚC TÍCH HỢP AI & VECTOR DATABASE
- Kết nối Qdrant Vector DB (`port 6333 / 6334`) để lưu trữ embeddings tri thức và ma trận đề thi.
- Tích hợp pipeline RAG (Retrieval-Augmented Generation) phục vụ gợi ý lộ trình học tập cá nhân hóa.

## 3. KẾT QUẢ NGHIỆM THU
- Docker Build: Multi-Stage .NET 9 trên cổng `5104` đạt 100% Succeeded.
- Tích hợp sẵn sàng cho hạ tầng AI Python/PostgreSQL/Qdrant.

## 4. CORE FLOW 1 — IRT & BKT DIAGNOSTIC ENGINE ACCEPTANCE
- **Module**: `rag-service/diagnostic_engine.py` & `rag-service/routers/diagnostic.py`.
- **Psychometric Modeling**:
  - IRT 2-Parameter Logistic (2PL) model with Maximum A Posteriori (MAP) estimation using a Gaussian prior `N(0, 2.0^2)` to regularize extreme scores.
  - Scalar optimization solved via Brent's method (`scipy.optimize.minimize_scalar`) bounded in `[-3.0, 3.0]`.
  - Rapid-guessing suppression: items answered in `< 5s` receive discounted discrimination (`a -> 0.1`) to prevent score inflation.
  - BKT Initial Mastery Prior: `P(L0) = Sigmoid(theta)` clamped safely to `[0.05, 0.95]`.
  - Unhappy case tolerance: untested skills seamlessly inherit inferred priors from their parent competency domain.
  - Placement tiers: `FOUNDATION` (`theta < -0.5`), `ACCELERATION` (`-0.5 <= theta <= 0.5`), `BREAKTHROUGH` (`theta > 0.5`).
  - Radar chart coordinates: student domain percentages vs benchmark targets calculated from the student's expected V-ACT score.
  - Socratic pedagogical commentary dynamically generated via Google Gemini (`gemini-3.6-flash`) with robust fallback.
- **Verification Results**:
  - 12/12 automated tests passing with 100% success rate (`tests/test_diagnostic.py`), covering math kernels and live FastAPI REST endpoints.
  - Comprehensive mathematical specifications published at `docs/cong_thuc_psychometrics_irt_bkt.md`.

## 5. CORE FLOW 2 — TEXTBOOK RAG INGESTION & SUPABASE `v_eval_ai` SCHEMA ACCEPTANCE
- **Local Textbook Ingestion Engine**:
  - Python PyMuPDF + RapidOCR local offline hybrid parser (`textbook_local_parser.py`) extracting pure-text subjects (Humanities, English) in 1s for searchable PDFs, with ONNX-based local OCR fallback for scanned images.
  - Multi-model Vision AI rotation (`gemini-1.5-flash`, `gemini-2.0-flash`, `gpt-4o-mini`) for STEM subjects (Math, Physics, Chemistry) ensuring LaTeX formulas wrapped in `$...\$`.
- **Database Persistence (`v_eval_ai` Schema on Supabase PostgreSQL)**:
  - Database extension `vector` (pgvector) enabled.
  - `v_eval_ai."KnowledgeSources"` table storing document metadata, total pages, character counts, and SHA-256 hashes.
  - `v_eval_ai."KnowledgeVectorChunks"` table storing semantic chunks (~1000 chars, ~200 overlap), `embedding vector(768)` and HNSW cosine index (`idx_knowledge_vector_hnsw`).
  - Implemented `TextbookRepository` (`ITextbookRepository`) using `Npgsql` to persist textbooks and chunks directly into Supabase.
- **Web Studio Frontend**:
  - Glassmorphic responsive interface at `/api/ai-engine/view-textbook` with navbar tab switching, real-time background job polling, progress console, chunk preview cards, and direct "Lưu Tri Thức Vào Database" button.

## 6. VISION MODELS BENCHMARK & RESUMABLE CHECKPOINT INGESTION ACCEPTANCE
- **Vision AI Performance Validation & Low-DPI Optimization**:
  - Validated that dense scanned textbook PDFs (100% image pages, e.g. 170 pages) causing CPU thrashing (~3000s execution) are solved by Cloud Vision models reducing latency to ~20-30s in batch.
  - Image rendering downsampled to `Dpi = 96` and compressed as JPEG quality 70 (<80 KB per page), minimizing token footprint and maximizing throughput on Google Free Tier.
  - Full verbatim retention of STEM formulas in LaTeX (``$x^2 + y^2$``) and Markdown table structures.
- **Resumable Checkpoint Architecture & Background Worker**:
  - Implemented per-page checkpointing directly into Supabase PostgreSQL (`v_eval_ai."KnowledgeSources"` and `v_eval_ai."KnowledgeVectorChunks"`).
  - Interrupted jobs automatically resume from `lastProcessedPage + 1` without re-processing earlier pages, preventing duplicate work and redundant API calls.
  - Studio UI `/api/ai-engine/view-textbook` automatically reconnects to in-flight jobs via `GET /api/ai-engine/textbooks/active-job` upon page reload or navigation return.
- **REST Ping Endpoint & Tooling**:
  - Implemented `GET /api/ai-engine/textbooks/ping-vision`: measures round-trip latency (ms), HTTP response codes, and model availability across `gemini-1.5-flash`, `gemini-2.0-flash`, `gemini-1.5-flash-8b`, `gemini-1.5-pro`, and `gpt-4o-mini`.
  - Glassmorphic Ping Test Banner integrated into `/api/ai-engine/view-textbook` allowing on-the-fly API key testing with real-time latency feedback.
  - PowerShell CLI utility `Scripts/run_local/ping_vision.ps1` for rapid terminal-based health checks.

## 7. FONT MOJIBAKE AUTO-DETECTION & FORCED VISION AI INGESTION ACCEPTANCE
- **Problem Analysis (InDesign Custom CID Encoding)**:
  - Commercial Vietnamese textbooks (notably Grade 10 Geography, History, Literature) contain subset fonts without standard ToUnicode CMaps. Extracting their digital text layer via standard PDF tools produces unreadable mojibake strings (e.g., `&IFHPkFVLQK WKKQPQCdo...`).
- **Tri-Layer Heuristic Detector (`IsCorruptedFontEncoding`)**:
  - **Layer 1 (Corrupted Glyphs Density)**: Detects abnormal symbol ratio (`{`, `}`, `\`, `^`, `~`, `|`, `¶`, `§`, `©`, `®`, `½`, `¼`, `¾`, `¿`, `±`, `ł`, `Ċ`, `ī`, `Ť`, `Š`, `ś`).
  - **Layer 2 (Vowel Ratio Analysis)**: Natural Vietnamese/English prose maintains 32%-50% vowels; corrupted font text collapses to `< 23%`.
  - **Layer 3 (Consonant Clustering & Vowelless Tokens)**: Flags tokens lacking vowels or containing sequences of 5+ consecutive consonants (`WKKQPQC`, `tFKWKtFKWt`).
- **Automatic Fallback & User Controls**:
  - When corrupted encoding is detected, the engine discards the corrupted text and immediately redirects the page to Gemini Vision AI (DPI 96), yielding 100% clean Vietnamese text.
  - Added `VISION_AI` mode option to force pure vision ingestion when requested.
  - Added `forceReingest` flag and UI checkbox to purge previously corrupted chunks (`DELETE FROM v_eval_ai."KnowledgeVectorChunks" WHERE source_id = @source_id`) and start cleanly from Page 1.
  - Added `GET /chunks/{sourceId}` and `GET /chunks-by-hash/{fileHash}` endpoints for instant chunk inspection.

## 8. RICH TEXTBOOK CHUNKS VISUALIZATION ACCEPTANCE (MARKDOWN + KATEX + TABLES)
- **Problem Statement (Raw Text Monoliths)**:
  - Previously, extracted chunks were presented in plain raw text without HTML formatting, causing multi-column statistical tables and STEM equations to appear as raw, unformatted markdown pipes and raw LaTeX strings.
- **Client-Side Rendering Engine (`view-textbook.html`)**:
  - Integrated `marked.js` with GitHub Flavored Markdown (GFM) tables, blockquotes, and lists.
  - Implemented pre-parsing math shield (`@@MATH_BLOCK_X@@` and `@@MATH_INLINE_X@@`) preserving inline (`$...$`) and block (`$$...$$`) LaTeX syntax from markdown parser disruption.
  - Integrated KaTeX render engine rendering complex equations and fractions without external server dependencies.
- **Glassmorphic UI Toolbar & Interaction**:
  - Added global & per-chunk view mode toggle: `👁️ Trực Quan` (Rendered HTML) vs `📄 Raw Text` (Source Markdown).
  - Added real-time text & page number filter (`chunkSearchInput`) with live chunk match counter.
  - Added one-click copy (`📋 Copy`) per chunk, bulk copy (`📋 Sao Chép Hết`), and one-click full book Markdown export (`💾 Tải File .MD`).

