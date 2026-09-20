"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";

// Deactivate/reactivate — ADMIN-only on the backend (department_admin
// route). Mirrors CustomerLifecycleActions.
export function SupplierLifecycleActions({ supplierId, status }: { supplierId: string; status: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function act(action: "deactivate" | "reactivate") {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(`/api/suppliers/${supplierId}/${action}`, { method: "POST" });
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
          Ngừng hợp tác
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
