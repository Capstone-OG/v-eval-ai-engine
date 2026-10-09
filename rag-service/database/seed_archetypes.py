"""
seed_archetypes.py — Knowledge Graph Initial Archetype Seeder
=============================================================
Seeds the baseline Archetype Pattern, Golden Exemplar, and Common Traps
into schema v_eval_ai on PostgreSQL/Supabase, computing a 3072-dim
vector embedding via Google Gemini Embedding API.
"""

import json
import os
import sys
import psycopg
from dotenv import load_dotenv

# Ensure environment variables are loaded from rag-service root
current_dir = os.path.dirname(os.path.abspath(__file__))
parent_dir = os.path.dirname(current_dir)
load_dotenv(os.path.join(parent_dir, ".env"))

from langchain_google_genai import GoogleGenerativeAIEmbeddings

DEFAULT_SUPABASE_URI = (
    "postgresql://postgres.zwxavhoszodmagisoiqu:ThinhTran2412@"
    "aws-0-ap-southeast-1.pooler.supabase.com:5432/postgres?sslmode=require"
)

def get_connection_uri() -> str:
    env_uri = (
        os.environ.get("GRAPH_DATABASE_URL")
        or os.environ.get("SUPABASE_DATABASE_URL")
        or os.environ.get("DATABASE_URL")
    )
    if env_uri and "supabase" in env_uri:
        return env_uri.replace("postgresql+psycopg://", "postgresql://")
    return DEFAULT_SUPABASE_URI


def resolve_skill_id(cur, skill_name: str) -> str:
    """Resolve a taxonomy skill by exact (case-insensitive) name.

    Fails loudly instead of silently linking the archetype to an unrelated skill.
    """
    cur.execute(
        'SELECT skill_id FROM v_eval_content."Skills" WHERE lower(name) = lower(%s) LIMIT 1;',
        (skill_name,),
    )
    row = cur.fetchone()
    if not row:
        raise LookupError(f"Skill '{skill_name}' not found in v_eval_content.Skills")
    return row[0]

def seed_data():
    conn_uri = get_connection_uri()
    print("Connecting to database for seeding...")

    # 1. Initialize Gemini Embeddings
    google_api_key = os.environ.get("GOOGLE_API_KEY")
    if not google_api_key:
        print("[ERROR] GOOGLE_API_KEY not found in environment!")
        sys.exit(1)

    embeddings = GoogleGenerativeAIEmbeddings(
        model="models/gemini-embedding-001",
        google_api_key=google_api_key
    )

    # 2. Define Archetype Data (Taxonomy Level 4)
    pattern_code = "MATH_ASYMPTOTE_PARAM_01"
    pattern_name = "Khảo sát tiệm cận hàm phân thức chứa tham số m"
    pattern_desc = (
        "Dạng toán tìm giá trị của tham số m để đồ thị hàm số phân thức hữu tỉ "
        "y = (ax + b) / (cx + d) hoặc y = P(x) / Q(x) có số đường tiệm cận đứng "
        "và tiệm cận ngang thỏa mãn điều kiện cho trước."
    )
    core_theorems = (
        "1. Đường thẳng y = y0 là tiệm cận ngang nếu lim(x -> +inf) y = y0 hoặc lim(x -> -inf) y = y0.\n"
        "2. Đường thẳng x = x0 là tiệm cận đứng nếu lim(x -> x0) y = +inf hoặc lim(x -> x0) y = -inf.\n"
        "3. Điều kiện cần và đủ để x = x0 là tiệm cận đứng của hàm phân thức P(x)/Q(x): "
        "x0 là nghiệm của mẫu số Q(x0) = 0 và x0 KHÔNG làm triệt tiêu tử số (P(x0) != 0)."
    )
    fast_heuristics = (
        "- Tiệm cận ngang của hàm bậc 1 / bậc 1 là y = a/c (hệ số x ở tử chia hệ số x ở mẫu).\n"
        "- Nghiệm của mẫu số nếu trùng với nghiệm của tử số sẽ bị rút gọn, làm mất tiệm cận đứng."
    )

    text_to_embed = f"{pattern_name}\n{pattern_desc}\n{core_theorems}"
    print("Computing 3072-dimensional vector embedding with Gemini...")
    vector = embeddings.embed_query(text_to_embed)
    vector_str = "[" + ",".join(map(str, vector)) + "]"
    print(f"[OK] Embedding computed ({len(vector)} dimensions).")

    # 3. Resolve the correct taxonomy skill (Level 3) by name
    with psycopg.connect(conn_uri) as conn:
        with conn.cursor() as cur:
            skill_id = resolve_skill_id(cur, "Khảo sát hàm số")
            print(f"Linking Archetype to Skill ID: {skill_id}")

            # 4. Insert or Update Archetype Pattern
            cur.execute("""
                INSERT INTO v_eval_ai.archetype_patterns 
                (skill_id, pattern_code, pattern_name, description, core_theorems, fast_solving_heuristics, embedding, is_verified)
                VALUES (%s, %s, %s, %s, %s, %s, %s::vector, TRUE)
                ON CONFLICT (pattern_code) DO UPDATE SET
                    skill_id = EXCLUDED.skill_id,
                    pattern_name = EXCLUDED.pattern_name,
                    description = EXCLUDED.description,
                    core_theorems = EXCLUDED.core_theorems,
                    fast_solving_heuristics = EXCLUDED.fast_solving_heuristics,
                    embedding = EXCLUDED.embedding,
                    updated_at = CURRENT_TIMESTAMP
                RETURNING id;
            """, (skill_id, pattern_code, pattern_name, pattern_desc, core_theorems, fast_heuristics, vector_str))
            
            archetype_id = cur.fetchone()[0]
            print(f"[SUCCESS] Archetype Pattern created/updated with ID: {archetype_id}")

            # 5. Insert Golden Exemplar
            question_latex = (
                "Tìm tất cả các giá trị thực của tham số $m$ để đồ thị hàm số "
                "$y = \\frac{x - 1}{x^2 - 2x + m}$ có đúng 2 đường tiệm cận đứng.\n"
                "A. $m < 1$\n"
                "B. $m < 1$ và $m \\neq 1$\n"
                "C. $m < 1$ và $m \\neq 0$\n"
                "D. $m > 1$"
            )
            golden_solution = (
                "Bước 1: Để đồ thị hàm số có 2 đường tiệm cận đứng, phương trình mẫu số "
                "$g(x) = x^2 - 2x + m = 0$ phải có 2 nghiệm phân biệt khác 1 (nghiệm của tử số).\n"
                "Bước 2: Điều kiện có 2 nghiệm phân biệt: $\\Delta' = (-1)^2 - m = 1 - m > 0 \\iff m < 1$.\n"
                "Bước 3: Điều kiện 2 nghiệm khác 1: $g(1) = 1^2 - 2(1) + m \\neq 0 \\iff m - 1 \\neq 0 \\iff m \\neq 1$.\n"
                "Bước 4: Điều kiện $m = 1$ đã bị loại bởi $m < 1$. Tại $x = 1$, nếu $m = 1$ thì mẫu có nghiệm kép $x = 1$. "
                "Khi $m < 1$, hai nghiệm là $1 \\pm \\sqrt{1-m}$, không bao giờ bằng 1 vì $\\sqrt{1-m} > 0$. "
                "Do đó cả 2 nghiệm luôn khác 1.\n"
                "Vậy điều kiện là $m < 1$ (Phương án A)."
            )
            explanation_steps = json.dumps([
                {"step": 1, "action": "Tìm điều kiện mẫu số có 2 nghiệm phân biệt", "condition": "Delta' > 0 => m < 1"},
                {"step": 2, "action": "Kiểm tra nghiệm mẫu không trùng nghiệm tử x=1", "condition": "g(1) != 0 => m != 1 (thỏa mãn khi m < 1)"},
                {"step": 3, "action": "Kết luận", "result": "m < 1"}
            ])

            # Delete old exemplars for this archetype before inserting
            cur.execute("DELETE FROM v_eval_ai.pattern_exemplars WHERE archetype_pattern_id = %s;", (archetype_id,))
            cur.execute("""
                INSERT INTO v_eval_ai.pattern_exemplars 
                (archetype_pattern_id, question_latex, golden_solution, correct_option, explanation_steps)
                VALUES (%s, %s, %s, %s, %s::jsonb);
            """, (archetype_id, question_latex, golden_solution, "A", explanation_steps))
            print("[SUCCESS] Pattern Exemplar inserted.")

            # 6. Insert Common Traps
            cur.execute("DELETE FROM v_eval_ai.pattern_traps WHERE archetype_pattern_id = %s;", (archetype_id,))
            traps = [
                (
                    "TRAP_FORGET_NUMERATOR_ROOT",
                    "Quên kiểm tra nghiệm mẫu số triệt tiêu nghiệm của tử số",
                    "B",
                    "Học sinh giải ra Delta > 0 rồi vội vàng trừ thêm điều kiện không cần thiết hoặc nhầm lẫn nghiệm bội của tử.",
                    "Em hãy kiểm tra xem hai nghiệm của mẫu số có khi nào bằng nghiệm của tử số (x = 1) hay không?"
                ),
                (
                    "TRAP_CONFUSE_HORIZONTAL_ASYMPTOTE",
                    "Nhầm lẫn giữa tiệm cận đứng và tiệm cận ngang",
                    "D",
                    "Học sinh nhầm lẫn tính chất bậc tử và mẫu đối với tiệm cận đứng, dẫn đến đảo chiều bất phương trình.",
                    "Để tìm tiệm cận đứng, chúng ta quan tâm đến nghiệm của mẫu số hay giới hạn khi x tiến ra vô cực?"
                )
            ]

            for t_code, t_name, wrong_opt, misconc, socratic in traps:
                cur.execute("""
                    INSERT INTO v_eval_ai.pattern_traps 
                    (archetype_pattern_id, trap_code, trap_name, wrong_option, misconception_explanation, socratic_hint)
                    VALUES (%s, %s, %s, %s, %s, %s);
                """, (archetype_id, t_code, t_name, wrong_opt, misconc, socratic))

            conn.commit()
            print(f"[SUCCESS] {len(traps)} Pattern Traps inserted.")
            print("[ALL COMPLETED] Knowledge Graph Phase 1 seeding finished cleanly!")

if __name__ == "__main__":
    seed_data()
