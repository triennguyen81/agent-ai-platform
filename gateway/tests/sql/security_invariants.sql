DO $$
DECLARE
    tenant_a UUID;
    tenant_b UUID;
    first_admin UUID;
    second_admin UUID;
    normal_user UUID;
    tenant_agent UUID;
    super_role INT;
    user_role INT;
BEGIN
    INSERT INTO tenants(code, name) VALUES ('security-a', 'Security A') RETURNING id INTO tenant_a;
    INSERT INTO tenants(code, name) VALUES ('security-b', 'Security B') RETURNING id INTO tenant_b;
    SELECT id INTO super_role FROM roles WHERE name = 'super_admin';
    SELECT id INTO user_role FROM roles WHERE name = 'user';

    INSERT INTO users(email, password_hash) VALUES ('root-one@test', 'x') RETURNING id INTO first_admin;
    INSERT INTO user_roles(user_id, role_id) VALUES (first_admin, super_role);

    INSERT INTO users(email, password_hash) VALUES ('root-two@test', 'x') RETURNING id INTO second_admin;
    BEGIN
        INSERT INTO user_roles(user_id, role_id) VALUES (second_admin, super_role);
        RAISE EXCEPTION 'second super_admin was incorrectly accepted';
    EXCEPTION WHEN unique_violation THEN
        NULL;
    END;

    BEGIN
        DELETE FROM users WHERE id = first_admin;
        RAISE EXCEPTION 'active super_admin was incorrectly deleted';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    BEGIN
        UPDATE users SET is_active = FALSE WHERE id = first_admin;
        RAISE EXCEPTION 'active super_admin was incorrectly disabled';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    BEGIN
        DELETE FROM user_roles WHERE user_id = first_admin AND role_id = super_role;
        RAISE EXCEPTION 'active super_admin was incorrectly demoted';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    INSERT INTO users(email, password_hash, tenant_id)
    VALUES ('user-b@test', 'x', tenant_b) RETURNING id INTO normal_user;
    INSERT INTO user_roles(user_id, role_id) VALUES (normal_user, user_role);
    INSERT INTO agents(tenant_id, code, name, lifecycle_state)
    VALUES (tenant_a, 'security-agent', 'Security Agent', 'production') RETURNING id INTO tenant_agent;

    BEGIN
        INSERT INTO agent_assignments(tenant_id, agent_id, user_id)
        VALUES (tenant_a, tenant_agent, normal_user);
        RAISE EXCEPTION 'cross-tenant assignment was incorrectly accepted';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;
END $$;
