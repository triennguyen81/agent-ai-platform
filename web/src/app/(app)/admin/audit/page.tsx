import { redirect } from "next/navigation";
import { Activity, CheckCircle2, XCircle } from "lucide-react";
import { agent, gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

interface AuditEntry {
  ts: number;
  event: string;
  user_prefix: string;
  name: string;
  ms: number;
  ok: boolean;
  detail: string | null;
}

function formatTimestamp(ts: number): string {
  return new Date(ts * 1000).toLocaleString("vi-VN", {
    dateStyle: "short",
    timeStyle: "medium",
  });
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

function eventColor(event: string): string {
  switch (event) {
    case "chat":
      return "bg-blue-100 text-blue-700";
    case "agentic":
      return "bg-purple-100 text-purple-700";
    case "tool":
      return "bg-amber-100 text-amber-700";
    case "skill":
      return "bg-emerald-100 text-emerald-700";
    default:
      return "bg-gray-100 text-gray-600";
  }
}

interface Props {
  searchParams: Promise<{ event?: string; limit?: string }>;
}

export default async function AuditPage({ searchParams }: Props) {
  const token = await requireSession();
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    if (!(me.roles ?? []).includes("super_admin")) {
      redirect("/chat");
    }
  }

  const { event, limit: limitParam } = await searchParams;
  const limit = Math.min(Math.max(Number(limitParam ?? 100), 1), 500);

  const res = await agent.listAudit(limit, event);
  const entries: AuditEntry[] = res.ok ? await res.json() : [];

  // Aggregate stats
  const stats = entries.reduce(
    (acc, e) => {
      acc.total += 1;
      acc.ok += e.ok ? 1 : 0;
      acc.failed += e.ok ? 0 : 1;
      acc.totalMs += e.ms;
      acc.byEvent[e.event] = (acc.byEvent[e.event] ?? 0) + 1;
      return acc;
    },
    {
      total: 0,
      ok: 0,
      failed: 0,
      totalMs: 0,
      byEvent: {} as Record<string, number>,
    },
  );

  const avgMs = stats.total > 0 ? Math.round(stats.totalMs / stats.total) : 0;
  const eventFilters = ["", "chat", "agentic", "tool"];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Audit log</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Log các sự kiện chat + agentic gần nhất (lưu trong Redis, cap 5000
          dòng). Chỉ super_admin xem được.
        </p>
      </header>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard label="Tổng events" value={stats.total} />
        <StatCard label="Thành công" value={stats.ok} accent="emerald" />
        <StatCard label="Lỗi" value={stats.failed} accent="red" />
        <StatCard label="Latency TB" value={formatDuration(avgMs)} />
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {eventFilters.map((ev) => {
          const isActive = (event ?? "") === ev;
          const params = new URLSearchParams();
          if (ev) params.set("event", ev);
          params.set("limit", String(limit));
          return (
            <a
              key={ev || "all"}
              href={`/admin/audit?${params.toString()}`}
              className={`rounded-full border px-3 py-1 text-xs transition ${
                isActive
                  ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                  : "border-[var(--color-border)] text-[var(--color-muted-fg)] hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]"
              }`}
            >
              {ev || "Tất cả"}
              {stats.byEvent[ev] !== undefined && ev && (
                <span className="ml-1.5 opacity-70">({stats.byEvent[ev]})</span>
              )}
            </a>
          );
        })}
      </div>

      {entries.length === 0 ? (
        <p className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
          Chưa có sự kiện nào — chat hoặc gọi /agent/run để bắt đầu ghi log.
        </p>
      ) : (
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Thời gian</th>
                <th className="px-3 py-2 text-left">Event</th>
                <th className="px-3 py-2 text-left">Skill / Tool</th>
                <th className="px-3 py-2 text-left">User</th>
                <th className="px-3 py-2 text-right">Latency</th>
                <th className="px-3 py-2 text-center">OK</th>
                <th className="px-3 py-2 text-left">Detail</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry, idx) => (
                <tr key={`${entry.ts}-${idx}`} className="border-t align-top">
                  <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">
                    {formatTimestamp(entry.ts)}
                  </td>
                  <td className="px-3 py-2">
                    <span
                      className={`inline-block rounded-full px-2 py-0.5 text-xs ${eventColor(entry.event)}`}
                    >
                      {entry.event}
                    </span>
                  </td>
                  <td className="max-w-xs truncate px-3 py-2 font-mono text-xs" title={entry.name}>
                    {entry.name}
                  </td>
                  <td className="px-3 py-2 font-mono text-xs text-[var(--color-muted-fg)]">
                    {entry.user_prefix}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">
                    {formatDuration(entry.ms)}
                  </td>
                  <td className="px-3 py-2 text-center">
                    {entry.ok ? (
                      <CheckCircle2 className="mx-auto size-4 text-emerald-600" />
                    ) : (
                      <XCircle className="mx-auto size-4 text-red-600" />
                    )}
                  </td>
                  <td
                    className="max-w-md truncate px-3 py-2 text-xs text-[var(--color-muted-fg)]"
                    title={entry.detail ?? ""}
                  >
                    {entry.detail}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-center text-xs text-[var(--color-muted-fg)]">
        Hiển thị {entries.length} / cap 500 (Redis cap 5000 toàn bộ). Refresh
        trang để load mới nhất.
      </p>
    </div>
  );
}

function StatCard({
  label,
  value,
  accent,
}: {
  label: string;
  value: string | number;
  accent?: "emerald" | "red";
}) {
  const colorClass =
    accent === "emerald"
      ? "text-emerald-700"
      : accent === "red"
      ? "text-red-700"
      : "text-[var(--color-fg)]";
  return (
    <div className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-3">
      <p className="flex items-center gap-1 text-xs text-[var(--color-muted-fg)]">
        <Activity className="size-3" />
        {label}
      </p>
      <p className={`mt-1 font-serif text-2xl ${colorClass}`}>{value}</p>
    </div>
  );
}
