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
