"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { UserCheck, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// Luồng 03 stage 1 — "Bộ phận" (department review): claim reasonableness.
// Server-enforced: different actor than the creator, and this claim can
// only move to stage 2 afterward — never straight to payment approval.
export function DepartmentReviewExpenseButton({ voucherId }: { voucherId: string }) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (loading) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/expense-claim/department-review`, { method: "POST" });
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
        {loading ? <Loader2 className="size-4 animate-spin" /> : <UserCheck className="size-4" />} Duyệt bộ phận
      </Button>
      {error && <span className="text-xs text-red-700">{error}</span>}
    </div>
  );
}
