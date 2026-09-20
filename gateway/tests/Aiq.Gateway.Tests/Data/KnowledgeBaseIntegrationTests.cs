using Aiq.Gateway.Data;
using Aiq.Gateway.Notes;
using Aiq.Gateway.Endpoints;
using System.ComponentModel.DataAnnotations;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

public sealed class KnowledgeBaseIntegrationTests
{
    [Fact]
    public void DirectEntryRejectsEmptyTextAndOversizedMetadata()
    {
        var invalid = new[] {
            new KnowledgeBaseEndpoints.ManualDocumentRequest("", "Policy"),
            new KnowledgeBaseEndpoints.ManualDocumentRequest("Policy", " "),
            new KnowledgeBaseEndpoints.ManualDocumentRequest(new string('x',301), "Policy"),
            new KnowledgeBaseEndpoints.ManualDocumentRequest("Policy", "Policy", new string[51]),
        };
        foreach (var body in invalid)
            Assert.False(Validator.TryValidateObject(body,new ValidationContext(body),[],true));
        var valid=new KnowledgeBaseEndpoints.ManualDocumentRequest("Policy","Policy text",["ar"]);
        Assert.True(Validator.TryValidateObject(valid,new ValidationContext(valid),[],true));
    }

    [Fact]
    public async Task CanonicalNotesCreateVersionsAndDepartmentOwnershipGatesAttachments()
    {
        var dsn = Environment.GetEnvironmentVariable("AIQ_TEST_POSTGRES");
        if (string.IsNullOrEmpty(dsn)) return;
        var schema = "kb_gateway_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(dsn);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {schema}");
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(dsn) { SearchPath = schema }.ConnectionString;
            var factory = new NpgsqlConnectionFactory(scoped);
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync("""
                CREATE TABLE tenants(id uuid PRIMARY KEY);
                CREATE TABLE users(id uuid PRIMARY KEY);
                CREATE TABLE departments(id uuid PRIMARY KEY,tenant_id uuid NOT NULL,UNIQUE(id,tenant_id));
                CREATE TABLE agents(id uuid PRIMARY KEY,name text,owning_tenant_id uuid,department_id uuid,v2_scope text,v2_migration_status text);
                CREATE TABLE notes(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),tenant_id uuid,department_id uuid,
                  user_id uuid,created_by uuid,title text NOT NULL,content text NOT NULL,tags text[] NOT NULL DEFAULT '{}',
                  scope text NOT NULL DEFAULT 'department',is_archived boolean NOT NULL DEFAULT false,
                  created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE agent_knowledge(agent_id uuid,note_id uuid,PRIMARY KEY(agent_id,note_id));
                """);
            var path = Path.Combine(AppContext.BaseDirectory,"Data","Migrations","047_knowledge_rag.sql");
            await conn.ExecuteAsync(await File.ReadAllTextAsync(path));
            var tenant=Guid.NewGuid(); var department=Guid.NewGuid(); var actor=Guid.NewGuid(); var agent=Guid.NewGuid();
            await conn.ExecuteAsync("INSERT INTO tenants VALUES(@tenant); INSERT INTO users VALUES(@actor); INSERT INTO departments VALUES(@department,@tenant)",new { tenant,actor,department });
            await conn.ExecuteAsync("INSERT INTO agents VALUES(@agent,'Policy agent',@tenant,@department,'department','resolved')",new { agent,tenant,department });
            var repo=new KnowledgeBaseRepository(factory);
            var kb=await repo.CreateAsync(tenant,department,actor,"Policy","AR procedures",default);
            Assert.True(await repo.OwnsAsync(tenant,department,kb,default));
            Assert.False(await repo.OwnsAsync(Guid.NewGuid(),department,kb,default));
            Assert.False(await repo.OwnsAsync(tenant,Guid.NewGuid(),kb,default));
            var doc=await repo.SaveDocumentAsync(tenant,department,actor,kb,null,"Policy.md","# Policy\nContact after 30 days.","text/markdown",default);
            Assert.NotNull(doc);
            Assert.Equal(1,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_versions WHERE document_id=@doc",new {doc}));
            await repo.SaveDocumentAsync(tenant,department,actor,kb,doc,"Policy.md","# Policy\nContact after 35 days.","text/markdown",default);
            Assert.Equal(2,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_versions WHERE document_id=@doc",new {doc}));
            var sameDocument = await repo.SaveDocumentAsync(tenant,department,actor,kb,null,"Policy.md","# Policy\nContact after 35 days.","text/markdown",default);
            Assert.Equal(doc,sameDocument);
            Assert.Equal(2,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_versions WHERE document_id=@doc",new {doc}));
            // Direct entry stays in the selected KB and uses the SAME version/job triggers.
            var beforeBases = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM knowledge_bases");
            var manual = await repo.SaveDocumentAsync(tenant,department,actor,kb,null,"Direct policy","First policy text","text/markdown",default,["policy","ar"],"manual");
            Assert.NotNull(manual);
            Assert.Equal(beforeBases,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM knowledge_bases"));
            Assert.Equal(kb,await conn.ExecuteScalarAsync<Guid>("SELECT knowledge_base_id FROM notes WHERE id=@manual",new {manual}));
            Assert.Equal(new[] {"policy","ar"},await conn.ExecuteScalarAsync<string[]>("SELECT tags FROM notes WHERE id=@manual",new {manual}));
            Assert.Equal("manual",await conn.ExecuteScalarAsync<string>("SELECT source FROM notes WHERE id=@manual",new {manual}));
            await repo.SaveDocumentAsync(tenant,department,actor,kb,manual,"Direct policy","Updated policy text","text/markdown",default,["updated"],"manual");
            Assert.Equal(2,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_versions WHERE document_id=@manual",new {manual}));
            Assert.Equal(2,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM indexing_jobs WHERE document_id=@manual AND kind='index'",new {manual}));
            // File replacement can update a manually entered document without losing tags.
            await repo.SaveDocumentAsync(tenant,department,actor,kb,manual,"Direct policy","Uploaded replacement text","text/plain",default);
            Assert.Equal(new[] {"updated"},await conn.ExecuteScalarAsync<string[]>("SELECT tags FROM notes WHERE id=@manual",new {manual}));
            Assert.Equal(3,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_versions WHERE document_id=@manual",new {manual}));
            Assert.Null(await repo.SaveDocumentAsync(Guid.NewGuid(),department,actor,kb,null,"Forbidden","Text","text/markdown",default,[],"manual"));
            Assert.Null(await repo.SaveDocumentAsync(tenant,Guid.NewGuid(),actor,kb,null,"Forbidden","Text","text/markdown",default,[],"manual"));
            var otherKb=await repo.CreateAsync(tenant,department,actor,"Other KB","",default);
            Assert.Null(await repo.SaveDocumentAsync(tenant,department,actor,otherKb,manual,"Forbidden","Text","text/markdown",default,[],"manual"));
            // Old Notes callers still receive a compatibility KB and an indexing job.
            var legacy=await conn.ExecuteScalarAsync<Guid>("INSERT INTO notes(tenant_id,department_id,title,content) VALUES(@tenant,@department,'Legacy','Legacy text') RETURNING id",new {tenant,department});
            Assert.Equal(legacy,await conn.ExecuteScalarAsync<Guid>("SELECT knowledge_base_id FROM notes WHERE id=@legacy",new {legacy}));
            Assert.Equal(1,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM indexing_jobs WHERE document_id=@legacy",new {legacy}));
            Assert.True(await repo.AttachAsync(tenant,department,actor,kb,agent,true,default));
            Assert.False(await repo.AttachAsync(tenant,Guid.NewGuid(),actor,kb,agent,true,default));
            Assert.True(await repo.DocumentActionAsync(kb,doc!.Value,"disable",default));
            Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT is_archived FROM notes WHERE id=@doc",new {doc}));
            Assert.True(await repo.DocumentActionAsync(kb,doc.Value,"restore",default));
            Assert.True(await repo.DocumentActionAsync(kb,doc.Value,"reindex",default));
            Assert.True(await repo.AttachAsync(tenant,department,actor,kb,agent,false,default));
            Assert.Equal(0,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM agent_knowledge_bases"));
        }
        finally { await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE"); }
    }
}
