"""
llm_clients.py — Shared low-latency Google GenAI client for the Socratic pipeline
================================================================================
The SDK's default Tenacity policy retries 429/503 with exponential backoff (up to
~25s), which starves every per-step timeout in the tutor pipeline. All auxiliary
calls (intent routing, LLM-as-a-Judge) therefore share ONE cached client with a
single attempt: a failing model fails fast and the caller degrades gracefully.
"""

from __future__ import annotations

import os
from functools import lru_cache

from dotenv import load_dotenv
from google import genai
from google.genai import types

_RAG_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(_RAG_ROOT, ".env"))
load_dotenv()


@lru_cache(maxsize=1)
def get_fast_genai_client() -> genai.Client:
    """Return a process-wide GenAI client without automatic retries."""
    api_key = os.environ.get("GOOGLE_API_KEY", "")
    if not api_key:
        raise EnvironmentError("GOOGLE_API_KEY is not set in environment.")
    return genai.Client(
        api_key=api_key,
        http_options=types.HttpOptions(retry_options=types.HttpRetryOptions(attempts=1)),
    )

