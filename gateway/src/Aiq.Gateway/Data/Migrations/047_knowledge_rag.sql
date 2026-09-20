-- Extend the canonical notes/agent_knowledge model, never copy it into a second document store.
CREATE TABLE knowledge_bases (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid REFERENCES tenants(id),
 department_id uuid, name text NOT NULL, description text NOT NULL DEFAULT '',
 status text NOT NULL DEFAULT 'active' CHECK(status IN ('active','disabled','deleted')),
 created_by uuid REFERENCES users(id) ON DELETE SET NULL, created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id), UNIQUE(id,tenant_id)
);
ALTER TABLE notes ADD COLUMN knowledge_base_id uuid REFERENCES knowledge_bases(id),
 ADD COLUMN mime_type text NOT NULL DEFAULT 'text/markdown',
 ADD COLUMN source text NOT NULL DEFAULT 'note', ADD COLUMN current_version_id uuid,
 ADD COLUMN index_verified_at timestamptz;
CREATE TABLE document_versions (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), document_id uuid NOT NULL,
 version integer NOT NULL, content_hash text NOT NULL, extracted_text text NOT NULL,
 title text NOT NULL, source text NOT NULL, mime_type text NOT NULL,
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','indexed','failed')),
 created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(document_id,version), UNIQUE(document_id,id)
);
ALTER TABLE notes ADD CONSTRAINT notes_current_version_fk FOREIGN KEY(id,current_version_id) REFERENCES document_versions(document_id,id);
CREATE TABLE document_chunks (
 id uuid PRIMARY KEY, document_version_id uuid NOT NULL REFERENCES document_versions(id),
 tenant_id uuid, knowledge_base_id uuid NOT NULL REFERENCES knowledge_bases(id), document_id uuid NOT NULL,
 chunk_index integer NOT NULL, content text NOT NULL, metadata jsonb NOT NULL DEFAULT '{}',
 embedding_status text NOT NULL DEFAULT 'pending' CHECK(embedding_status IN ('pending','indexed','failed')),
 FOREIGN KEY(document_id,document_version_id) REFERENCES document_versions(document_id,id),
 FOREIGN KEY(knowledge_base_id,tenant_id) REFERENCES knowledge_bases(id,tenant_id),
 UNIQUE(document_version_id,chunk_index)
);
CREATE TABLE agent_knowledge_bases (
 agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
 tenant_id uuid NOT NULL REFERENCES tenants(id), knowledge_base_id uuid NOT NULL REFERENCES knowledge_bases(id),
 attached_by uuid REFERENCES users(id) ON DELETE SET NULL, created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY(knowledge_base_id,tenant_id) REFERENCES knowledge_bases(id,tenant_id),
 PRIMARY KEY(agent_id,tenant_id,knowledge_base_id)
);
CREATE TABLE indexing_jobs (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), document_id uuid NOT NULL, tenant_id uuid,
 document_version_id uuid REFERENCES document_versions(id), kind text NOT NULL DEFAULT 'index' CHECK(kind IN ('index','cleanup')),
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','running','completed','failed')),
 error text, attempts integer NOT NULL DEFAULT 0, started_at timestamptz, completed_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY(document_id,document_version_id) REFERENCES document_versions(document_id,id)
);
CREATE INDEX indexing_jobs_pending ON indexing_jobs(status,created_at);
CREATE INDEX document_chunks_scope ON document_chunks(tenant_id,knowledge_base_id,document_version_id);
CREATE INDEX notes_kb ON notes(knowledge_base_id);
CREATE UNIQUE INDEX notes_uploaded_filename ON notes(knowledge_base_id,title) WHERE source='upload';
-- One compatibility KB per legacy note deliberately preserves exact attachment permissions.
INSERT INTO knowledge_bases(id,tenant_id,department_id,name,created_by)
 SELECT id,tenant_id,department_id,title,created_by FROM notes;
UPDATE notes SET knowledge_base_id=id;
ALTER TABLE notes ALTER COLUMN knowledge_base_id SET NOT NULL;
-- BEFORE trigger lets every existing Notes/Knowledge API create KB-backed documents unchanged.
CREATE FUNCTION knowledge_note_prepare() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='UPDATE' AND (NEW.knowledge_base_id IS DISTINCT FROM OLD.knowledge_base_id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.department_id IS DISTINCT FROM OLD.department_id) THEN
  RAISE EXCEPTION 'Knowledge ownership is immutable';
 END IF;
 IF NEW.knowledge_base_id IS NULL THEN
  INSERT INTO knowledge_bases(id,tenant_id,department_id,name,created_by)
   VALUES(NEW.id,NEW.tenant_id,NEW.department_id,NEW.title,NEW.created_by);
  NEW.knowledge_base_id := NEW.id;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM knowledge_bases k WHERE k.id=NEW.knowledge_base_id
     AND k.tenant_id IS NOT DISTINCT FROM NEW.tenant_id
     AND k.department_id IS NOT DISTINCT FROM NEW.department_id) THEN
  RAISE EXCEPTION 'Knowledge document ownership mismatch';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER knowledge_note_prepare BEFORE INSERT OR UPDATE ON notes FOR EACH ROW EXECUTE FUNCTION knowledge_note_prepare();
CREATE FUNCTION knowledge_note_version() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE vid uuid;
BEGIN
 IF TG_OP='DELETE' THEN
  INSERT INTO indexing_jobs(document_id,tenant_id,kind) VALUES(OLD.id,OLD.tenant_id,'cleanup');
  RETURN OLD;
 END IF;
 IF TG_OP='INSERT' OR NEW.content IS DISTINCT FROM OLD.content OR NEW.title IS DISTINCT FROM OLD.title THEN
  INSERT INTO document_versions(document_id,version,content_hash,extracted_text,title,source,mime_type)
   SELECT NEW.id,COALESCE(MAX(version),0)+1,encode(sha256(convert_to(NEW.title||E'\n'||NEW.content,'UTF8')),'hex'),NEW.content,NEW.title,NEW.source,NEW.mime_type
   FROM document_versions WHERE document_id=NEW.id RETURNING id INTO vid;
  INSERT INTO indexing_jobs(document_id,document_version_id) VALUES(NEW.id,vid);
 END IF;
 IF TG_OP='UPDATE' AND NEW.is_archived IS DISTINCT FROM OLD.is_archived THEN
  IF NEW.is_archived THEN
   INSERT INTO indexing_jobs(document_id,tenant_id,kind) VALUES(NEW.id,NEW.tenant_id,'cleanup');
  ELSE
   INSERT INTO indexing_jobs(document_id,document_version_id)
    SELECT NEW.id,id FROM document_versions WHERE document_id=NEW.id ORDER BY version DESC LIMIT 1;
  END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER knowledge_note_version AFTER INSERT OR UPDATE OR DELETE ON notes FOR EACH ROW EXECUTE FUNCTION knowledge_note_version();
INSERT INTO document_versions(document_id,version,content_hash,extracted_text,title,source,mime_type)
 SELECT id,1,encode(sha256(convert_to(title||E'\n'||content,'UTF8')),'hex'),content,title,source,mime_type FROM notes;
INSERT INTO indexing_jobs(document_id,document_version_id) SELECT document_id,id FROM document_versions;
-- The original attachment table remains authoritative for compatibility KBs.
-- No broad per-agent migration grant is made: detaching a legacy note still revokes access.
CREATE FUNCTION knowledge_base_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.status IS DISTINCT FROM OLD.status THEN
  IF NEW.status <> 'active' THEN
   INSERT INTO indexing_jobs(document_id,tenant_id,kind) SELECT id,tenant_id,'cleanup' FROM notes WHERE knowledge_base_id=NEW.id;
  ELSE
   INSERT INTO indexing_jobs(document_id,document_version_id)
    SELECT n.id,v.id FROM notes n JOIN LATERAL
      (SELECT id FROM document_versions WHERE document_id=n.id ORDER BY version DESC LIMIT 1) v ON true
    WHERE n.knowledge_base_id=NEW.id AND NOT n.is_archived;
  END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER knowledge_base_cleanup AFTER UPDATE ON knowledge_bases FOR EACH ROW EXECUTE FUNCTION knowledge_base_cleanup();

CREATE FUNCTION knowledge_version_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (NEW.document_id,NEW.version,NEW.content_hash,NEW.extracted_text,NEW.title,NEW.source,NEW.mime_type,NEW.created_at)
   IS DISTINCT FROM (OLD.document_id,OLD.version,OLD.content_hash,OLD.extracted_text,OLD.title,OLD.source,OLD.mime_type,OLD.created_at) THEN
  RAISE EXCEPTION 'Document version content is immutable';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER knowledge_version_immutable BEFORE UPDATE ON document_versions FOR EACH ROW EXECUTE FUNCTION knowledge_version_immutable();
