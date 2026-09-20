using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Contracts;

public interface IContractDocumentRepository
{
    Task<IReadOnlyList<ContractDocumentRecord>> ListAsync(Guid tenantId,Guid departmentId,Guid contractId,CancellationToken ct);
    Task<ContractDocumentRecord?> AddAsync(Guid tenantId,Guid departmentId,Guid actorId,Guid contractId,string documentType,string? referencedCode,string filename,string contentType,string storageKey,string sha256,long sizeBytes,CancellationToken ct);
    Task<ContractDocumentRecord?> FindAsync(Guid tenantId,Guid departmentId,Guid documentId,CancellationToken ct);
    Task<bool> DeleteAsync(Guid tenantId,Guid departmentId,Guid documentId,CancellationToken ct);
}

public sealed class ContractDocumentRepository(NpgsqlConnectionFactory factory):IContractDocumentRepository
{
    private const string Columns="id AS Id,contract_id AS ContractId,uploaded_by AS UploadedBy,document_type AS DocumentType,referenced_code AS ReferencedCode,original_filename AS OriginalFilename,content_type AS ContentType,storage_key AS StorageKey,sha256 AS Sha256,size_bytes AS SizeBytes,created_at AS CreatedAt";
    public async Task<IReadOnlyList<ContractDocumentRecord>> ListAsync(Guid tenantId,Guid departmentId,Guid contractId,CancellationToken ct)
    {await using var c=await factory.OpenAsync(ct);return(await c.QueryAsync<ContractDocumentRecord>(new CommandDefinition($"SELECT {Columns} FROM contract_documents WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@contractId ORDER BY created_at",new{tenantId,departmentId,contractId},cancellationToken:ct))).AsList();}
    public async Task<ContractDocumentRecord?> AddAsync(Guid tenantId,Guid departmentId,Guid actorId,Guid contractId,string documentType,string? referencedCode,string filename,string contentType,string storageKey,string sha256,long sizeBytes,CancellationToken ct)
    {await using var c=await factory.OpenAsync(ct);return await c.QuerySingleOrDefaultAsync<ContractDocumentRecord>(new CommandDefinition($"""
      INSERT INTO contract_documents(contract_id,tenant_id,department_id,uploaded_by,document_type,referenced_code,original_filename,content_type,storage_key,sha256,size_bytes)
      SELECT id,@tenantId,@departmentId,@actorId,@documentType,@referencedCode,@filename,@contentType,@storageKey,@sha256,@sizeBytes FROM contracts
      WHERE id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId AND workflow_state NOT IN ('APPROVED','HANDED_OFF','REJECTED')
      ON CONFLICT(contract_id,sha256) DO UPDATE SET referenced_code=COALESCE(EXCLUDED.referenced_code,contract_documents.referenced_code)
      RETURNING {Columns}
      """,new{tenantId,departmentId,actorId,contractId,documentType,referencedCode,filename,contentType,storageKey,sha256,sizeBytes},cancellationToken:ct));}
    public async Task<ContractDocumentRecord?> FindAsync(Guid tenantId,Guid departmentId,Guid documentId,CancellationToken ct)
    {await using var c=await factory.OpenAsync(ct);return await c.QuerySingleOrDefaultAsync<ContractDocumentRecord>(new CommandDefinition($"SELECT {Columns} FROM contract_documents WHERE id=@documentId AND tenant_id=@tenantId AND department_id=@departmentId",new{tenantId,departmentId,documentId},cancellationToken:ct));}
    public async Task<bool> DeleteAsync(Guid tenantId,Guid departmentId,Guid documentId,CancellationToken ct)
    {await using var c=await factory.OpenAsync(ct);return await c.ExecuteAsync(new CommandDefinition("DELETE FROM contract_documents d USING contracts c WHERE d.id=@documentId AND d.contract_id=c.id AND d.tenant_id=@tenantId AND d.department_id=@departmentId AND c.workflow_state NOT IN ('APPROVED','HANDED_OFF','REJECTED')",new{tenantId,departmentId,documentId},cancellationToken:ct))>0;}
}
