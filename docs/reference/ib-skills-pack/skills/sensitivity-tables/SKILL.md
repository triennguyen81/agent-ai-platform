---
name: sensitivity-tables
description: Builds live one- and two-variable sensitivity tables on the model's key outputs, used to show which assumptions drive value and where outcomes break.
---

# Sensitivity Tables

## When to use
Reach for this once the model is built and you need to demonstrate which levers matter most. It turns a single point estimate into a defensible range and surfaces the breakpoints a reviewer or committee will inevitably ask about.

## Instructions
1. Identify the output metrics that matter (value, IRR, leverage, coverage) and the candidate driver inputs.
2. Wire one- and two-variable data tables that recalculate cleanly off the live model.
3. Set sensible ranges around the base case for each driver.
4. Rank the drivers by the magnitude of their swing on each output.
5. Flag the breakpoints where an output crosses a covenant, return, or sign threshold.

## Example prompts
- "Add sensitivity tables on IRR for entry multiple and exit multiple."
- "Show me which two assumptions move enterprise value the most and where it breaks."

## Output
A set of one- and two-way sensitivity grids with a swing-driver ranking and short commentary on breakpoints.
