"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Ban, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// Luồng 03 — ADMIN decides a proposed expense claim outcome is wrong (or the
// claim should not proceed at all) and rejects it before anything is posted
// to sim_employees.
export function RejectExpenseClaimButton({ voucherId }: { voucherId: string }) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (loading) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/reject-expense-claim`, { method: "POST" });
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
      <Button variant="outline" onClick={submit} disabled={loading}>
        {loading ? <Loader2 className="size-4 animate-spin" /> : <Ban className="size-4" />} Từ chối đề nghị
      </Button>
      {error && <span className="text-xs text-red-700">{error}</span>}
    </div>
  );
}
