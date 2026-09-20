"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface SupplierFormValues {
  supplierCode?: string;
  name?: string;
  mst?: string | null;
  address?: string | null;
  phone?: string | null;
  email?: string | null;
  supplierType?: string;
  representative?: string | null;
  paymentTerms?: string | null;
  paymentTermsDays?: number;
  notes?: string | null;
}

// Shared by "+ Thêm nhà cung cấp" (create, supplierId undefined) and the
// detail page's "Sửa" form (edit, supplierId set) — mirrors CustomerForm;
// backend decides what's actually allowed (create vs PATCH, creator-scoped).
export function SupplierForm({
  supplierId,
  initial,
  onDone,
}: {
  supplierId?: string;
  initial?: SupplierFormValues;
  onDone?: () => void;
}) {
  const router = useRouter();
  const [values, setValues] = useState<SupplierFormValues>(
    initial ?? { supplierType: "company", paymentTermsDays: 0 },
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [duplicates, setDuplicates] = useState<Array<{ name: string; supplierCode: string }>>([]);

  function set<K extends keyof SupplierFormValues>(key: K, value: SupplierFormValues[K]) {
    setValues((v) => ({ ...v, [key]: value }));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDuplicates([]);
    try {
      const url = supplierId ? `/api/suppliers/${supplierId}` : "/api/suppliers";
      const res = await fetch(url, {
        method: supplierId ? "PATCH" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(values),
      });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        if (res.status === 409) {
          setError("Trùng mã nhà cung cấp hoặc mã số thuế đã tồn tại.");
        } else if (body?.errors) {
          setError(Object.values(body.errors as Record<string, string[]>).flat().join("; "));
        } else {
          setError(`Thao tác thất bại (HTTP ${res.status})`);
        }
        return;
      }
      const body = await res.json();
      if (body?.possibleDuplicates?.length) setDuplicates(body.possibleDuplicates);
      router.refresh();
      onDone?.();
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-5">
      <fieldset className="grid gap-3 sm:grid-cols-2">
        <legend className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--color-muted-fg)] sm:col-span-2">
          Thông tin chung
        </legend>
        {!supplierId && (
          <div>
            <Label htmlFor="supplierCode">Mã nhà cung cấp *</Label>
            <Input
              id="supplierCode"
              required
              value={values.supplierCode ?? ""}
              onChange={(e) => set("supplierCode", e.target.value)}
            />
          </div>
        )}
        <div>
          <Label htmlFor="name">Tên nhà cung cấp *</Label>
          <Input id="name" required value={values.name ?? ""} onChange={(e) => set("name", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="mst">Mã số thuế</Label>
          <Input id="mst" placeholder="0101234567" value={values.mst ?? ""} onChange={(e) => set("mst", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="supplierType">Loại nhà cung cấp</Label>
          <select
            id="supplierType"
            className="h-10 w-full rounded-[var(--radius)] border px-3 text-sm"
            value={values.supplierType ?? "company"}
            onChange={(e) => set("supplierType", e.target.value)}
          >
            <option value="company">Doanh nghiệp</option>
            <option value="individual">Cá nhân</option>
          </select>
        </div>
      </fieldset>

      <fieldset className="grid gap-3 sm:grid-cols-2">
        <legend className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--color-muted-fg)] sm:col-span-2">
          Liên hệ
        </legend>
        <div className="sm:col-span-2">
          <Label htmlFor="address">Địa chỉ</Label>
          <Input id="address" value={values.address ?? ""} onChange={(e) => set("address", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="representative">Người liên hệ / đại diện</Label>
          <Input id="representative" value={values.representative ?? ""} onChange={(e) => set("representative", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="phone">Điện thoại</Label>
          <Input id="phone" value={values.phone ?? ""} onChange={(e) => set("phone", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="email">Email</Label>
          <Input id="email" type="email" value={values.email ?? ""} onChange={(e) => set("email", e.target.value)} />
        </div>
      </fieldset>

      <fieldset className="grid gap-3 sm:grid-cols-2">
        <legend className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--color-muted-fg)] sm:col-span-2">
          Công nợ
        </legend>
        <div>
          <Label htmlFor="paymentTerms">Điều khoản thanh toán</Label>
          <Input id="paymentTerms" placeholder="Net 30" value={values.paymentTerms ?? ""} onChange={(e) => set("paymentTerms", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="paymentTermsDays">Số ngày công nợ</Label>
          <Input
            id="paymentTermsDays"
            type="number"
            min={0}
            value={values.paymentTermsDays ?? 0}
            onChange={(e) => set("paymentTermsDays", Number(e.target.value))}
          />
        </div>
      </fieldset>

      <div className="sm:col-span-2">
        <Label htmlFor="notes">Ghi chú</Label>
        <Input id="notes" value={values.notes ?? ""} onChange={(e) => set("notes", e.target.value)} />
      </div>

      <div className="flex items-center gap-3">
        <Button type="submit" disabled={busy}>
          {supplierId ? "Lưu thay đổi" : "Tạo nhà cung cấp"}
        </Button>
        {error && <span className="text-sm text-red-700">{error}</span>}
      </div>
      {duplicates.length > 0 && (
        <div className="rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800">
          Có thể trùng tên với: {duplicates.map((d) => `${d.name} (${d.supplierCode})`).join(", ")}
        </div>
      )}
    </form>
  );
}
