"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";

// Deactivate/reactivate — ADMIN-only on the backend (department_admin
// route), so this only renders for isAdmin; a USER never sees it, matching
// "USER cannot approve/reject/final handoff"-style lifecycle actions
// elsewhere (Voucher delete, Contract review/handoff).
export function CustomerLifecycleActions({ customerId, status }: { customerId: string; status: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function act(action: "deactivate" | "reactivate") {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(`/api/customers/${customerId}/${action}`, { method: "POST" });
      if (!res.ok) {
        setError(`Thao tác thất bại (HTTP ${res.status})`);
        return;
      }
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex items-center gap-2">
      {status === "active" ? (
        <Button variant="outline" disabled={busy} onClick={() => void act("deactivate")}>
          Ngừng hoạt động
        </Button>
      ) : (
        <Button disabled={busy} onClick={() => void act("reactivate")}>
          Kích hoạt lại
        </Button>
      )}
      {error && <span className="text-sm text-red-700">{error}</span>}
    </div>
  );
}
