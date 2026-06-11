# AIQ Agentic AI Platform — Documentation

Documentation index. Start here when joining the project.

---

## Getting started

| Document | Purpose |
|----------|---------|
| [getting-started.md](./getting-started.md) | Bring the stack up locally in under 10 minutes. |
| [../README.md](../README.md) | Top-level repository README — stack summary, endpoints, smoke tests. |

## Architecture

| Document | Purpose |
|----------|---------|
| [architecture/overview.md](./architecture/overview.md) | Layered architecture, components, request flow, data flow. |
| [../plan (3).md](../plan%20%283%29.md) | Original 4–6 month roadmap. |
| [../progress-check.md](../progress-check.md) | Live milestone tracker — updated per commit. |

## Operations

| Document | Purpose |
|----------|---------|
| [operations/disaster-recovery.md](./operations/disaster-recovery.md) | RPO/RTO targets, backup cadence, restore procedures, drill log. |
| [operations/sla.md](./operations/sla.md) | Availability + latency SLOs, error budget, on-call expectations. |
| [../infra/backup/](../infra/backup/) | Shell scripts referenced by the DR runbook. |

## Security

| Document | Purpose |
|----------|---------|
| [security/encryption-at-rest.md](./security/encryption-at-rest.md) | Per-store encryption requirements, KMS guidance, verification checklist. |

## Testing

| Document | Purpose |
|----------|---------|
| [../tests/load/README.md](../tests/load/README.md) | k6 load test scripts and thresholds. |
| [../agent/README.md](../agent/README.md) | Agent runtime layout, install + pytest instructions. |

---

## Conventions

- **Markdown style:** tables use compact pipes (`| h |` not `| h  |`).
- **File paths in prose:** wrap in backticks (`infra/backup/postgres-backup.sh`).
- **Commits referenced from docs:** use full SHA — short SHAs collide as the repo grows.
- **Dates:** absolute ISO-8601 (`2026-06-10`), never relative ("yesterday").
- **Diagrams:** Mermaid first, ASCII when Mermaid does not render in the target surface.

## Where to put new docs

| You're writing... | Put it in... |
|-------------------|--------------|
| A user-facing how-to for the running app | `docs/guides/` (create the folder) |
| A new operational runbook | `docs/operations/` |
| A security policy or threat model | `docs/security/` |
| A change to the system architecture | Update `docs/architecture/overview.md` + diagram |
| An API contract change | Inline in the code (XML/docstring) + bump `docs/architecture/overview.md` |

Anything user-facing must end with a "Last verified" line so stale docs are
visible.
