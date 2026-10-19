"use client";

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";

// ĐỀ NGHỊ HOÀN ỨNG / THANH TOÁN CÔNG TÁC PHÍ — one action, many bills:
// ONE claim, ONE item per file. Totals, advance and payable are always
// computed by the Gateway (never sent from here).

interface Employee { id: string; employeeCode: string; fullName: string; position?: string | null; outstandingAdvance: number }
interface FileResult { index: number; fileName: string; outcome: string; itemId?: string | null; error?: string | null }

const vnd = (n: number) => new Intl.NumberFormat("vi-VN").format(n);

export default function NewExpenseClaimPage() {
  const router = useRouter();
  const [employees, setEmployees] = useState<Employee[]>([]);
  const [employeeId, setEmployeeId] = useState("");
  const [purpose, setPurpose] = useState("");
  const [reference, setReference] = useState("");
  const [tripStart, setTripStart] = useState("");
  const [tripEnd, setTripEnd] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rejected, setRejected] = useState<FileResult[]>([]);
  // One identity per screen: a double-submit/replay returns the same claim and items.
  const requestId = useMemo(() => crypto.randomUUID(), []);

  useEffect(() => {
    void fetch("/api/expense-claims/employees", { cache: "no-store" })
      .then((r) => (r.ok ? r.json() : { items: [] }))
      .then((b: { items: Employee[] }) => setEmployees(b.items ?? []));
  }, []);

  const employee = employees.find((e) => e.id === employeeId);

  async function submit() {
    setBusy(true);
    setError(null);
    setRejected([]);
    try {
      const form = new FormData();
      form.append("employeeId", employeeId);
      form.append("purpose", purpose);
      form.append("reference", reference);
      form.append("tripStart", tripStart);
      form.append("tripEnd", tripEnd);
      form.append("requestId", requestId);
      for (const f of files) form.append("files", f, f.name);
      const res = await fetch("/api/expense-claims", { method: "POST", body: form });
      const body = (await res.json().catch(() => ({}))) as { claimId?: string; error?: string; files?: FileResult[] };
      if (!res.ok || !body.claimId) {
        setError(body.error ?? `Không tạo được đề nghị (HTTP ${res.status}).`);
        setRejected((body.files ?? []).filter((f) => f.outcome === "rejected"));
        return;
      }
      const bad = (body.files ?? []).filter((f) => f.outcome === "rejected");
      if (bad.length > 0) setRejected(bad);
      router.push(`/vouchers/${body.claimId}`);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-3xl space-y-4 p-4">
      <h1 className="font-serif text-2xl">Đề nghị hoàn ứng / thanh toán công tác phí</h1>
      <p className="text-sm text-[var(--color-muted-fg)]">
        Tải lên tất cả hoá đơn của chuyến công tác trong một lần. AIQ tạo MỘT đề nghị, mỗi file là một khoản chi được đọc và kiểm tra riêng.
      </p>
      <div className="grid gap-3 rounded border p-4 text-sm sm:grid-cols-2">
        <label className="sm:col-span-2">Nhân viên
          <select aria-label="Nhân viên" className="mt-1 block w-full rounded border px-2 py-1" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
            <option value="">— Chọn nhân viên —</option>
            {employees.map((e) => <option key={e.id} value={e.id}>{e.employeeCode} · {e.fullName}</option>)}
          </select>
        </label>
        <label className="sm:col-span-2">Mục đích chuyến công tác
          <input aria-label="Mục đích" className="mt-1 block w-full rounded border px-2 py-1" value={purpose} onChange={(e) => setPurpose(e.target.value)} placeholder="VD: Nha Trang → TP.HCM" />
        </label>
        <label>Từ ngày
          <input aria-label="Từ ngày" type="date" className="mt-1 block w-full rounded border px-2 py-1" value={tripStart} onChange={(e) => setTripStart(e.target.value)} />
        </label>
        <label>Đến ngày
          <input aria-label="Đến ngày" type="date" className="mt-1 block w-full rounded border px-2 py-1" value={tripEnd} onChange={(e) => setTripEnd(e.target.value)} />
        </label>
        <label>Số đề nghị (tuỳ chọn)
          <input aria-label="Số đề nghị" className="mt-1 block w-full rounded border px-2 py-1" value={reference} onChange={(e) => setReference(e.target.value)} />
        </label>
        <div>Tạm ứng hiện có
          <div className="mt-1 font-mono" data-testid="employee-advance">{employee ? `${vnd(employee.outstandingAdvance)} ₫` : "—"}</div>
          <div className="text-xs text-[var(--color-muted-fg)]">Lấy từ sổ tạm ứng của nhân viên — không nhập tay.</div>
        </div>
      </div>
      <label
        className="block cursor-pointer rounded border-2 border-dashed p-6 text-center text-sm"
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => { e.preventDefault(); setFiles([...files, ...Array.from(e.dataTransfer.files)]); }}
      >
        Kéo thả hoặc <u>chọn nhiều file</u> (PDF / ảnh — mỗi file một hoá đơn)
        <input data-testid="claim-files" type="file" multiple accept="application/pdf,image/jpeg,image/png,image/webp" className="hidden"
          onChange={(e) => setFiles([...files, ...Array.from(e.target.files ?? [])])} />
      </label>
      {files.length > 0 && (
        <ul className="divide-y rounded border text-sm" data-testid="claim-file-list">
          {files.map((f, i) => (
            <li key={`${f.name}-${i}`} className="flex justify-between px-3 py-1">
              <span>{f.name}</span>
              <button type="button" className="text-xs text-red-700" onClick={() => setFiles(files.filter((_, j) => j !== i))}>Bỏ</button>
            </li>
          ))}
        </ul>
      )}
      {error && <p className="text-sm text-red-700" data-testid="claim-error">{error}</p>}
      {rejected.length > 0 && (
        <ul className="text-sm text-amber-800">
          {rejected.map((r) => <li key={r.index}>{r.fileName}: không nhận ({r.error})</li>)}
        </ul>
      )}
      <button type="button" className="rounded bg-[var(--color-primary)] px-4 py-2 text-white disabled:opacity-50"
        disabled={busy || !employeeId || files.length === 0} onClick={submit}>
        {busy ? "Đang tải lên…" : `Tạo đề nghị với ${files.length} chứng từ`}
      </button>
    </div>
  );
}
