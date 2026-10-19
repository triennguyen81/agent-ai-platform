"use client";

import { useMemo, useState } from "react";
import { FileCog, FlaskConical, Lock, Plus, Save, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

// Agent Builder → Xử lý chứng từ (configurable document processing).
//
// What an ADMIN configures here is how a DRAFT Agent version EXTRACTS
// documents: which SYSTEM document types it accepts, their field schema,
// extraction instructions, workflow and skills. The accounting handler of a
// system type (sales invoice, cash receipt, …) is fixed server-side and shown
// read-only — there is no input anywhere on this screen for accounts, Dr/Cr
// or posting logic. Published versions are immutable (rendered read-only;
// the Gateway also rejects edits). Every save goes through the Gateway's
// structured validation.

export interface DocumentField {
  name: string;
  label: string;
  type: string;
  required: boolean;
  description: string | null;
}

export interface DocumentTypeConfig {
  code: string;
  enabled: boolean;
  displayName: string;
  fields: DocumentField[];
  extractionInstructions: string;
  workflowId: string | null;
  skillCodes: string[];
  accountingHandler: string;
}

export interface CatalogDocumentType {
  code: string;
  displayName: string;
  accountingHandler: string;
  defaultFields: DocumentField[];
  defaultInstructions: string;
}

interface Option { id: string; name: string; scope?: string }
interface SkillOption { code: string; name: string; scope?: string }
interface SampleVoucher { id: string; label: string }

const selectClass = "w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm";

const SUB_TABS = [
  { value: "identity", label: "Identity" },
  { value: "schema", label: "Schema" },
  { value: "instructions", label: "Hướng dẫn trích xuất" },
  { value: "workflow", label: "Workflow" },
  { value: "skills", label: "Skills" },
  { value: "test", label: "Test" },
] as const;
type SubTab = (typeof SUB_TABS)[number]["value"];

function fromCatalog(t: CatalogDocumentType): DocumentTypeConfig {
  return {
    code: t.code,
    enabled: true,
    displayName: t.displayName,
    fields: t.defaultFields.map((f) => ({ ...f, description: f.description ?? null })),
    extractionInstructions: t.defaultInstructions,
    workflowId: null,
    skillCodes: [],
    accountingHandler: t.accountingHandler,
  };
}

export function DocumentProcessingEditor({
  agentId,
  version,
  initialTypes,
  catalog,
  fieldTypes,
  workflows,
  skills,
  sampleVouchers,
}: {
  agentId: string;
  version: { id: string; version: number; status: string } | null;
  initialTypes: DocumentTypeConfig[];
  catalog: CatalogDocumentType[];
  fieldTypes: string[];
  workflows: Option[];
  skills: SkillOption[];
  sampleVouchers: SampleVoucher[];
}) {
  const editable = version?.status === "draft";
  const [types, setTypes] = useState<DocumentTypeConfig[]>(() =>
    // Keep catalog order; unconfigured system types appear as disabled rows.
    initialTypes.length === 0
      ? []
      : catalog.map((c) => initialTypes.find((t) => t.code === c.code) ?? { ...fromCatalog(c), enabled: false }),
  );
  const [selected, setSelected] = useState<string | null>(types[0]?.code ?? null);
  const [subTab, setSubTab] = useState<SubTab>("schema");
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string; errors?: string[] } | null>(null);
  const current = useMemo(() => types.find((t) => t.code === selected) ?? null, [types, selected]);

  function update(code: string, patch: Partial<DocumentTypeConfig>) {
    setTypes((all) => all.map((t) => (t.code === code ? { ...t, ...patch } : t)));
    setMessage(null);
  }
  function updateField(index: number, patch: Partial<DocumentField>) {
    if (!current) return;
    update(current.code, { fields: current.fields.map((f, i) => (i === index ? { ...f, ...patch } : f)) });
  }

  async function save() {
    if (!version) return;
    setSaving(true);
    setMessage(null);
    try {
      const res = await fetch(`/api/department-agents/${agentId}/document-processing`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          versionId: version.id,
          types: types.map((t) => ({
            code: t.code,
            enabled: t.enabled,
            displayName: t.displayName,
            fields: t.fields,
            extractionInstructions: t.extractionInstructions,
            workflowId: t.workflowId,
            skillCodes: t.skillCodes,
          })),
        }),
      });
      const body = (await res.json().catch(() => null)) as
        | { errors?: Record<string, string[]>; error?: string }
        | null;
      if (res.ok) setMessage({ ok: true, text: `Đã lưu cấu hình xử lý chứng từ vào draft v${version.version}.` });
      else
        setMessage({
          ok: false,
          text: body?.error ?? `Không lưu được (HTTP ${res.status}).`,
          errors: body?.errors ? Object.values(body.errors).flat() : undefined,
        });
    } catch {
      setMessage({ ok: false, text: "Không kết nối được tới Gateway." });
    } finally {
      setSaving(false);
    }
  }

  if (!version) {
    return <p className="text-sm text-[var(--color-muted-fg)]">Lưu draft trước để cấu hình xử lý chứng từ.</p>;
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1 text-xs text-[var(--color-muted-fg)]">
          <p>
            Cấu hình của <span className="font-medium text-[var(--color-fg)]">v{version.version}</span> ({version.status}) —{" "}
            {types.length === 0 ? "đang dùng trích xuất mặc định (legacy)." : "nguồn cấu hình: manifest."}
          </p>
          <p>
            Bút toán, công nợ, thanh toán và xác minh thuế luôn do hệ thống xử lý theo loại chứng từ — không cấu hình ở đây.
          </p>
        </div>
        {editable ? (
          <div className="flex gap-2">
            {types.length === 0 && (
              <Button type="button" size="sm" variant="outline" onClick={() => {
                const loaded = catalog.map(fromCatalog);
                setTypes(loaded);
                setSelected(loaded[0]?.code ?? null);
              }}>
                <FileCog className="size-3.5" /> Nạp cấu hình hệ thống mặc định
              </Button>
            )}
            {types.length > 0 && (
              <Button type="button" size="sm" onClick={save} disabled={saving}>
                <Save className="size-3.5" /> {saving ? "Đang lưu..." : "Lưu cấu hình chứng từ"}
              </Button>
            )}
          </div>
        ) : (
          <span className="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs text-[var(--color-muted-fg)]">
            <Lock className="size-3" /> Version đã publish là bất biến — tạo draft mới để sửa
          </span>
        )}
      </div>

      {message && (
        <div className={`rounded border px-3 py-2 text-sm ${message.ok ? "border-emerald-200 bg-emerald-50 text-emerald-700" : "border-red-200 bg-red-50 text-red-700"}`}>
          <p>{message.text}</p>
          {message.errors && (
            <ul className="mt-1 list-disc pl-5 font-mono text-xs">
              {message.errors.map((e) => <li key={e}>{e}</li>)}
            </ul>
          )}
        </div>
      )}

      {types.length > 0 && (
        <div className="grid gap-4 lg:grid-cols-[260px_1fr]">
          <div className="space-y-1 rounded border p-2">
            <p className="px-1 pb-1 text-xs font-medium uppercase tracking-wider text-[var(--color-muted-fg)]">Loại chứng từ hỗ trợ</p>
            {types.map((t) => (
              <div key={t.code} className={`flex items-center gap-2 rounded px-2 py-1.5 text-sm ${selected === t.code ? "bg-[var(--color-muted)]" : ""}`}>
                <input
                  type="checkbox"
                  aria-label={`Bật ${t.displayName}`}
                  checked={t.enabled}
                  disabled={!editable}
                  onChange={(e) => update(t.code, { enabled: e.target.checked })}
                />
                <button type="button" className="flex-1 text-left" onClick={() => setSelected(t.code)}>
                  <span className={t.enabled ? "" : "text-[var(--color-muted-fg)] line-through"}>{t.displayName}</span>
                  <span className="block font-mono text-[10px] text-[var(--color-muted-fg)]">{t.code}</span>
                </button>
              </div>
            ))}
          </div>

          {current && (
            <div className="space-y-3 rounded border p-4">
              <div className="flex flex-wrap gap-1 border-b pb-2">
                {SUB_TABS.map((s) => (
                  <button
                    key={s.value}
                    type="button"
                    onClick={() => setSubTab(s.value)}
                    className={`rounded px-2.5 py-1 text-sm ${subTab === s.value ? "bg-[var(--color-fg)] text-[var(--color-bg)]" : "hover:bg-[var(--color-muted)]"}`}
                  >
                    {s.label}
                  </button>
                ))}
              </div>

              {subTab === "identity" && (
                <div className="grid gap-3 sm:grid-cols-2">
                  <div className="space-y-1.5">
                    <Label>Mã loại (hệ thống)</Label>
                    <Input value={current.code} disabled readOnly />
                  </div>
                  <div className="space-y-1.5">
                    <Label htmlFor="dp-name">Tên hiển thị</Label>
                    <Input id="dp-name" value={current.displayName} disabled={!editable}
                      onChange={(e) => update(current.code, { displayName: e.target.value })} />
                  </div>
                  <div className="space-y-1.5 sm:col-span-2">
                    <Label>Bộ xử lý nghiệp vụ (cố định phía server)</Label>
                    <Input value={current.accountingHandler} disabled readOnly />
                    <p className="text-xs text-[var(--color-muted-fg)]">
                      Không thể thay đổi — cấu hình chỉ quyết định trích xuất; hạch toán/công nợ/thanh toán vẫn do hệ thống.
                    </p>
                  </div>
                </div>
              )}

              {subTab === "schema" && (
                <div className="space-y-2">
                  <div className="overflow-x-auto rounded border">
                    <table className="w-full text-sm">
                      <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                        <tr>
                          <th className="px-2 py-1.5 text-left">Field</th>
                          <th className="px-2 py-1.5 text-left">Label</th>
                          <th className="px-2 py-1.5 text-left">Type</th>
                          <th className="px-2 py-1.5 text-center">Bắt buộc</th>
                          <th className="px-2 py-1.5 text-left">Mô tả</th>
                          <th />
                        </tr>
                      </thead>
                      <tbody>
                        {current.fields.map((f, i) => (
                          <tr key={i} className="border-t align-top">
                            <td className="px-1 py-1"><Input aria-label="field name" className="font-mono text-xs" value={f.name} disabled={!editable} onChange={(e) => updateField(i, { name: e.target.value })} /></td>
                            <td className="px-1 py-1"><Input aria-label="field label" value={f.label} disabled={!editable} onChange={(e) => updateField(i, { label: e.target.value })} /></td>
                            <td className="px-1 py-1">
                              <select aria-label="field type" className={selectClass} value={f.type} disabled={!editable} onChange={(e) => updateField(i, { type: e.target.value })}>
                                {fieldTypes.map((ft) => <option key={ft} value={ft}>{ft}</option>)}
                              </select>
                            </td>
                            <td className="px-1 py-1 text-center"><input aria-label="field required" type="checkbox" checked={f.required} disabled={!editable} onChange={(e) => updateField(i, { required: e.target.checked })} /></td>
                            <td className="px-1 py-1"><Input aria-label="field description" value={f.description ?? ""} disabled={!editable} onChange={(e) => updateField(i, { description: e.target.value || null })} /></td>
                            <td className="px-1 py-1">
                              {editable && (
                                <Button type="button" size="sm" variant="ghost" title="Xoá field"
                                  onClick={() => update(current.code, { fields: current.fields.filter((_, j) => j !== i) })}>
                                  <Trash2 className="size-3.5" />
                                </Button>
                              )}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  {editable && (
                    <Button type="button" size="sm" variant="outline"
                      onClick={() => update(current.code, { fields: [...current.fields, { name: "", label: "", type: "string", required: false, description: null }] })}>
                      <Plus className="size-3.5" /> Thêm field
                    </Button>
                  )}
                  <p className="text-xs text-[var(--color-muted-fg)]">
                    JSON Schema được sinh tự động từ bảng này; Gateway kiểm tra kết quả trích xuất theo schema (thiếu trường bắt buộc/sai kiểu → cần ADMIN xác nhận).
                  </p>
                </div>
              )}

              {subTab === "instructions" && (
                <textarea
                  aria-label="Hướng dẫn trích xuất"
                  rows={8}
                  className={selectClass}
                  value={current.extractionInstructions}
                  disabled={!editable}
                  onChange={(e) => update(current.code, { extractionInstructions: e.target.value })}
                />
              )}

              {subTab === "workflow" && (
                <div className="space-y-1.5">
                  <Label htmlFor="dp-wf">Workflow xử lý chứng từ</Label>
                  <select id="dp-wf" className={selectClass} value={current.workflowId ?? ""} disabled={!editable}
                    onChange={(e) => update(current.code, { workflowId: e.target.value || null })}>
                    <option value="">Không dùng workflow</option>
                    {workflows.map((w) => (
                      <option key={w.id} value={w.id}>{w.name}{w.scope === "department" ? " (Phòng ban)" : ""}</option>
                    ))}
                  </select>
                  <p className="text-xs text-[var(--color-muted-fg)]">
                    Bước &ldquo;Prompt&rdquo; bổ sung hướng dẫn trích xuất; bước &ldquo;Yêu cầu duyệt thủ công&rdquo; buộc ADMIN xác nhận. Workflow được chốt (pin) theo version khi publish.
                  </p>
                </div>
              )}

              {subTab === "skills" && (
                <div className="grid gap-1.5 rounded border p-3 sm:grid-cols-2">
                  {skills.length === 0 && <p className="text-xs text-[var(--color-muted-fg)]">Chưa có skill nào khả dụng.</p>}
                  {skills.map((s) => (
                    <label key={s.code} className="flex items-center gap-2 text-sm">
                      <input type="checkbox" disabled={!editable} checked={current.skillCodes.includes(s.code)}
                        onChange={(e) => update(current.code, {
                          skillCodes: e.target.checked ? [...current.skillCodes, s.code] : current.skillCodes.filter((c) => c !== s.code),
                        })} />
                      <span>{s.name}</span>
                      <span className="rounded-full bg-[var(--color-muted)] px-1.5 py-0.5 text-[10px]">{s.scope === "department" ? "Phòng ban" : "Hệ thống"}</span>
                    </label>
                  ))}
                </div>
              )}

              {subTab === "test" && (
                <DocumentProcessingTestPanel agentId={agentId} hint={current.code} sampleVouchers={sampleVouchers} />
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

interface TestResult {
  mode: string;
  version: number;
  versionStatus: string;
  detectedType: string;
  detectedTypeLabel: string;
  classification: { confidence: number; status: string; source: string; evidence: string[]; requires_confirmation: boolean };
  unsupportedDocumentType: boolean;
  schemaValid: boolean | null;
  missingRequired: string[];
  typeErrors: string[];
  workflow: { id: string; name: string; versionId: string | null } | null;
  workflowRequiresReview: boolean;
  extractedFields: Record<string, unknown>;
  accountingMutations: number;
  provider: string | null;
  model: string | null;
  error?: string;
}

export function DocumentProcessingTestPanel({
  agentId,
  hint,
  sampleVouchers,
}: {
  agentId: string;
  hint: string;
  sampleVouchers: SampleVoucher[];
}) {
  const [rawText, setRawText] = useState("");
  const [voucherId, setVoucherId] = useState("");
  const [useHint, setUseHint] = useState(false);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/department-agents/${agentId}/document-processing/test`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ rawText, voucherId: voucherId || undefined, voucherTypeHint: useHint ? hint : undefined }),
      });
      const body = (await res.json().catch(() => null)) as (TestResult & { error?: string }) | null;
      if (!res.ok || !body) setError(body?.error ?? `HTTP ${res.status}`);
      else if (body.error) setError(body.error);
      else setResult(body);
    } catch {
      setError("Không kết nối được tới Gateway.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="space-y-3">
      <p className="text-xs text-[var(--color-muted-fg)]">
        <FlaskConical className="mr-1 inline size-3.5" />
        Test chạy cấu hình của draft hiện tại (nếu có) qua runtime trích xuất không dùng tool. Không tạo chứng từ, bút toán, công nợ, đối soát hay thanh toán.
      </p>
      <div className="grid gap-2 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label htmlFor="dp-sample">Chứng từ mẫu (OCR có sẵn)</Label>
          <select id="dp-sample" className={selectClass} value={voucherId} onChange={(e) => setVoucherId(e.target.value)}>
            <option value="">— Dán text bên dưới —</option>
            {sampleVouchers.map((v) => <option key={v.id} value={v.id}>{v.label}</option>)}
          </select>
        </div>
        <label className="flex items-end gap-2 pb-2 text-sm">
          <input type="checkbox" checked={useHint} onChange={(e) => setUseHint(e.target.checked)} />
          Gợi ý loại: <span className="font-mono text-xs">{hint}</span>
        </label>
      </div>
      {!voucherId && (
        <textarea
          aria-label="Text chứng từ mẫu"
          rows={6}
          className={selectClass}
          value={rawText}
          onChange={(e) => setRawText(e.target.value)}
          placeholder="Dán raw OCR text của chứng từ mẫu..."
        />
      )}
      <Button type="button" size="sm" onClick={run} disabled={loading || (!voucherId && !rawText.trim())}>
        {loading ? "Đang trích xuất..." : "Test trích xuất"}
      </Button>
      {error && <p className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}
      {result && (
        <div className="space-y-2 rounded border p-3 text-sm" data-testid="document-test-result">
          <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-2">
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Loại phát hiện</dt><dd className="font-medium">{result.detectedTypeLabel} <span className="font-mono text-xs">({result.detectedType})</span></dd></div>
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Độ tin cậy / trạng thái</dt><dd>{Math.round(result.classification.confidence * 100)}% · {result.classification.status}</dd></div>
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Schema</dt><dd className={result.schemaValid === false || result.unsupportedDocumentType ? "text-red-700" : "text-emerald-700"}>
              {result.unsupportedDocumentType ? "Loại không được hỗ trợ" : result.schemaValid === null ? "—" : result.schemaValid ? "Hợp lệ" : "Không hợp lệ"}
            </dd></div>
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Thiếu trường bắt buộc</dt><dd className="font-mono text-xs">{result.missingRequired.length ? result.missingRequired.join(", ") : "—"}</dd></div>
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Workflow</dt><dd>{result.workflow ? `${result.workflow.name}${result.workflowRequiresReview ? " · cần duyệt" : ""}` : "—"}</dd></div>
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Version · model</dt><dd>v{result.version} ({result.versionStatus}) · {result.provider}/{result.model}</dd></div>
            <div><dt className="text-xs text-[var(--color-muted-fg)]">Ghi sổ kế toán</dt><dd data-testid="accounting-mutations">{result.accountingMutations} thay đổi</dd></div>
          </dl>
          {result.typeErrors.length > 0 && <p className="font-mono text-xs text-red-700">Sai kiểu: {result.typeErrors.join(", ")}</p>}
          <details>
            <summary className="cursor-pointer text-xs text-[var(--color-muted-fg)]">Căn cứ phân loại ({result.classification.evidence.length})</summary>
            <ul className="list-disc pl-5 text-xs">{result.classification.evidence.map((e, i) => <li key={i}>{e}</li>)}</ul>
          </details>
          <div className="overflow-x-auto rounded border">
            <table className="w-full text-xs">
              <tbody>
                {Object.entries(result.extractedFields)
                  .filter(([k]) => k !== "voucher_type_classification")
                  .map(([k, v]) => (
                    <tr key={k} className="border-t">
                      <td className="px-2 py-1 font-mono">{k}</td>
                      <td className="px-2 py-1">{typeof v === "object" ? JSON.stringify(v) : String(v ?? "")}</td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
