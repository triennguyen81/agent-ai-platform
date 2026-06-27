"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { Building2, ChevronDown, Loader2 } from "lucide-react";

interface Membership {
  tenantId: string;
  tenantCode: string;
  tenantName: string;
  role: string;
}

interface Props {
  activeTenantId: string | null;
}

export function TenantSwitcher({ activeTenantId }: Props) {
  const router = useRouter();
  const [memberships, setMemberships] = useState<Membership[]>([]);
  const [open, setOpen] = useState(false);
  const [switching, setSwitching] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch("/api/me/tenants");
        if (!res.ok) return;
        const body = (await res.json()) as { items: Membership[] };
        if (!cancelled) setMemberships(body.items ?? []);
      } catch {
        // silent
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const active = memberships.find((m) => m.tenantId === activeTenantId) ?? memberships[0];

  const switchTo = useCallback(
    async (tenantId: string) => {
      if (switching || tenantId === activeTenantId) {
        setOpen(false);
        return;
      }
      setSwitching(tenantId);
      try {
        const res = await fetch("/api/me/tenants/switch", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ tenantId }),
        });
        if (res.ok) {
          setOpen(false);
          router.refresh();
        }
      } finally {
        setSwitching(null);
      }
    },
    [switching, activeTenantId, router],
  );

  // Hide entirely if user has <= 1 tenant — no switching needed
  if (memberships.length <= 1) {
    return null;
  }

  return (
    <div className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center gap-1.5 rounded-md px-2 py-1.5 text-xs text-[var(--color-muted-fg)] hover:bg-[var(--color-muted)] hover:text-[var(--color-fg)]"
      >
        <Building2 className="size-3.5" />
        <span className="font-medium">{active?.tenantName ?? "Tenant"}</span>
        <ChevronDown className="size-3" />
      </button>

      {open && (
        <div className="absolute right-0 z-20 mt-1 w-56 overflow-hidden rounded-md border border-[var(--color-border)] bg-[var(--color-bg)] shadow-lg">
          <ul>
            {memberships.map((m) => {
              const isActive = m.tenantId === activeTenantId;
              const isSwitching = switching === m.tenantId;
              return (
                <li key={m.tenantId}>
                  <button
                    type="button"
                    onClick={() => void switchTo(m.tenantId)}
                    disabled={isSwitching}
                    className={`flex w-full items-center justify-between gap-2 px-3 py-2 text-left text-xs hover:bg-[var(--color-muted)] ${
                      isActive ? "bg-[var(--color-muted)] font-medium" : ""
                    }`}
                  >
                    <div className="min-w-0 flex-1">
                      <div className="truncate">{m.tenantName}</div>
                      <div className="text-[10px] text-[var(--color-muted-fg)]">
                        {m.tenantCode} · {m.role}
                      </div>
                    </div>
                    {isSwitching && <Loader2 className="size-3 animate-spin" />}
                  </button>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}
