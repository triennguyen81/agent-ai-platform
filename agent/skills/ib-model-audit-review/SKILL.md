---
name: ib-model-audit-review
description: Audits an integrated model for circularity, broken links, hardcodes, and formula inconsistencies, used as the quality gate before a model goes upstairs.
---

# Model Audit Review

## When to use
Reach for this before any model leaves the analyst's hands. It is the discipline of finding the circular reference, the broken link, and the buried hardcode before a reviewer does, so the model survives scrutiny and the team's credibility holds.

## Instructions
1. Trace the calculation chain and locate any circular references, documenting their source and intended handling.
2. Map links across tabs and external sources and flag any broken or stale references.
3. Scan formulas for hardcoded values, inconsistent rows, and sign errors.
4. Reconcile key outputs against an independent rough check to catch magnitude errors.
5. Compile an error log and a sign-off checklist with each issue marked resolved or open.

## Example prompts
- "Audit this model for circular references and broken links before I send it up."
- "Run a pre-review check and give me an error log with hardcodes flagged."

## Output
An audit report with a categorized error log, a circularity and link summary, hardcode flags, and a sign-off checklist.
