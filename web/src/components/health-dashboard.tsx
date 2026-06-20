"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, CheckCircle2, Loader2, RefreshCw, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";

interface ServiceCheck {
  name: string;
  url: string;
  ok: boolean;
  status: number;
  ms: number;
  detail?: string;
}

interface HealthSnapshot {
  overall: "healthy" | "degraded" | "down";
  checked_at: string;
  checks: ServiceCheck[];
}

const REFRESH_MS = 30_000;

export function HealthDashboard() {
  const [snapshot, setSnapshot] = useState<HealthSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastFetched, setLastFetched] = useState<Date | null>(null);

  const fetchSnapshot = useCallback(async () => {
    setError(null);
    try {
      const res = await fetch("/api/admin/health", { cache: "no-store" });
      if (!res.ok) {
        setError(`HTTP ${res.status}`);
        return;
      }
      const body = (await res.json()) as HealthSnapshot;
      setSnapshot(body);
      setLastFetched(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchSnapshot();
    const id = setInterval(() => void fetchSnapshot(), REFRESH_MS);
    return () => clearInterval(id);
  }, [fetchSnapshot]);

  if (loading && !snapshot) {
    return (
      <div className="flex items-center gap-2 text-sm text-[var(--color-muted-fg)]">
        <Loader2 className="size-4 animate-spin" />
        Đang kiểm tra…
      </div>
    );
  }

  if (error && !snapshot) {
    return (
      <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
        {error}
      </p>
    );
  }

  if (!snapshot) return null;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-4">
        <OverallBadge overall={snapshot.overall} />
        <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
          {lastFetched && (
            <span>
              Lần check cuối: {lastFetched.toLocaleTimeString("vi-VN")} ·{" "}
              {snapshot.checks.filter((c) => c.ok).length}/{snapshot.checks.length} OK
            </span>
          )}
          <Button
            size="sm"
            variant="ghost"
            onClick={() => void fetchSnapshot()}
            disabled={loading}
          >
            {loading ? <Loader2 className="size-3.5 animate-spin" /> : <RefreshCw className="size-3.5" />}
            Refresh
          </Button>
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        {snapshot.checks.map((check) => (
          <ServiceCard key={`${check.name}-${check.url}`} check={check} />
        ))}
      </div>

      <p className="text-center text-xs text-[var(--color-muted-fg)]">
        Auto-refresh mỗi {REFRESH_MS / 1000} giây
      </p>
    </div>
  );
}

function OverallBadge({ overall }: { overall: HealthSnapshot["overall"] }) {
  const config = {
    healthy: {
      icon: <CheckCircle2 className="size-5" />,
      label: "Tất cả OK",
      className: "border-emerald-300 bg-emerald-50 text-emerald-700",
    },
    degraded: {
      icon: <AlertTriangle className="size-5" />,
      label: "Một phần lỗi",
      className: "border-orange-300 bg-orange-50 text-orange-700",
    },
    down: {
      icon: <XCircle className="size-5" />,
      label: "Down",
      className: "border-red-300 bg-red-50 text-red-700",
    },
  }[overall];

  return (
    <span className={`inline-flex items-center gap-2 rounded-full border px-4 py-1 font-medium ${config.className}`}>
      {config.icon}
      {config.label}
    </span>
  );
}

function ServiceCard({ check }: { check: ServiceCheck }) {
  const latencyClass =
    check.ms < 200
      ? "text-emerald-700"
      : check.ms < 1000
      ? "text-orange-700"
      : "text-red-700";

  return (
    <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-bg)] p-4">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="font-medium">{check.name}</p>
          <p className="truncate font-mono text-[10px] text-[var(--color-muted-fg)]" title={check.url}>
            {check.url}
          </p>
        </div>
        {check.ok ? (
          <CheckCircle2 className="size-5 shrink-0 text-emerald-600" />
        ) : (
          <XCircle className="size-5 shrink-0 text-red-600" />
        )}
      </div>
      <div className="flex items-center gap-4 text-xs">
        <span className="font-mono">
          HTTP <strong>{check.status || "—"}</strong>
        </span>
        <span className={`font-mono ${latencyClass}`}>{check.ms}ms</span>
      </div>
      {check.detail && (
        <p className="rounded bg-[var(--color-muted)] px-2 py-1 text-xs text-red-700">
          {check.detail}
        </p>
      )}
    </div>
  );
}
