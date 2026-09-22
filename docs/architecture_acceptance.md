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
  - Socratic pedagogical commentary dynamically generated via Google Gemini (`gemini-3.5-flash`) with robust fallback.
- **Verification Results**:
  - 10/10 automated tests passing with 100% success rate (`tests/test_diagnostic.py`), covering math kernels and live FastAPI REST endpoints.
