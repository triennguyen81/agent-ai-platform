BEGIN;

DO $$
DECLARE
    tenant_id_value UUID;
    owner_id_value UUID;
    agent_id_value UUID;
    version_id_value UUID;
BEGIN
    SELECT u.tenant_id, u.id INTO tenant_id_value, owner_id_value
    FROM users u
    WHERE u.tenant_id IS NOT NULL AND u.is_active
    ORDER BY u.created_at
    LIMIT 1;

    IF tenant_id_value IS NULL THEN
        RAISE EXCEPTION 'Phase 3A invariant test requires one active tenant user';
    END IF;

    INSERT INTO agents (tenant_id, code, name, type, owner_user_id)
    VALUES (tenant_id_value, 'phase3a-invariant-test', 'Phase 3A Invariant Test', 'domain', owner_id_value)
    RETURNING id INTO agent_id_value;

    SELECT current_draft_version_id INTO version_id_value
    FROM agents WHERE id = agent_id_value;

    IF version_id_value IS NULL OR NOT EXISTS (
        SELECT 1 FROM agent_versions
        WHERE id = version_id_value AND agent_id = agent_id_value
          AND version = 1 AND status = 'draft'
    ) THEN
        RAISE EXCEPTION 'legacy agent insert did not create draft version 1';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM agent_tenant_assignments
        WHERE agent_id = agent_id_value AND tenant_id = tenant_id_value AND status = 'active'
    ) THEN
        RAISE EXCEPTION 'legacy agent insert did not create active tenant availability';
    END IF;

    UPDATE agent_versions
    SET status = 'published', published_by = owner_id_value, published_at = NOW()
    WHERE id = version_id_value;
    UPDATE agents
    SET current_draft_version_id = NULL, published_version_id = version_id_value
    WHERE id = agent_id_value;

    BEGIN
        UPDATE agent_versions SET temperature = 0.75 WHERE id = version_id_value;
        RAISE EXCEPTION 'published version was mutable';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    BEGIN
        DELETE FROM agent_versions WHERE id = version_id_value;
        RAISE EXCEPTION 'published version was deletable';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;
END $$;

ROLLBACK;
