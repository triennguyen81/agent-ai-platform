-- Tenant Owner RBAC — Stage 3: database enforcement.
--
-- Runs only after Stage 2 (SUPER_ADMIN explicit, audited, per-tenant
-- ownership assignment via /system/tenants/{tenantId}/owner) has actually
-- been completed for every active tenant. The backfill below derives
-- owner_user_id from whatever Stage 2 already produced — it contains no
-- hard-coded tenant/user mapping — and the guard clause fails the migration
-- loudly (not silently) if any active tenant still has none.

ALTER TABLE tenants
  ADD COLUMN IF NOT EXISTS owner_user_id UUID REFERENCES users(id) ON DELETE RESTRICT;

UPDATE tenants t
SET owner_user_id = (
    SELECT u.id
    FROM users u
    JOIN user_roles ur ON ur.user_id = u.id
    JOIN roles r ON r.id = ur.role_id AND r.name = 'tenant_owner'
    WHERE u.tenant_id = t.id AND u.is_active
    ORDER BY u.id
    LIMIT 1
)
WHERE t.owner_user_id IS NULL;

DO $$
DECLARE
    missing_count INT;
BEGIN
    SELECT count(*) INTO missing_count FROM tenants WHERE status = 'active' AND owner_user_id IS NULL;
    IF missing_count > 0 THEN
        RAISE EXCEPTION 'Cannot enforce tenant ownership: % active tenant(s) have no tenant_owner assigned. Complete Stage 2 (SUPER_ADMIN explicit ownership assignment via /system/tenants/{id}/owner) for every active tenant first.', missing_count;
    END IF;
END $$;

-- owner_user_id is kept authoritative automatically: whenever the
-- tenant_owner role is granted to or removed from a user, the owning
-- tenant's pointer is updated to match. This holds regardless of whether
-- the change came through the application's atomic transfer endpoint or a
-- direct role edit — the pointer can never silently drift from reality.
CREATE OR REPLACE FUNCTION sync_tenant_owner_pointer()
RETURNS TRIGGER AS $$
DECLARE
    v_tenant UUID;
    v_role TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT name INTO v_role FROM roles WHERE id = OLD.role_id;
        IF v_role = 'tenant_owner' THEN
            SELECT tenant_id INTO v_tenant FROM users WHERE id = OLD.user_id;
            IF v_tenant IS NOT NULL THEN
                UPDATE tenants SET owner_user_id = NULL, updated_at = NOW()
                WHERE id = v_tenant AND owner_user_id = OLD.user_id;
            END IF;
        END IF;
        RETURN OLD;
    ELSE
        SELECT name INTO v_role FROM roles WHERE id = NEW.role_id;
        IF v_role = 'tenant_owner' THEN
            SELECT tenant_id INTO v_tenant FROM users WHERE id = NEW.user_id;
            IF v_tenant IS NOT NULL THEN
                UPDATE tenants SET owner_user_id = NEW.user_id, updated_at = NOW()
                WHERE id = v_tenant;
            END IF;
        END IF;
        RETURN NEW;
    END IF;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_sync_tenant_owner_pointer ON user_roles;
CREATE TRIGGER trg_sync_tenant_owner_pointer
AFTER INSERT OR DELETE ON user_roles
FOR EACH ROW EXECUTE FUNCTION sync_tenant_owner_pointer();

-- The single authoritative invariant: every active tenant has EXACTLY one
-- active tenant_owner. Deliberately a DEFERRED CONSTRAINT TRIGGER (checked
-- once at COMMIT, not after each individual row change) rather than an
-- immediate check — this is what lets an atomic transfer legitimately pass
-- through a momentary two-owner or zero-owner state mid-transaction while
-- still guaranteeing the invariant holds the instant the transaction
-- becomes visible to anyone else. This same mechanism covers every scenario
-- that can break the invariant: (re)activating a tenant without an owner,
-- disabling/demoting/deleting the sole owner without a replacement, and an
-- incomplete or buggy transfer — all fail the same way, at commit, with the
-- whole transaction rolled back.
--
-- The tenants-table trigger fires on UPDATE OF status/owner_user_id only,
-- deliberately NOT on INSERT: a brand-new tenant is created unowned by
-- design — that is exactly the "unowned" state the Stage 2 preflight report
-- (/system/tenants/owners/preflight) and assignment endpoint
-- (/system/tenants/{id}/owner) exist to surface and close. Gating INSERT
-- itself would make ordinary tenant onboarding (create tenant, then create
-- its admin, then assign its owner — three necessarily separate steps) not
-- possible. "Activating" in the enforced sense means the transition that
-- actually turns access on for an existing tenant record: a status change
-- (e.g. un-suspending) or an owner_user_id change — never the initial row.
--
-- Scoped to exactly the tenant(s) each row change could have affected —
-- never a blanket scan of every tenant in the system — so an unrelated
-- action in tenant B (e.g. tenant B's admin creating a plain user) can never
-- fail because some other tenant happens to be mid-transfer or not yet
-- onboarded.
CREATE OR REPLACE FUNCTION check_tenant_owner_invariant(p_tenant_id UUID)
RETURNS VOID AS $$
DECLARE
    v_status TEXT;
    v_owners INT;
BEGIN
    IF p_tenant_id IS NULL THEN RETURN; END IF;
    SELECT status INTO v_status FROM tenants WHERE id = p_tenant_id;
    IF v_status IS NULL OR v_status <> 'active' THEN RETURN; END IF;

    SELECT count(*) INTO v_owners
    FROM users u
    JOIN user_roles ur ON ur.user_id = u.id
    JOIN roles r ON r.id = ur.role_id AND r.name = 'tenant_owner'
    WHERE u.tenant_id = p_tenant_id AND u.is_active;

    IF v_owners <> 1 THEN
        RAISE EXCEPTION 'tenant % must have exactly one active tenant_owner (has %)', p_tenant_id, v_owners
            USING ERRCODE = '23514';
    END IF;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION check_owner_invariant_on_tenants()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM check_tenant_owner_invariant(NEW.id);
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION check_owner_invariant_on_user_roles()
RETURNS TRIGGER AS $$
DECLARE
    v_role TEXT;
    v_tenant UUID;
BEGIN
    SELECT name INTO v_role FROM roles WHERE id = COALESCE(NEW.role_id, OLD.role_id);
    IF v_role = 'tenant_owner' THEN
        SELECT tenant_id INTO v_tenant FROM users WHERE id = COALESCE(NEW.user_id, OLD.user_id);
        PERFORM check_tenant_owner_invariant(v_tenant);
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION check_owner_invariant_on_users()
RETURNS TRIGGER AS $$
BEGIN
    -- Only relevant if this user actually holds tenant_owner — cheap enough
    -- to check directly rather than scan every disable/tenant-change.
    IF EXISTS (
        SELECT 1 FROM user_roles ur JOIN roles r ON r.id = ur.role_id
        WHERE ur.user_id = NEW.id AND r.name = 'tenant_owner'
    ) THEN
        PERFORM check_tenant_owner_invariant(OLD.tenant_id);
        PERFORM check_tenant_owner_invariant(NEW.tenant_id);
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_check_owner_on_tenants ON tenants;
CREATE CONSTRAINT TRIGGER trg_check_owner_on_tenants
AFTER UPDATE OF status, owner_user_id ON tenants
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_owner_invariant_on_tenants();

DROP TRIGGER IF EXISTS trg_check_owner_on_user_roles ON user_roles;
CREATE CONSTRAINT TRIGGER trg_check_owner_on_user_roles
AFTER INSERT OR UPDATE OR DELETE ON user_roles
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_owner_invariant_on_user_roles();

DROP TRIGGER IF EXISTS trg_check_owner_on_users ON users;
CREATE CONSTRAINT TRIGGER trg_check_owner_on_users
AFTER UPDATE OF is_active, tenant_id ON users
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_owner_invariant_on_users();
