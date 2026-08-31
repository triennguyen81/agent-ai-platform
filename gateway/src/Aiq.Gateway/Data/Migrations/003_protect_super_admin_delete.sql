CREATE OR REPLACE FUNCTION protect_active_super_admin_delete()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.is_active AND EXISTS (
        SELECT 1
        FROM user_roles ur
        JOIN roles r ON r.id = ur.role_id
        WHERE ur.user_id = OLD.id AND r.name = 'super_admin'
    ) THEN
        RAISE EXCEPTION 'the active super_admin cannot be deleted'
            USING ERRCODE = '23514';
    END IF;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_active_super_admin_delete ON users;
CREATE TRIGGER trg_protect_active_super_admin_delete
BEFORE DELETE ON users
FOR EACH ROW EXECUTE FUNCTION protect_active_super_admin_delete();
