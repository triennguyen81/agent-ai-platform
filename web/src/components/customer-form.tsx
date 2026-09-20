"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface CustomerFormValues {
  customerCode?: string;
  name?: string;
  mst?: string | null;
  address?: string | null;
  phone?: string | null;
  email?: string | null;
  category?: string | null;
  customerType?: string;
  representative?: string | null;
  creditLimit?: number;
  creditTermsDays?: number;
  notes?: string | null;
}

// Shared by "+ Thêm khách hàng" (create, customerId undefined) and the
// detail page's "Sửa" form (edit, customerId set) — same fields, backend
// decides what's actually allowed (create vs PATCH, creator-scoped).
export function CustomerForm({
  customerId,
  initial,
  onDone,
}: {
  customerId?: string;
  initial?: CustomerFormValues;
  onDone?: () => void;
}) {
  const router = useRouter();
  const [values, setValues] = useState<CustomerFormValues>(
    initial ?? { customerType: "company", creditLimit: 0, creditTermsDays: 0 },
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [duplicates, setDuplicates] = useState<Array<{ name: string; customerCode: string }>>([]);

  function set<K extends keyof CustomerFormValues>(key: K, value: CustomerFormValues[K]) {
    setValues((v) => ({ ...v, [key]: value }));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDuplicates([]);
    try {
      const url = customerId ? `/api/customers/${customerId}` : "/api/customers";
      const res = await fetch(url, {
        method: customerId ? "PATCH" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(values),
      });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        if (res.status === 409) {
          setError("Trùng mã khách hàng hoặc mã số thuế đã tồn tại.");
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
        {!customerId && (
          <div>
            <Label htmlFor="customerCode">Mã khách hàng *</Label>
            <Input
              id="customerCode"
              required
              value={values.customerCode ?? ""}
              onChange={(e) => set("customerCode", e.target.value)}
            />
          </div>
        )}
        <div>
          <Label htmlFor="name">Tên khách hàng *</Label>
          <Input id="name" required value={values.name ?? ""} onChange={(e) => set("name", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="mst">Mã số thuế</Label>
          <Input id="mst" placeholder="0101234567" value={values.mst ?? ""} onChange={(e) => set("mst", e.target.value)} />
        </div>
        <div>
          <Label htmlFor="customerType">Loại khách hàng</Label>
          <select
            id="customerType"
            className="h-10 w-full rounded-[var(--radius)] border px-3 text-sm"
            value={values.customerType ?? "company"}
            onChange={(e) => set("customerType", e.target.value)}
          >
            <option value="company">Doanh nghiệp</option>
            <option value="individual">Cá nhân</option>
          </select>
        </div>
        <div>
          <Label htmlFor="category">Phân loại</Label>
          <Input
            id="category"
            list="customer-category-options"
            placeholder="cong_ty"
            value={values.category ?? ""}
            onChange={(e) => set("category", e.target.value)}
          />
          <datalist id="customer-category-options">
            <option value="phong_kham" />
            <option value="benh_vien" />
            <option value="dai_ly" />
            <option value="truong_hoc" />
            <option value="cong_ty" />
          </datalist>
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
          <Label htmlFor="creditTermsDays">Điều khoản thanh toán (số ngày)</Label>
          <Input
            id="creditTermsDays"
            type="number"
            min={0}
            value={values.creditTermsDays ?? 0}
            onChange={(e) => set("creditTermsDays", Number(e.target.value))}
          />
        </div>
        <div>
          <Label htmlFor="creditLimit">Hạn mức tín dụng (VNĐ)</Label>
          <Input
            id="creditLimit"
            type="number"
            min={0}
            value={values.creditLimit ?? 0}
            onChange={(e) => set("creditLimit", Number(e.target.value))}
          />
        </div>
      </fieldset>

      <div className="sm:col-span-2">
        <Label htmlFor="notes">Ghi chú</Label>
        <Input id="notes" value={values.notes ?? ""} onChange={(e) => set("notes", e.target.value)} />
      </div>

      <div className="flex items-center gap-3">
        <Button type="submit" disabled={busy}>
          {customerId ? "Lưu thay đổi" : "Tạo khách hàng"}
        </Button>
        {error && <span className="text-sm text-red-700">{error}</span>}
      </div>
      {duplicates.length > 0 && (
        <div className="rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800">
          Có thể trùng tên với: {duplicates.map((d) => `${d.name} (${d.customerCode})`).join(", ")}
        </div>
      )}
    </form>
  );
}
