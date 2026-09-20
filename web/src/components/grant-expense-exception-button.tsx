"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { ShieldCheck, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// Luồng 03 — ADMIN grants a configured-policy exception on a
// WAITING_EXCEPTION_APPROVAL claim: un-caps to the full requested amount
// (minus only genuinely blocked duplicate lines) and applies it immediately.
export function GrantExpenseExceptionButton({ voucherId }: { voucherId: string }) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (loading) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/grant-expense-exception`, { method: "POST" });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        setError(body.error ?? `HTTP ${res.status}`);
        return;
      }
      router.refresh();
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="flex items-center gap-2">
      <Button onClick={submit} disabled={loading}>
        {loading ? <Loader2 className="size-4 animate-spin" /> : <ShieldCheck className="size-4" />} Duyệt ngoại lệ
      </Button>
      {error && <span className="text-xs text-red-700">{error}</span>}
    </div>
  );
}
