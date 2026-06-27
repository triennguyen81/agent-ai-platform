-- ----------------------------------------------------------------------------
-- G3 — Review Board + Agent Promotion Workflow
--
-- Agents cannot self-promote to production. An agent owner (or anyone with
-- editor/owner ACL) submits a review request; a quorum of review board
-- members must approve before the gateway will allow the lifecycle
-- transition pending_review → production.
--
-- The gateway enforces this guard at the API layer (AgentRepository
-- transition method): pending_review → production is blocked unless there
-- is an approved agent_review_request for that agent.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS review_boards (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name         TEXT NOT NULL,
    quorum       INT NOT NULL DEFAULT 1 CHECK (quorum >= 1),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, name)
);

CREATE INDEX IF NOT EXISTS idx_review_boards_tenant ON review_boards(tenant_id);

CREATE TABLE IF NOT EXISTS review_board_members (
    board_id   UUID NOT NULL REFERENCES review_boards(id) ON DELETE CASCADE,
    user_id    UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role       TEXT NOT NULL DEFAULT 'reviewer',  -- reviewer / chair
    added_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (board_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_review_board_members_user ON review_board_members(user_id);

CREATE TABLE IF NOT EXISTS agent_review_requests (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    agent_id       UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    board_id       UUID NOT NULL REFERENCES review_boards(id) ON DELETE RESTRICT,
    requested_by   UUID NOT NULL REFERENCES users(id) ON DELETE SET NULL,
    target_state   TEXT NOT NULL DEFAULT 'production',
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending / approved / rejected / withdrawn
    rationale      TEXT,
    decided_at     TIMESTAMPTZ,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_review_requests_agent ON agent_review_requests(agent_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_review_requests_board_pending
    ON agent_review_requests(board_id, created_at DESC)
    WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_review_requests_tenant ON agent_review_requests(tenant_id, status);

-- One active (pending) request per (agent, target_state) — prevents
-- duplicates while allowing fresh submissions after a rejection/withdrawal.
CREATE UNIQUE INDEX IF NOT EXISTS uq_review_requests_active
    ON agent_review_requests(agent_id, target_state)
    WHERE status = 'pending';

CREATE TABLE IF NOT EXISTS agent_review_votes (
    request_id   UUID NOT NULL REFERENCES agent_review_requests(id) ON DELETE CASCADE,
    voter_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    vote         TEXT NOT NULL,                    -- approve / reject
    comment      TEXT,
    voted_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (request_id, voter_id)
);

CREATE INDEX IF NOT EXISTS idx_review_votes_request ON agent_review_votes(request_id);

-- ----------------------------------------------------------------------------
-- Seed a default review board for the existing 'default' tenant so the
-- workflow works out of the box. All current tenant_admins are added as
-- reviewers (quorum=1 for the pilot — a single approver is enough).
-- ----------------------------------------------------------------------------

INSERT INTO review_boards (tenant_id, name, quorum)
SELECT t.id, 'Default Review Board', 1
FROM tenants t
WHERE t.code = 'default'
ON CONFLICT (tenant_id, name) DO NOTHING;

INSERT INTO review_board_members (board_id, user_id, role)
SELECT rb.id, tm.user_id, 'reviewer'
FROM review_boards rb
JOIN tenants t ON t.id = rb.tenant_id AND t.code = 'default'
JOIN tenant_members tm ON tm.tenant_id = t.id AND tm.role = 'tenant_admin'
WHERE rb.name = 'Default Review Board'
ON CONFLICT (board_id, user_id) DO NOTHING;
