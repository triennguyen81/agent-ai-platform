"""Agentic runtime (Phase 2).

POST /agent/run uses the Claude Agent SDK with in-process MCP tools that
proxy to Gateway (`search_notes`, `save_note`) and a lightweight
`fetch_url` for ingestion workflows.
"""

from aiq_agent.agentic.runner import AgenticRunResult, run_task

__all__ = ["AgenticRunResult", "run_task"]
