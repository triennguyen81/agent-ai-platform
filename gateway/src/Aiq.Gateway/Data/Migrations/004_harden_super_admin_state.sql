CREATE OR REPLACE FUNCTION protect_super_admin_state_change()
RETURNS TRIGGER AS $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM user_roles ur
        JOIN roles r ON r.id = ur.role_id
        WHERE ur.user_id = OLD.id AND r.name = 'super_admin'
    ) AND (
        (OLD.is_active AND NOT NEW.is_active)
        OR NEW.tenant_id IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'the active super_admin is system-scoped and cannot be disabled or assigned to a tenant'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_super_admin_state_change ON users;
CREATE TRIGGER trg_protect_super_admin_state_change
BEFORE UPDATE OF tenant_id, is_active ON users
FOR EACH ROW EXECUTE FUNCTION protect_super_admin_state_change();
