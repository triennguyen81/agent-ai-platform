"""No Agent/LLM tool can approve or pay an expense claim — only the Gateway's
ADMIN-only "Duyệt & chi tiền" action moves cash."""

from __future__ import annotations

import inspect
import pathlib

import aiq_agent.agentic as agentic


def test_no_agent_tool_reaches_expense_approval_or_payment() -> None:
    root = pathlib.Path(inspect.getfile(agentic)).parent
    sources = "\n".join(p.read_text(encoding="utf-8") for p in root.glob("*.py"))
    for route in ("expense-claim/pay", "apply-expense", "expense-claim/department-review", "expense-claim/accounting-review"):
        assert route not in sources, route
