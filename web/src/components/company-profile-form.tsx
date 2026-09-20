"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

// Sets tenants.company_tax_id — the ONLY trusted "who are we" identity
// InvoiceDirectionResolver uses to classify an invoice/contract as
// SALES vs PURCHASE (Accounting/InvoiceDirectionResolver.cs). Never
// inferred from document text or an LLM; TENANT_OWNER-configured here.
export function CompanyProfileForm({ initialCompanyTaxId }: { initialCompanyTaxId: string | null }) {
  const router = useRouter();
  const [value, setValue] = useState(initialCompanyTaxId ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const res = await fetch("/api/tenant/company-profile", {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ companyTaxId: value.trim() || null }),
      });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        setError(
          body?.errors
            ? Object.values(body.errors as Record<string, string[]>).flat().join("; ")
            : `Thao tác thất bại (HTTP ${res.status})`,
        );
        return;
      }
      setSaved(true);
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="max-w-sm space-y-3">
      <div>
        <Label htmlFor="companyTaxId">Mã số thuế công ty (tenant hiện tại)</Label>
        <Input
          id="companyTaxId"
          placeholder="0301234567"
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
        <p className="mt-1 text-xs text-[var(--color-muted-fg)]">
          Dùng để hệ thống tự xác định hoá đơn/hợp đồng là BÁN hay MUA (so khớp
          MST người bán/mua với MST này) — không dựa vào nội dung tài liệu hay
          AI đoán.
        </p>
      </div>
      <div className="flex items-center gap-3">
        <Button type="submit" disabled={busy}>Lưu</Button>
        {saved && <span className="text-sm text-emerald-700">Đã lưu.</span>}
        {error && <span className="text-sm text-red-700">{error}</span>}
      </div>
    </form>
  );
}
