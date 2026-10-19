"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { ClipboardCheck, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// Luồng 03 stage 2 — "Kế toán" (accounting review): tax/account validity.
// Server-enforced: different actor than the creator AND than stage 1's
// reviewer. Only reachable after department-review.
export function AccountingReviewExpenseButton({ voucherId }: { voucherId: string }) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (loading) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/expense-claim/accounting-review`, { method: "POST" });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        setError(body.message ?? body.error ?? `HTTP ${res.status}`);
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
        {loading ? <Loader2 className="size-4 animate-spin" /> : <ClipboardCheck className="size-4" />} Duyệt kế toán
      </Button>
      {error && <span className="text-xs text-red-700">{error}</span>}
    </div>
  );
}
