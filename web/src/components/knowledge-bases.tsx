"use client";

import { useEffect, useState } from "react";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { KnowledgeDocumentForm, documentRequest, type DocumentMode } from "@/components/knowledge-document-form";

type Base = { id: string; name: string; status: string; documentCount: number };
type Document = { id: string; title: string; tags: string[]; versionId: string; version: number; currentVersionId: string | null; indexingStatus: string; error: string | null; cleanupStatus: string | null; cleanupError: string | null; isArchived: boolean };
type Agent = { id: string; name: string; attached: boolean };
async function api(path = "", init?: RequestInit) {
  const response = await fetch(`/api/knowledge-bases${path}`, init);
  if (!response.ok) throw new Error(`Không thể thực hiện yêu cầu Knowledge (${response.status}).`);
  return response.status === 204 ? null : response.json();
}

export function KnowledgeBases() {
  const [bases, setBases] = useState<Base[]>([]);
  const [selected, setSelected] = useState("");
  const [documents, setDocuments] = useState<Document[]>([]);
  const [agents, setAgents] = useState<Agent[]>([]);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(false);
  const [adding, setAdding] = useState(false);
  const [mode, setMode] = useState<DocumentMode>("upload");
  const [revision, setRevision] = useState(0);
  const selectedBase = bases.find((base) => base.id === selected);

  // Cancel old selection requests so a slow response cannot put another KB's
  // documents or version choices into the currently selected KB's form.
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function refresh() {
      try {
        const init = { signal: controller.signal };
        const [result, docs, attached] = await Promise.all([
          api("", init),
          selected ? api(`/${selected}/documents`, init) : Promise.resolve({ items: [] }),
          selected ? api(`/${selected}/agents`, init) : Promise.resolve({ items: [] }),
        ]);
        if (!controller.signal.aborted) {
          setBases(result.items); setDocuments(docs.items); setAgents(attached.items);
        }
      } catch (e) {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Không thể tải Knowledge.");
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
          timer = setTimeout(refresh, 5000);
        }
      }
    }
    void refresh();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [selected, revision]);

  function selectBase(id: string) {
    setSelected(id); setDocuments([]); setAgents([]); setAdding(false);
    setMode("upload"); setError(""); setMessage(""); setLoading(Boolean(id));
  }
  async function mutate(work: () => Promise<unknown>): Promise<boolean> {
    setBusy(true); setError(""); setMessage("");
    try { await work(); setRevision((value) => value + 1); return true; }
    catch (e) { setError(e instanceof Error ? e.message : "Không thể lưu thay đổi."); return false; }
    finally { setBusy(false); }
  }
  const grouped = new Map<string, Document[]>();
  for (const doc of documents) grouped.set(doc.id, [...(grouped.get(doc.id) ?? []), doc]);
  const currentDocuments = [...grouped.values()].map((versions) => versions[0]);

  return <section className="space-y-4 rounded-[var(--radius)] border p-5">
    <h2 className="font-serif text-xl">Knowledge Bases</h2>
    <p className="text-sm text-[var(--color-muted-fg)]">Tài liệu chính sách, quy trình và hướng dẫn. Số dư và giao dịch kế toán được tra cứu bằng công cụ nghiệp vụ.</p>
    {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
    {message && <p role="status" className="text-sm text-emerald-700">{message}</p>}
    <form className="flex gap-2" onSubmit={(event) => {
      event.preventDefault(); const form = event.currentTarget; const data = new FormData(form);
      void mutate(async () => {
        const result = await api("", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: data.get("name"), description: data.get("description") }) });
        selectBase(result.id); form.reset();
      });
    }}>
      <Input name="name" aria-label="Tên Knowledge Base" placeholder="Tên Knowledge Base" required maxLength={300} disabled={busy} />
      <Input name="description" aria-label="Mô tả" placeholder="Mô tả" maxLength={4000} disabled={busy} />
      <Button disabled={busy}>Tạo KB</Button>
    </form>
    <Label htmlFor="kb-select">Knowledge Base</Label>
    <select id="kb-select" disabled={busy} className="w-full rounded border bg-[var(--color-bg)] p-2" value={selected} onChange={(e) => selectBase(e.target.value)}>
      <option value="">Chọn Knowledge Base</option>
      {bases.filter((base) => base.status !== "deleted").map((base) => <option key={base.id} value={base.id}>{base.name} · {base.status} · {base.documentCount} tài liệu</option>)}
    </select>
    {selectedBase && <div className="flex gap-2">
      <Button disabled={busy} variant="outline" onClick={() => void mutate(() => api(`/${selected}`, { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ status: selectedBase.status === "active" ? "disabled" : "active" }) }))}>Bật / tắt KB</Button>
      <Button disabled={busy} variant="outline" onClick={() => { if (confirm("Xóa Knowledge Base khỏi truy xuất? Lịch sử được giữ lại.")) void mutate(async () => { await api(`/${selected}`, { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ status: "deleted" }) }); selectBase(""); }); }}>Xóa KB</Button>
    </div>}
    <div className="space-y-2">
      {selectedBase ? <p className="text-sm font-medium">Đang thêm vào KB: {selectedBase.name}</p>
        : <p className="text-sm text-[var(--color-muted-fg)]">Chọn Knowledge Base trước khi thêm tài liệu.</p>}
      <Button type="button" disabled={!selectedBase || busy || loading} aria-expanded={adding} aria-controls="kb-add-document" onClick={() => setAdding((value) => !value)}>
        <Plus className="mr-1 size-4" /> Thêm tài liệu
      </Button>
    </div>
    {adding && selectedBase && <KnowledgeDocumentForm key={selected} mode={mode} onModeChange={setMode}
      documents={currentDocuments} busy={busy} onSubmit={(event) => {
        event.preventDefault();
        const request = documentRequest(mode, new FormData(event.currentTarget));
        void mutate(async () => {
          await api(`/${selected}/documents`, request);
          setAdding(false); setMessage("Đã lưu tài liệu. Theo dõi trạng thái lập chỉ mục trong danh sách bên dưới.");
        });
      }} />}
    {selected && <>
      <div className="space-y-2" aria-busy={loading}>
        <h3 className="font-medium">Tài liệu / phiên bản</h3>
        {loading ? <p className="text-sm">Đang tải tài liệu…</p> : !documents.length && <p className="text-sm">Chưa có tài liệu.</p>}
        {[...grouped.values()].map((versions) => {
          const doc = versions[0];
          return <div className="space-y-2 rounded border p-3 text-sm" key={doc.id}>
            <p className="font-medium">{doc.title}{doc.isArchived ? " · đã tắt" : ""}</p>
            {(doc.tags ?? []).length > 0 && <p className="text-xs text-[var(--color-muted-fg)]">{doc.tags.join(", ")}</p>}
            <p>v{doc.version} · {doc.indexingStatus}{doc.currentVersionId === doc.versionId ? " · đang sử dụng" : ""}</p>
            {doc.error && <p role="alert" className="text-red-600">Lập chỉ mục thất bại: {doc.error}. Chọn lập chỉ mục lại để thử lại.</p>}
            {doc.cleanupError && <p role="alert" className="text-red-600">Dọn chỉ mục: {doc.cleanupStatus} ({doc.cleanupError}). Hệ thống sẽ tự thử lại.</p>}
            <details>
              <summary className="cursor-pointer">Lịch sử phiên bản ({versions.length})</summary>
              <ul className="mt-2 space-y-1">
                {versions.map((version) => <li key={version.versionId}>v{version.version} · {version.indexingStatus}{version.currentVersionId === version.versionId ? " · đang sử dụng" : ""}</li>)}
              </ul>
            </details>
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="outline" disabled={busy || doc.isArchived} onClick={() => void mutate(() => api(`/${selected}/documents/${doc.id}/reindex`, { method: "POST" }))}>Lập chỉ mục lại</Button>
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void mutate(() => api(`/${selected}/documents/${doc.id}/${doc.isArchived ? "restore" : "disable"}`, { method: "POST" }))}>{doc.isArchived ? "Khôi phục" : "Tắt tài liệu"}</Button>
              <Button size="sm" variant="outline" disabled={busy} onClick={() => { if (confirm("Xóa tài liệu khỏi truy xuất? Lịch sử được giữ lại.")) void mutate(() => api(`/${selected}/documents/${doc.id}/delete`, { method: "POST" })); }}>Xóa</Button>
            </div>
          </div>;
        })}
      </div>
      <div className="space-y-2"><h3 className="font-medium">Gắn Knowledge Base vào Agent</h3>
        {agents.map((agent) => <label className="flex items-center gap-2 text-sm" key={agent.id}>
          <input type="checkbox" checked={agent.attached} disabled={busy} onChange={(e) => { const attach = e.target.checked; void mutate(() => api(`/${selected}/agents/${agent.id}`, { method: attach ? "PUT" : "DELETE" })); }} />{agent.name}
        </label>)}
        {!loading && !agents.length && <p className="text-sm">Chưa có Agent do phòng ban sở hữu.</p>}
      </div>
    </>}
  </section>;
}
