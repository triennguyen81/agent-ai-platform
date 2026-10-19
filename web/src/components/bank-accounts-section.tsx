"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Landmark, Loader2, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";

// TÀI KHOẢN NGÂN HÀNG — Customer/Supplier Bank Account Master. ADMIN of the
// department can add/edit/deactivate (Gateway enforces department_admin);
// others read only. Accounts are never deleted — deactivation keeps the
// audit trail for statement lines that matched them.

export interface BankAccountView {
  id: string;
  bankCode: string | null;
  bankName: string | null;
  accountNumber: string;
  accountNumberNormalized: string;
  accountName: string | null;
  branch: string | null;
  currency: string;
  isPrimary: boolean;
  isActive: boolean;
  source: string;
  createdAt: string;
}

interface FormState {
  bankCode: string;
  bankName: string;
  accountNumber: string;
  accountName: string;
  branch: string;
  isPrimary: boolean;
}

const empty = (accountName: string): FormState => ({ bankCode: "", bankName: "", accountNumber: "", accountName, branch: "", isPrimary: false });

export function BankAccountsSection({
  owner,
  ownerId,
  ownerName,
  accounts,
  canManage,
}: {
  owner: "customers" | "suppliers";
  ownerId: string;
  ownerName: string;
  accounts: BankAccountView[];
  canManage: boolean;
}) {
  const router = useRouter();
  const [editing, setEditing] = useState<string | "new" | null>(null);
  const [form, setForm] = useState<FormState>(empty(ownerName));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const send = async (url: string, method: "POST" | "PATCH", body?: unknown) => {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(url, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
      const json = (await res.json().catch(() => ({}))) as { message?: string; error?: string };
      if (!res.ok) {
        throw new Error(res.status === 403 ? "Chỉ ADMIN cùng phòng ban mới được thay đổi tài khoản ngân hàng." : json.message ?? json.error ?? `HTTP ${res.status}`);
      }
      setEditing(null);
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Lỗi mạng");
    } finally {
      setBusy(false);
    }
  };

  const base = `/api/bank-accounts/${owner}/${ownerId}`;
  const save = () => {
    const payload = {
      bankCode: form.bankCode || null, bankName: form.bankName || null, accountNumber: form.accountNumber,
      accountName: form.accountName || null, branch: form.branch || null,
      // New account: unchecked = let the server decide (first account becomes primary).
      isPrimary: editing === "new" && !form.isPrimary ? null : form.isPrimary,
    };
    return editing === "new" ? send(base, "POST", payload) : send(`${base}/${editing}`, "PATCH", payload);
  };

  const formRow = (
    <div className="grid gap-2 rounded border bg-[var(--color-muted)]/30 p-3 sm:grid-cols-3">
      <input aria-label="Mã ngân hàng" placeholder="Mã NH (VCB, ACB…)" value={form.bankCode} onChange={(e) => setForm({ ...form, bankCode: e.target.value })} className="h-9 rounded border px-2 text-sm" />
      <input aria-label="Tên ngân hàng" placeholder="Tên ngân hàng" value={form.bankName} onChange={(e) => setForm({ ...form, bankName: e.target.value })} className="h-9 rounded border px-2 text-sm" />
      <input aria-label="Số tài khoản" placeholder="Số tài khoản" value={form.accountNumber} onChange={(e) => setForm({ ...form, accountNumber: e.target.value })} className="h-9 rounded border px-2 font-mono text-sm" />
      <input aria-label="Tên tài khoản" placeholder="Tên tài khoản" value={form.accountName} onChange={(e) => setForm({ ...form, accountName: e.target.value })} className="h-9 rounded border px-2 text-sm sm:col-span-2" />
      <input aria-label="Chi nhánh" placeholder="Chi nhánh" value={form.branch} onChange={(e) => setForm({ ...form, branch: e.target.value })} className="h-9 rounded border px-2 text-sm" />
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={form.isPrimary} onChange={(e) => setForm({ ...form, isPrimary: e.target.checked })} /> Tài khoản chính
      </label>
      <div className="flex gap-2 sm:col-span-2 sm:justify-end">
        <Button size="sm" variant="ghost" onClick={() => setEditing(null)} disabled={busy}>Huỷ</Button>
        <Button size="sm" onClick={save} disabled={busy || !form.accountNumber.trim()}>
          {busy ? <Loader2 className="size-3.5 animate-spin" /> : null} Lưu tài khoản
        </Button>
      </div>
    </div>
  );

  return (
    <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
      <div className="flex items-center justify-between">
        <h2 className="font-serif text-xl"><Landmark className="mr-2 inline size-5" />Tài khoản ngân hàng</h2>
        {canManage && editing === null && (
          <Button size="sm" variant="outline" onClick={() => { setForm(empty(ownerName)); setEditing("new"); }}>
            <Plus className="size-3.5" /> Thêm tài khoản
          </Button>
        )}
      </div>

      {editing === "new" && formRow}

      {accounts.length === 0 ? (
        <p className="text-sm text-[var(--color-muted-fg)]">Chưa có tài khoản ngân hàng.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[640px] text-sm">
            <thead className="text-left text-xs text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-2 py-1">Ngân hàng</th>
                <th className="px-2 py-1">Số tài khoản</th>
                <th className="px-2 py-1">Tên tài khoản</th>
                <th className="px-2 py-1 text-center">Chính</th>
                <th className="px-2 py-1">Trạng thái</th>
                <th className="px-2 py-1" />
              </tr>
            </thead>
            <tbody>
              {accounts.map((a) => (
                editing === a.id ? (
                  <tr key={a.id}><td colSpan={6} className="py-2">{formRow}</td></tr>
                ) : (
                  <tr key={a.id} className={`border-t ${a.isActive ? "" : "text-[var(--color-muted-fg)]"}`}>
                    <td className="px-2 py-1">{a.bankCode ?? "—"}{a.bankName && a.bankName !== a.bankCode ? <span className="text-xs text-[var(--color-muted-fg)]"> · {a.bankName}</span> : null}</td>
                    <td className="px-2 py-1 font-mono">{a.accountNumber}</td>
                    <td className="px-2 py-1">{a.accountName ?? "—"}</td>
                    <td className="px-2 py-1 text-center">{a.isPrimary ? "✓" : ""}</td>
                    <td className="px-2 py-1 text-xs">
                      {a.isActive ? "Active" : "Ngừng sử dụng"}
                      {a.source === "statement_review" && <span className="ml-1 rounded border px-1 text-[10px]">học từ sao kê</span>}
                    </td>
                    <td className="px-2 py-1 text-right">
                      {canManage && editing === null && (
                        <div className="flex justify-end gap-1">
                          {a.isActive && (
                            <Button size="sm" variant="ghost" disabled={busy} onClick={() => {
                              setForm({ bankCode: a.bankCode ?? "", bankName: a.bankName ?? "", accountNumber: a.accountNumber, accountName: a.accountName ?? "", branch: a.branch ?? "", isPrimary: a.isPrimary });
                              setEditing(a.id);
                            }}>Sửa</Button>
                          )}
                          <Button size="sm" variant="ghost" disabled={busy} onClick={() => send(`${base}/${a.id}/${a.isActive ? "deactivate" : "reactivate"}`, "POST")}>
                            {a.isActive ? "Ngừng sử dụng" : "Dùng lại"}
                          </Button>
                        </div>
                      )}
                    </td>
                  </tr>
                )
              ))}
            </tbody>
          </table>
        </div>
      )}
      {error && <p className="rounded border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">Lỗi: {error}</p>}
    </section>
  );
}
