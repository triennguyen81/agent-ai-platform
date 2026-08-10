---
name: comparable-companies
description: Runs a comparable companies analysis by selecting peers, calculating trading multiples, and applying them to the target, used for a current, market-anchored valuation view.
---

# Comparable Companies

## When to use
Reach for this when you need to know what the public market pays for similar businesses right now. It grounds the valuation in observable multiples and pairs naturally with intrinsic and transaction methods to triangulate a range.

## Instructions
1. Select a peer set justified by business model, size, growth, and margin comparability.
2. Pull and normalize each peer's financial metrics and market data, adjusting for non-recurring items.
3. Calculate the relevant trading multiples (EV/EBITDA, EV/Revenue, P/E) and review the distribution.
4. Apply the chosen multiple range to the target's normalized metrics to derive an implied value range.
5. Flag outliers and explain why each peer pulls the range up or down.

## Example prompts
- "Run trading comps for this mid-cap medtech target and give me an EV/EBITDA range."
- "Pick a defensible peer set and show the implied valuation with outliers explained."

## Output
A comps output with a peer table (metrics and multiples), a distribution summary, and an implied-range statement with outlier notes.
