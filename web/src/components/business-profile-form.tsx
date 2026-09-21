"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

export interface BusinessProfileValues {
  legalName: string | null;
  taxCode: string | null;
  shortName: string | null;
  aliases: string[];
  registeredAddress: string | null;
  legalRepresentative: string | null;
  representativeTitle: string | null;
  phone: string | null;
  email: string | null;
  website: string | null;
}

const EMPTY: BusinessProfileValues = {
  legalName: null, taxCode: null, shortName: null, aliases: [],
  registeredAddress: null, legalRepresentative: null, representativeTitle: null,
  phone: null, email: null, website: null,
};

const FIELDS: { key: keyof Omit<BusinessProfileValues, "aliases">; label: string; placeholder?: string }[] = [
  { key: "legalName", label: "Tên pháp lý đầy đủ", placeholder: "Công ty TNHH ..." },
  { key: "taxCode", label: "Mã số thuế", placeholder: "0301234567" },
  { key: "shortName", label: "Tên viết tắt / thương hiệu" },
  { key: "registeredAddress", label: "Địa chỉ đăng ký kinh doanh" },
  { key: "legalRepresentative", label: "Người đại diện pháp luật" },
  { key: "representativeTitle", label: "Chức danh người đại diện", placeholder: "Giám đốc" },
  { key: "phone", label: "Điện thoại" },
  { key: "email", label: "Email" },
  { key: "website", label: "Website" },
];

// AIQ's own legal-entity identity ("công ty chủ thể") — TENANT_OWNER-only.
// Read by the AI runtime as trusted company context (never overridable from
// chat); the server always derives tenant_id from the authenticated
// session, never from anything submitted by this form.
export function BusinessProfileForm({ initial }: { initial: BusinessProfileValues | null }) {
  const router = useRouter();
  const base = initial ?? EMPTY;
  const [values, setValues] = useState<BusinessProfileValues>(base);
  const [aliasesText, setAliasesText] = useState(base.aliases.join(", "));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  function setField(key: keyof Omit<BusinessProfileValues, "aliases">, value: string) {
    setValues((v) => ({ ...v, [key]: value }));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const payload: BusinessProfileValues = {
        ...values,
        aliases: aliasesText.split(",").map((a) => a.trim()).filter(Boolean),
      };
      const normalized = Object.fromEntries(
        Object.entries(payload).map(([k, v]) => [k, typeof v === "string" ? v.trim() || null : v]),
      );
      const res = await fetch("/api/tenant/business-profile", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(normalized),
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
    <form onSubmit={submit} className="max-w-xl space-y-4">
      {FIELDS.map(({ key, label, placeholder }) => (
        <div key={key}>
          <Label htmlFor={key}>{label}</Label>
          <Input
            id={key}
            placeholder={placeholder}
            value={values[key] ?? ""}
            onChange={(e) => setField(key, e.target.value)}
          />
        </div>
      ))}
      <div>
        <Label htmlFor="aliases">Tên gọi khác (cách nhau bởi dấu phẩy)</Label>
        <Input
          id="aliases"
          placeholder="AIQ, Cát Tường AI"
          value={aliasesText}
          onChange={(e) => setAliasesText(e.target.value)}
        />
      </div>
      <p className="text-xs text-[var(--color-muted-fg)]">
        Thông tin này được AI dùng làm định danh công ty đáng tin cậy khi trò chuyện — luôn lấy
        theo tenant đã xác thực, người dùng không thể ghi đè qua chat.
      </p>
      <div className="flex items-center gap-3">
        <Button type="submit" disabled={busy}>Lưu</Button>
        {saved && <span className="text-sm text-emerald-700">Đã lưu.</span>}
        {error && <span className="text-sm text-red-700">{error}</span>}
      </div>
    </form>
  );
}
