# AIQ — project guidance for Claude

## ACL invariant (permanent product decision)

**ADMIN Department Authority Invariant** — canonical text:
`docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md` §15.

- A same-department ADMIN may complete the FULL normal operational workflow
  (create, upload, review, department approval, accounting approval,
  authoritative recognition, payment) — including on records they created or
  reviewed themselves. Never block an ADMIN for being creator, uploader,
  claimant, previous reviewer, accounting reviewer or payer.
- USER: no self-approval of authoritative accounting operations (decision
  endpoints are `department_admin`). TENANT_OWNER and SUPER_ADMIN: no
  operational accounting approval. Other department / other tenant:
  deny / not-found.
- Do not reintroduce ADMIN separation-of-duties because old code/tests/docs
  or a generic security pattern suggest it. If a requirement seems to need
  it, STOP and ask for an explicit product decision.
