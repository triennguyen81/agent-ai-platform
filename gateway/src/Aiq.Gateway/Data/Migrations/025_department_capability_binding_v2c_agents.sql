-- ACL V2-D: widen department_capability_bindings' eligibility trigger to
-- also recognize a V2-C Department Agent's structural ownership.
--
-- Why this migration is required (V2-D brief §18's explicit escape hatch):
-- 022_business_capabilities.sql's enforce_department_capability_binding_eligibility()
-- unconditionally requires an ACTIVE agent_department_assignments row before
-- allowing an active binding. A V2-C Department Agent (agents.v2_scope=
-- 'department') can never have a row in that table: migration 007's
-- agents_scope_ownership_valid requires scope='system' rows to have a NULL
-- owning_tenant_id (agent_department_assignments' own INSERT path — see
-- DynamicAgentRepository.AssignDepartmentAgentAsync — requires scope=
-- 'system'), while migration 024's agents_v2_scope_ownership_valid requires
-- v2_scope='department' rows to have a NON-NULL owning_tenant_id. No single
-- row can satisfy both constraints, so no Department Agent can ever acquire
-- an agent_department_assignments row — the trigger would reject 100% of
-- attempts to bind a capability to one, regardless of any correct
-- application-layer authorization. This is a genuine structural gap, not a
-- design preference; it cannot be worked around in application code because
-- the rejection happens inside the database trigger itself.
--
-- Fix: widen the trigger with one additional, purely additive eligibility
-- path (structural Department Agent ownership). Every existing legacy/
-- platform-agent binding path — the original condition — is preserved
-- byte-for-byte; this migration only ever makes a previously-impossible
-- case possible, never removes or narrows an existing one. No table,
-- column, or row is altered.

CREATE OR REPLACE FUNCTION enforce_department_capability_binding_eligibility()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.status = 'active' THEN
        IF NOT EXISTS (
            SELECT 1 FROM agent_department_assignments ada
            WHERE ada.agent_id = NEW.agent_id AND ada.tenant_id = NEW.tenant_id
              AND ada.department_id = NEW.department_id AND ada.status = 'active'
        ) AND NOT EXISTS (
            -- ACL V2-D: structural Department Agent ownership is itself the
            -- entitlement — there is no separate assignment row to check.
            SELECT 1 FROM agents a
            WHERE a.id = NEW.agent_id AND a.v2_scope = 'department'
              AND a.owning_tenant_id = NEW.tenant_id AND a.department_id = NEW.department_id
        ) THEN
            RAISE EXCEPTION 'active capability binding requires an active department agent assignment' USING ERRCODE = '23514';
        END IF;
        IF NOT EXISTS (
            SELECT 1 FROM agents a
            JOIN agent_versions v ON v.id = a.published_version_id
            JOIN agent_version_capabilities avc ON avc.agent_version_id = v.id
            WHERE a.id = NEW.agent_id AND v.status = 'published' AND avc.capability_id = NEW.capability_id
        ) THEN
            RAISE EXCEPTION 'active capability binding requires the agent''s published version to declare this capability' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
