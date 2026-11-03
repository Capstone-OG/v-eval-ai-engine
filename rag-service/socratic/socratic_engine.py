"""
socratic_engine.py â€” Socratic AI Tutor Guidance Engine (Core Flow 4 / Phase 4)
=============================================================================
Coordinates Socratic prompt construction, two-layer validation guardrails,
LLM-as-a-Judge gating, and real-time SSE streaming.

Architecture:
-------------
1. Context Hydration: Extracts Core Theorems, Misconceptions, and Hints from the Subgraph.
2. Draft Generation: Generates an exploratory, non-revealing response using Gemini (temp=0.2).
3. LLM-as-a-Judge Gating: Validates the draft against solution-leaking and hallucination.
4. Safe Streaming / Failover:
   - If Approved: Streams validated response token-by-token.
   - If Rejected: Emits a structured Ground-Truth fallback from the verified Knowledge Graph.
"""

from __future__ import annotations

import asyncio
import logging
import os
from typing import Any, AsyncIterator, Optional, Sequence

from dotenv import load_dotenv
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_google_genai import ChatGoogleGenerativeAI

from socratic.validator_judge import (
    JudgeValidationResult,
    validate_draft_response,
)

_RAG_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(_RAG_ROOT, ".env"))
load_dotenv()

logger = logging.getLogger(__name__)

# Primary & fallback models for Socratic generation
SOCRATIC_MODELS: Sequence[str] = (
    "gemini-flash-lite-latest",
    "gemini-3.6-flash",
    "gemini-3.8-flash",
)


def get_socratic_llm(model_name: Optional[str] = None) -> ChatGoogleGenerativeAI:
    api_key = os.environ.get("GOOGLE_API_KEY", "")
    if not api_key:
        raise EnvironmentError("GOOGLE_API_KEY is not set in environment.")
    model = model_name or os.environ.get("SOCRATIC_MODEL") or SOCRATIC_MODELS[0]
    return ChatGoogleGenerativeAI(
        model=model,
        google_api_key=api_key,
        temperature=0.2,
    )


def extract_subgraph_payload(subgraph_context: dict[str, Any]) -> dict[str, str]:
    """Extract and normalize knowledge elements from SocraticSubgraph dict."""
    is_grounded = subgraph_context.get("status") == "GROUNDED" or subgraph_context.get("is_grounded", False)
    anchor = (subgraph_context.get("anchor") or {}) if is_grounded else {}
    pattern_name = anchor.get("pattern_name") or subgraph_context.get("pattern_name") or "Dáº¡ng bÃ i chuáº©n ÄGNL"
    core_theorems = anchor.get("core_theorems") or subgraph_context.get("core_theorems") or "Váº­n dá»¥ng Ä‘á»‹nh lÃ½ vÃ  tÃ­nh cháº¥t cÆ¡ báº£n."
    fast_heuristics = anchor.get("fast_solving_heuristics") or subgraph_context.get("fast_solving_heuristics") or ""

    matched_trap = subgraph_context.get("matched_trap") or {}
    candidate_traps = subgraph_context.get("candidate_traps") or []

    if matched_trap:
        trap_name = matched_trap.get("trap_name", "Lá»—i tÃ­nh toÃ¡n hoáº·c Ã¡p dá»¥ng cÃ´ng thá»©c")
        misconception = matched_trap.get("misconception_explanation", "ChÆ°a kiá»ƒm tra ká»¹ Ä‘iá»u kiá»‡n bÃ i toÃ¡n.")
        socratic_hint = matched_trap.get("socratic_hint", "Em hÃ£y Ä‘á»‘i chiáº¿u láº¡i cÃ¡c bÆ°á»›c tÃ­nh toÃ¡n.")
    elif candidate_traps:
        trap_summaries = []
        for t in candidate_traps:
            opt = f" (phÆ°Æ¡ng Ã¡n {t.get('wrong_option')})" if t.get("wrong_option") else ""
            trap_summaries.append(f"- {t.get('trap_name')}{opt}: {t.get('misconception_explanation')}")
        trap_name = "CÃ¡c báº«y tÆ° duy thÆ°á»ng gáº·p trong dáº¡ng bÃ i nÃ y"
        misconception = "\n".join(trap_summaries)
        socratic_hint = candidate_traps[0].get("socratic_hint", "Em hÃ£y xem láº¡i Ä‘iá»u kiá»‡n rÃ ng buá»™c cá»§a bÃ i toÃ¡n.")
    else:
        trap_name = "Lá»—i nháº­n thá»©c phá»• biáº¿n"
        misconception = "CÃ³ thá»ƒ em Ä‘ang nháº§m láº«n á»Ÿ bÆ°á»›c chuyá»ƒn Ä‘á»•i Ä‘áº¡i lÆ°á»£ng hoáº·c Ä‘iá»u kiá»‡n biÃªn."
        socratic_hint = "Em hÃ£y kiá»ƒm tra láº¡i Ä‘á»‹nh nghÄ©a cá»§a cÃ¡c Ä‘áº¡i lÆ°á»£ng trong Ä‘á» bÃ i nhÃ©."

    return {
        "pattern_name": pattern_name,
        "core_theorems": core_theorems,
        "fast_heuristics": fast_heuristics,
        "trap_name": trap_name,
        "misconception": misconception,
        "socratic_hint": socratic_hint,
    }


def build_socratic_prompts(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_payload: dict[str, str],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
) -> tuple[str, str]:
    """Construct pedagogically strict system and user messages."""
    pattern_name = subgraph_payload["pattern_name"]
    core_theorems = subgraph_payload["core_theorems"]
    trap_name = subgraph_payload["trap_name"]
    misconception = subgraph_payload["misconception"]
    socratic_hint = subgraph_payload["socratic_hint"]

    options_formatted = ""
    if options:
        options_formatted = "\nCÃ¡c phÆ°Æ¡ng Ã¡n cá»§a cÃ¢u há»i:\n" + "\n".join(
            f"{k}. {v}" for k, v in sorted(options.items())
        )

    student_choice_desc = f"{student_selected_option}"
    if student_selected_option_text:
        student_choice_desc += f" ({student_selected_option_text})"

    system_prompt = f"""Báº¡n lÃ  Gia sÆ° AI Socratic cá»§a Há»‡ thá»‘ng Kháº£o thÃ­ V-Eval.
Há»c sinh vá»«a chá»n phÆ°Æ¡ng Ã¡n SAI trong má»™t cÃ¢u há»i tráº¯c nghiá»‡m ÄGNL.
Nhiá»‡m vá»¥ tá»‘i thÆ°á»£ng cá»§a báº¡n lÃ  DáºªN Dáº®T Gá»¢I Má»ž THEO PHÆ¯Æ NG PHÃP SOCRATES. TUYá»†T Äá»I Cáº¤M GIáº¢I THAY.

[NGUá»’N CHÃ‚N LÃ Tá»ª KNOWLEDGE GRAPH]:
- Dáº¡ng bÃ i: {pattern_name}
- Äá»‹nh lÃ½ / CÃ´ng thá»©c chuáº©n:
{core_theorems}
- PhÃ¢n tÃ­ch báº«y nháº­n thá»©c: {trap_name}
{misconception}
- Gá»£i Ã½ báº£n lá» sÆ° pháº¡m: {socratic_hint}

[Dá»® LIá»†U Äá»€ BÃ€I VÃ€ Há»ŒC SINH]:
- PhÆ°Æ¡ng Ã¡n há»c sinh Ä‘Ã£ chá»n (SAI): {student_choice_desc}
- ÄÃ¡p Ã¡n Ä‘Ãºng cá»§a há»‡ thá»‘ng: {correct_option} (BÃ Máº¬T TUYá»†T Äá»I â€” Cáº¤M NÃ“I RA, Cáº¤M Báº¢O Há»ŒC SINH CHá»ŒN {correct_option})

[NGUYÃŠN Táº®C Báº®T BUá»˜C (GUARDRAILS)]:
1. BÆ¯á»šC 1 - GHI NHáº¬N THÃ‚N THIá»†N: Chá»‰ ra lá»—i tÆ° duy há»c sinh vá»«a vÆ°á»›ng pháº£i (vÃ­ dá»¥: "CÃ³ váº» em Ä‘ang tÃ­nh nháº§m táº§n sá»‘ gÃ³c thÃ nh pi/T...").
2. BÆ¯á»šC 2 - NHáº®C Láº I Äá»ŠNH LÃ: TrÃ­ch dáº«n 1 cÃ´ng thá»©c hoáº·c Ä‘á»‹nh lÃ½ báº£n lá» tá»« giÃ¡o trÃ¬nh Ä‘á»ƒ há»c sinh lÃ m má» neo.
3. BÆ¯á»šC 3 - CÃ‚U Há»ŽI Gá»¢I Má»ž: Äáº·t DUY NHáº¤T 1 cÃ¢u há»i gá»£i má»Ÿ then chá»‘t Ä‘á»ƒ há»c sinh tá»± suy nghÄ© vÃ  tÃ­nh toÃ¡n bÆ°á»›c tiáº¿p theo.
4. QUY Táº®C Cáº¤M:
   - TUYá»†T Äá»I KHÃ”NG tÃ­nh ra káº¿t quáº£ sá»‘ há»c cuá»‘i cÃ¹ng.
   - TUYá»†T Äá»I KHÃ”NG nÃ³i Ä‘Ã¡p Ã¡n Ä‘Ãºng lÃ  phÆ°Æ¡ng Ã¡n nÃ o (A, B, C, D).
   - Ngáº¯n gá»n, sÃºc tÃ­ch (dÆ°á»›i 150 tá»«), giá»ng Ä‘iá»‡u Ã¢n cáº§n, khÃ­ch lá»‡.
"""

    if student_query and student_query.strip():
        user_message = f"""Äá» bÃ i cÃ¢u há»i tráº¯c nghiá»‡m:
{question_latex}{options_formatted}

- PhÆ°Æ¡ng Ã¡n há»c sinh Ä‘Ã£ chá»n (sai): {student_choice_desc}
- ÄÃ¡p Ã¡n Ä‘Ãºng cá»§a há»‡ thá»‘ng: {correct_option}

Há»c sinh vá»«a há»i / tháº¯c máº¯c trong cuá»™c trÃ² chuyá»‡n:
"{student_query.strip()}"

Tháº§y/CÃ´ hÃ£y Ä‘Ã³ng vai Gia sÆ° AI Socratic, pháº£n há»“i trá»±c tiáº¿p cÃ¢u há»i/tháº¯c máº¯c trÃªn cá»§a há»c sinh theo nguyÃªn táº¯c gá»£i má»Ÿ sÆ° pháº¡m:
1. Giáº£i thÃ­ch tá»± nhiÃªn, ngáº¯n gá»n Ä‘iá»ƒm máº¥u chá»‘t há»c sinh Ä‘ang bÄƒn khoÄƒn.
2. TUYá»†T Äá»I KHÃ”NG giáº£i thay trá»n váº¹n, khÃ´ng Ä‘Æ°a ra Ä‘Ã¡p Ã¡n cuá»‘i cÃ¹ng.
3. Äáº·t duy nháº¥t 1 cÃ¢u há»i dáº«n dáº¯t Ä‘á»ƒ há»c sinh tá»± suy nghÄ© vÃ  tÃ¬m ra lá»i giáº£i."""
    else:
        user_message = f"""CÃ¢u há»i em vá»«a lÃ m sai lÃ :
{question_latex}{options_formatted}

Em Ä‘Ã£ chá»n Ä‘Ã¡p Ã¡n {student_choice_desc}. Tháº§y/CÃ´ cÃ³ thá»ƒ chá»‰ ra giÃºp em táº¡i sao em suy luáº­n sai vÃ  em nÃªn kiá»ƒm tra láº¡i Ä‘iá»u gÃ¬ khÃ´ng áº¡?"""

    return system_prompt, user_message


def build_fallback_guidance(
    subgraph_payload: dict[str, str],
    question_latex: Optional[str] = None,
    options: Optional[dict[str, str]] = None,
    student_selected_option: Optional[str] = None,
) -> str:
    """Render grounded safe fallback text tailored by curriculum topic from Knowledge Graph."""
    pattern_name = subgraph_payload.get("pattern_name", "Dáº¡ng bÃ i chuáº©n ÄGNL")
    core_theorems = subgraph_payload.get("core_theorems", "")
    misconception = subgraph_payload.get("misconception", "")
    socratic_hint = subgraph_payload.get("socratic_hint", "")

    q_lower = (question_latex or "").lower()
    sel = (student_selected_option or "").strip().upper()
    sel_text = (options or {}).get(sel) if options else None
    choice_label = f"phÆ°Æ¡ng Ã¡n **{sel}**" + (f" ({sel_text})" if sel_text else "") if sel else "phÆ°Æ¡ng Ã¡n Ä‘Ã£ chá»n"

    # Specific topic override if subgraph was generic
    if r"p \Rightarrow q" in q_lower or "kÃ©o theo" in q_lower or "má»‡nh Ä‘á»" in q_lower:
        return (
            "ðŸ’¡ **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (ToÃ¡n logic má»‡nh Ä‘á» & suy luáº­n kÃ©o theo):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label} cho má»‡nh Ä‘á» kÃ©o theo $P \Rightarrow Q$.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Báº£ng chÃ¢n trá»‹ má»‡nh Ä‘á» kÃ©o theo $P \Rightarrow Q$ chá»‰ nháº­n giÃ¡ trá»‹ **Sai** duy nháº¥t khi $P$ ÄÃºng vÃ  $Q$ Sai ($1 \Rightarrow 0 = 0$). Trong má»i trÆ°á»ng há»£p khÃ¡c, má»‡nh Ä‘á» kÃ©o theo luÃ´n ÄÃºng.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Nhiá»u báº¡n láº§m tÆ°á»Ÿng khi $Q$ sai thÃ¬ $P$ cÃ³ thá»ƒ nháº­n báº¥t ká»³ giÃ¡ trá»‹ nÃ o, quÃªn máº¥t ráº±ng náº¿u $P$ Ä‘Ãºng thÃ¬ $P \Rightarrow Q$ sáº½ bá»‹ sai mÃ¢u thuáº«n giáº£ thiáº¿t.\n\n"
            "ðŸ‘‰ *CÃ¢u há»i gá»£i má»Ÿ:* Giáº£ thiáº¿t cho biáº¿t $P \Rightarrow Q$ lÃ  ÄÃºng vÃ  $Q$ lÃ  Sai. Náº¿u $P$ lÃ  ÄÃºng thÃ¬ má»‡nh Ä‘á» $P \Rightarrow Q$ sáº½ mang giÃ¡ trá»‹ ÄÃºng hay Sai?\n"
        )
    elif "cáº¥p sá»‘ cá»™ng" in q_lower or "cÃ´ng sai" in q_lower or "u_n" in q_lower:
        return (
            "ðŸ’¡ **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (DÃ£y sá»‘ & Cáº¥p sá»‘ cá»™ng):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label}.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Sá»‘ háº¡ng tá»•ng quÃ¡t cá»§a cáº¥p sá»‘ cá»™ng $(u_n)$ lÃ  $u_n = u_1 + (n - 1)d$.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Sai láº§m phá»• biáº¿n lÃ  quÃªn trá»« 1 á»Ÿ chá»‰ sá»‘ (láº¥y $u_1 + n \times d$ thay vÃ¬ $(n-1)d$).\n\n"
            "ðŸ‘‰ *CÃ¢u há»i gá»£i má»Ÿ:* Giá»¯a sá»‘ háº¡ng Ä‘áº§u $u_1$ vÃ  sá»‘ háº¡ng thá»© $n$, cÃ³ bao nhiÃªu khoáº£ng cÃ¡ch cÃ´ng sai $d$?\n"
        )
    elif r"log" in q_lower or "táº­p xÃ¡c Ä‘á»‹nh" in q_lower:
        return (
            "ðŸ’¡ **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (HÃ m sá»‘ LÅ©y thá»«a, MÅ© & Logarit):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label}.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Biá»ƒu thá»©c $\\log_a f(x)$ xÃ¡c Ä‘á»‹nh khi vÃ  chá»‰ khi $f(x) > 0$ nghiÃªm ngáº·t vÃ  cÆ¡ sá»‘ $0 < a \neq 1$.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Nháº§m dáº¥u báº¥t phÆ°Æ¡ng trÃ¬nh hoáº·c láº¥y nháº§m khoáº£ng nghiá»‡m cá»§a tam thá»©c báº­c hai.\n\n"
            "ðŸ‘‰ *CÃ¢u há»i gá»£i má»Ÿ:* Khi giáº£i báº¥t phÆ°Æ¡ng trÃ¬nh $f(x) > 0$, khoáº£ng nghiá»‡m cá»§a tam thá»©c báº­c hai náº±m á»Ÿ phÃ­a trong hay phÃ­a ngoÃ i hai nghiá»‡m?\n"
        )
    elif "chÃ­nh táº£" in q_lower or "xÃ¡n láº¡n" in q_lower or "sÃ¡ng láº¡ng" in q_lower:
        return (
            "ðŸ’¡ **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i Socratic (ChÃ­nh táº£ Tiáº¿ng Viá»‡t):**\n\n"
            f"- **PhÃ¢n tÃ­ch lá»±a chá»n:** Em Ä‘ang chá»n {choice_label}.\n"
            "- **Kiáº¿n thá»©c trá»ng tÃ¢m:** Tá»« HÃ¡n Viá»‡t chuáº©n 'XÃ¡n láº¡n' (chá»¯ 'xÃ¡n' lÃ  rá»±c rá»¡, 'láº¡n' lÃ  sÃ¡ng sá»§a, viáº¿t x-l). Trong tá»« Ä‘iá»ƒn tiáº¿ng Viá»‡t chuáº©n khÃ´ng cÃ³ tá»« 'sÃ¡ng láº¡ng'.\n"
            "- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:** Lá»—i Ä‘á»“ng hÃ³a ngá»¯ Ã¢m vá»›i tá»« thuáº§n Viá»‡t 'sÃ¡ng sá»§a'.\n\n"
            "ðŸ‘‰ *CÃ¢u há»i gá»£i má»Ÿ:* Trong tá»« Ä‘iá»ƒn chÃ­nh táº£ tiáº¿ng Viá»‡t, tá»« chá»‰ tÆ°Æ¡ng lai rá»±c rá»¡ báº¯t Ä‘áº§u báº±ng Ã¢m 'x' hay 's'?\n"
        )

    return (
        f"ðŸ’¡ **Gá»£i Ã½ phÆ°Æ¡ng phÃ¡p giáº£i chuáº©n ({pattern_name}):**\n\n"
        f"- **Kiáº¿n thá»©c trá»ng tÃ¢m:**\n{core_theorems}\n\n"
        f"- **LÆ°u Ã½ báº«y thÆ°á»ng gáº·p:**\n{misconception}\n\n"
        f"ðŸ‘‰ *CÃ¢u há»i gá»£i má»Ÿ:* {socratic_hint}\n"
    )


async def generate_socratic_guidance(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_context: dict[str, Any],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
    llm: Optional[ChatGoogleGenerativeAI] = None,
) -> tuple[str, JudgeValidationResult, bool]:
    """Non-streaming guidance generator returning (response_text, judge_result, is_fallback)."""
    subgraph_payload = extract_subgraph_payload(subgraph_context)
    system_prompt, user_message = build_socratic_prompts(
        question_latex=question_latex,
        student_selected_option=student_selected_option,
        correct_option=correct_option,
        subgraph_payload=subgraph_payload,
        options=options,
        student_selected_option_text=student_selected_option_text,
        student_query=student_query,
    )

    llm = llm or get_socratic_llm()
    
    # 1. Generate draft response
    try:
        draft = await llm.ainvoke([
            SystemMessage(content=system_prompt),
            HumanMessage(content=user_message),
        ])
        content_raw = draft.content
        if isinstance(content_raw, list):
            draft_text = "".join(
                item.get("text", "") if isinstance(item, dict) else str(item)
                for item in content_raw
            ).strip()
        else:
            draft_text = str(content_raw).strip()
    except Exception as exc:
        logger.error("Socratic draft generation failed: %s. Using fallback.", exc)
        fallback = build_fallback_guidance(subgraph_payload, question_latex, options, student_selected_option)
        dummy_judge = JudgeValidationResult(
            is_passed=False,
            revealed_direct_answer=False,
            consistent_with_ground_truth=False,
            grounded_in_theorems=False,
            rejection_reason=f"LLM generation failed: {exc}",
        )
        return fallback, dummy_judge, True

    # 2. Inspect with LLM-as-a-Judge
    judge_result = await validate_draft_response(
        ai_draft_response=draft_text,
        correct_option=correct_option,
        core_theorems=subgraph_payload["core_theorems"],
        student_selected_option=student_selected_option,
    )

    if judge_result.is_passed:
        logger.info("Draft response PASSED pedagogical validation.")
        return draft_text, judge_result, False
    else:
        logger.warning("Draft response REJECTED by judge (%s). Reverting to safe fallback.", judge_result.rejection_reason)
        fallback = build_fallback_guidance(subgraph_payload, question_latex, options, student_selected_option)
        return fallback, judge_result, True


async def generate_socratic_guidance_stream(
    question_latex: str,
    student_selected_option: str,
    correct_option: str,
    subgraph_context: dict[str, Any],
    options: Optional[dict[str, str]] = None,
    student_selected_option_text: Optional[str] = None,
    student_query: Optional[str] = None,
    llm: Optional[ChatGoogleGenerativeAI] = None,
) -> AsyncIterator[str]:
    """Real-time SSE token generator with two-layer pedagogical gating."""
    text, judge_result, is_fallback = await generate_socratic_guidance(
        question_latex=question_latex,
        student_selected_option=student_selected_option,
        correct_option=correct_option,
        subgraph_context=subgraph_context,
        options=options,
        student_selected_option_text=student_selected_option_text,
        student_query=student_query,
        llm=llm,
    )

    # Stream out chunks with slight pacing for natural conversational rendering
    words = text.split(" ")
    for idx, word in enumerate(words):
        chunk = word if idx == len(words) - 1 else word + " "
        yield chunk
        await asyncio.sleep(0.015)

