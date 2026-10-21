"""
apply_graph_schema.py — Migration Runner for Knowledge Graph Schema
====================================================================
Executes database/graph_schema.sql to set up the GraphRAG tables
in schema v_eval_ai with HNSW vector index support on PostgreSQL/Supabase.
"""

import os
import sys
import psycopg

# Default to production Supabase pooler if local DATABASE_URL is not configured
DEFAULT_SUPABASE_URI = (
    "postgresql://postgres.zwxavhoszodmagisoiqu:ThinhTran2412@"
    "aws-0-ap-southeast-1.pooler.supabase.com:5432/postgres?sslmode=require"
)

def get_connection_uri() -> str:
    env_uri = os.environ.get("SUPABASE_DATABASE_URL") or os.environ.get("DATABASE_URL")
    if env_uri and "supabase" in env_uri:
        return env_uri
    return DEFAULT_SUPABASE_URI

def apply_schema():
    current_dir = os.path.dirname(os.path.abspath(__file__))
    sql_path = os.path.join(current_dir, "graph_schema.sql")
    
    if not os.path.exists(sql_path):
        print(f"❌ Error: {sql_path} does not exist!")
        sys.exit(1)
        
    with open(sql_path, "r", encoding="utf-8") as f:
        sql_content = f.read()

    conn_uri = get_connection_uri()
    print(f"Connecting to database to apply Knowledge Graph schema...")

    with psycopg.connect(conn_uri) as conn:
        with conn.cursor() as cur:
            cur.execute(sql_content)
            conn.commit()
            print("[OK] DDL executed successfully.")

            # Verification
            cur.execute("""
                SELECT table_name 
                FROM information_schema.tables 
                WHERE table_schema = 'v_eval_ai'
                ORDER BY table_name;
            """)
            tables = [row[0] for row in cur.fetchall()]
            print("Verified tables in schema 'v_eval_ai':", tables)

            expected_tables = [
                "ai_tutor_interaction_logs",
                "archetype_patterns",
                "novel_pattern_proposals",
                "pattern_exemplars",
                "pattern_traps"
            ]
            missing = [t for t in expected_tables if t not in tables]
            if missing:
                print(f"[WARN] Missing expected tables: {missing}")
            else:
                print("[SUCCESS] All 5 Knowledge Graph tables created and verified successfully!")

if __name__ == "__main__":
    apply_schema()
