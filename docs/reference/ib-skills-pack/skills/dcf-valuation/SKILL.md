---
name: dcf-valuation
description: Builds a discounted-cash-flow valuation with transparent cash-flow, discount-rate, and terminal-value assumptions, used when the team needs an intrinsic, assumption-driven view of value.
---

# DCF Valuation

## When to use
Use this when intrinsic value matters and you can defend a multi-year forecast, typically alongside market-based methods to anchor the low-to-mid of a range. It exposes how value depends on growth, margins, and discount rate, which is exactly what a committee will probe.

## Instructions
1. Build the unlevered free-cash-flow forecast from revenue, margin, capex, and working-capital assumptions.
2. Derive the discount rate from capital-structure and risk inputs, documenting each component.
3. Estimate terminal value under both perpetuity-growth and exit-multiple methods and reconcile them.
4. Discount cash flows and terminal value to enterprise value, then bridge to equity value.
5. Run a sensitivity grid on discount rate and terminal assumptions and state the resulting range.

## Example prompts
- "Build a DCF for this SaaS target with a 5-year forecast and a sensitivity on WACC and growth."
- "Value this business intrinsically and show me how much the answer swings on terminal multiple."

## Output
A valuation memo with the FCF build table, discount-rate derivation, terminal-value reconciliation, and a two-way sensitivity grid.
