"""
rag_engine.py — Conversational RAG Engine
==========================================
Builds the full Conversational RAG chain using modern LangChain LCEL:

1. **History-Aware Retriever** — rewrites the user question into a
   standalone query using chat history context.
2. **Stuff Documents QA Chain** — feeds retrieved context + history
   into the LLM to produce a grounded answer.
3. **RunnableWithMessageHistory** — automatically persists chat turns
   per ``session_id``.
"""

from __future__ import annotations

import logging
import warnings
from typing import Any

try:
    from langchain.chains import create_retrieval_chain
    from langchain.chains.combine_documents import create_stuff_documents_chain
except ModuleNotFoundError:
    from langchain_classic.chains import create_retrieval_chain
    from langchain_classic.chains.combine_documents import create_stuff_documents_chain
from langchain_community.chat_message_histories import ChatMessageHistory
from langchain_core.chat_history import BaseChatMessageHistory
from langchain_core.output_parsers import StrOutputParser
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_core.runnables import RunnableBranch, RunnableLambda
from langchain_core.runnables.history import RunnableWithMessageHistory

from config import get_llm, get_vector_store

logger = logging.getLogger(__name__)

# Suppress noisy warnings from LangChain and Google GenAI SDK
warnings.filterwarnings("ignore", message=".*RunnableWithMessageHistory is deprecated.*")
warnings.filterwarnings("ignore", message=".*fixed sampling defaults.*")
warnings.filterwarnings("ignore", message=".*automatic function calling.*")

# ---------------------------------------------------------------------------
# In-memory session store  (dict[session_id] → ChatMessageHistory)
# ---------------------------------------------------------------------------
_session_store: dict[str, ChatMessageHistory] = {}


def _get_session_history(session_id: str) -> BaseChatMessageHistory:
    """Return (or create) a ChatMessageHistory for *session_id*.

    Parameters
    ----------
    session_id : str
        Unique conversation identifier.

    Returns
    -------
    BaseChatMessageHistory
        The message history bound to this session.
    """
    if session_id not in _session_store:
        logger.debug("Creating new chat history for session '%s'", session_id)
        _session_store[session_id] = ChatMessageHistory()
    return _session_store[session_id]


# ---------------------------------------------------------------------------
# Prompt Templates
# ---------------------------------------------------------------------------

# Step 1 — Contextualise the question using chat history
_CONTEXTUALISE_SYSTEM_PROMPT = (
    "Given the following conversation history and the user's latest question, "
    "reformulate the question into a clear, standalone question that can be "
    "understood without the chat history. Do NOT answer the question — only "
    "reformulate it if needed, otherwise return it as-is."
)

_contextualise_prompt = ChatPromptTemplate.from_messages(
    [
        ("system", _CONTEXTUALISE_SYSTEM_PROMPT),
        MessagesPlaceholder("chat_history"),
        ("human", "{input}"),
    ]
)

# Step 2 — Answer the question using retrieved context
_QA_SYSTEM_PROMPT = (
    "You are a helpful assistant for question-answering tasks. "
    "Use the following retrieved context to answer the user's question. "
    "If the answer is not contained in the context, clearly state that "
    "you don't have enough information to answer.\n\n"
    "Context:\n{context}"
)

_qa_prompt = ChatPromptTemplate.from_messages(
    [
        ("system", _QA_SYSTEM_PROMPT),
        MessagesPlaceholder("chat_history"),
        ("human", "{input}"),
    ]
)


# ---------------------------------------------------------------------------
# Chain Builder
# ---------------------------------------------------------------------------


def _build_history_aware_retriever(llm, retriever):
    """Build a history-aware retriever with explicit str() casting.

    This replaces ``create_history_aware_retriever`` to work around a
    compatibility issue between Gemini 3.x models and the Google GenAI
    embedding SDK:

    - Gemini 3.x returns ``AIMessage.content`` as a ``list[dict]``
      (with signature metadata), not a plain ``str``.
    - ``StrOutputParser`` wraps this into a ``TextAccessor`` (a ``str``
      subclass). The Google GenAI SDK's async ``aembed_query`` method
      serializes ``TextAccessor`` incorrectly, causing a **500 INTERNAL**
      server error.
    - Adding ``RunnableLambda(str)`` after ``StrOutputParser`` casts the
      value to a plain ``str``, which the SDK handles correctly.
    """
    # Rewrite chain: prompt → LLM → parse to str → cast to plain str
    rewrite_chain = (
        _contextualise_prompt
        | llm
        | StrOutputParser()
        | RunnableLambda(str)
    )

    return RunnableBranch(
        # If no chat history → pass input directly to retriever
        (
            lambda x: not x.get("chat_history", False),
            (lambda x: x["input"]) | retriever,
        ),
        # If chat history exists → rewrite question, then retrieve
        rewrite_chain | retriever,
    ).with_config(run_name="history_aware_retriever")


def build_conversational_rag_chain() -> RunnableWithMessageHistory:
    """Construct the full Conversational RAG chain with memory.

    Architecture::

        User question + chat_history
            │
            ▼
        ┌─────────────────────────┐
        │ History-Aware Retriever │  ← rewrites question → retrieves docs
        └────────────┬────────────┘
                     │ retrieved documents
                     ▼
        ┌─────────────────────────┐
        │  Stuff Documents Chain  │  ← context + history → LLM answer
        └────────────┬────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ RunnableWithMsgHistory  │  ← auto-saves turns per session_id
        └─────────────────────────┘

    Returns
    -------
    RunnableWithMessageHistory
        A chain that accepts ``{"input": str}`` and a ``config`` with
        ``{"configurable": {"session_id": "<id>"}}``.
    """
    llm = get_llm()
    vector_store = get_vector_store()
    # Filter only active document chunks (is_current = True)
    retriever = vector_store.as_retriever(
        search_type="similarity",
        search_kwargs={"k": 4, "filter": {"is_current": True}},
    )

    # Step 1 — History-aware retriever (custom, with str() fix)
    history_aware_retriever = _build_history_aware_retriever(llm, retriever)

    # Step 2 — QA chain (stuff documents into prompt)
    qa_chain = create_stuff_documents_chain(llm, _qa_prompt)

    # Step 3 — Full retrieval chain
    rag_chain = create_retrieval_chain(history_aware_retriever, qa_chain)

    # Step 4 — Wrap with message history
    conversational_chain = RunnableWithMessageHistory(
        rag_chain,
        _get_session_history,
        input_messages_key="input",
        history_messages_key="chat_history",
        output_messages_key="answer",
    )

    logger.info("Conversational RAG chain built successfully.")
    return conversational_chain


# ---------------------------------------------------------------------------
# Convenience API
# ---------------------------------------------------------------------------

# Module-level cached chain (lazy init)
_chain: RunnableWithMessageHistory | None = None


def _ensure_chain() -> RunnableWithMessageHistory:
    """Lazy-init and cache the RAG chain."""
    global _chain  # noqa: PLW0603
    if _chain is None:
        _chain = build_conversational_rag_chain()
    return _chain


def ask(
    question: str,
    session_id: str = "default",
) -> dict[str, Any]:
    """Send a question to the Conversational RAG chain.

    Parameters
    ----------
    question : str
        The user's natural-language question.
    session_id : str, optional
        Conversation session identifier (default ``"default"``).

    Returns
    -------
    dict[str, Any]
        Keys include ``"answer"`` (str) and ``"context"`` (list of
        retrieved ``Document`` objects).

    Raises
    ------
    RuntimeError
        If the chain invocation fails.
    """
    chain = _ensure_chain()
    try:
        result = chain.invoke(
            {"input": question},
            config={"configurable": {"session_id": session_id}},
        )
        return result
    except Exception as exc:
        raise RuntimeError(f"RAG chain invocation failed: {exc}") from exc


def ask_stream(
    question: str,
    session_id: str = "default",
):
    """Stream answer tokens from the Conversational RAG chain.

    Yields strings containing partial answer text as it is generated.
    """
    chain = _ensure_chain()
    try:
        for chunk in chain.stream(
            {"input": question},
            config={"configurable": {"session_id": session_id}},
        ):
            if "answer" in chunk and chunk["answer"]:
                yield chunk["answer"]
    except Exception as exc:
        raise RuntimeError(f"RAG stream failed: {exc}") from exc


def get_session_ids() -> list[str]:
    """Return all active session IDs."""
    return list(_session_store.keys())


def clear_session(session_id: str) -> None:
    """Clear the chat history for a given session.

    Parameters
    ----------
    session_id : str
        The session to clear.
    """
    if session_id in _session_store:
        _session_store[session_id].clear()
        logger.info("Cleared history for session '%s'", session_id)
