import Link from "next/link";
import { AlertCircle, Bot, Inbox, Lock, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

interface MyAgent {
  agentId: string;
  agentCode: string;
  agentName: string;
  description: string | null;
  agentType: string;
  lifecycleState: string;
  publishedVersion: number | null;
  actionTier: string;
  agentDisabled: boolean;
  accessLevel: string;
}

const LIFECYCLE_BADGE: Record<string, string> = {
  draft: "border-gray-300 bg-gray-50 text-gray-700",
  testing: "border-blue-300 bg-blue-50 text-blue-700",
  pending_review: "border-amber-300 bg-amber-50 text-amber-700",
  production: "border-emerald-300 bg-emerald-50 text-emerald-700",
  deprecated: "border-orange-300 bg-orange-50 text-orange-700",
  retired: "border-gray-300 bg-gray-100 text-gray-500",
};

export default async function MyAgentsPage() {
  const token = await requireSession();
  const res = await gateway.listMyAgents(token);
  const body = res.ok
    ? ((await res.json()) as { items: MyAgent[]; total: number })
    : { items: [], total: 0 };

  // Only production + not-disabled agents are usable
  const usable = body.items.filter(
    (a) => a.lifecycleState === "production" && !a.agentDisabled,
  );
  const others = body.items.filter(
    (a) => a.lifecycleState !== "production" || a.agentDisabled,
  );

  return (
    <div className="space-y-6">
      <header className="space-y-1">
        <h1 className="font-serif text-3xl">
          <Bot className="mr-2 inline size-6" /> Trợ lý AI của tôi
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Đây là các trợ lý AI đã được admin gán cho bạn. Bạn không tự thay đổi được — nếu cần
          thêm/bớt, hãy liên hệ admin của tenant.
        </p>
        <p className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)]">
          <Lock className="size-3" /> Read-only view
        </p>
      </header>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center">
          <Inbox className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="text-sm text-[var(--color-muted-fg)]">
            Bạn chưa được gán agent nào. Liên hệ admin để được cấp quyền.
          </p>
        </div>
      ) : (
        <>
          {usable.length > 0 && (
            <section className="space-y-3">
              <h2 className="font-serif text-lg">
                <ShieldCheck className="mr-2 inline size-4 text-emerald-600" />
                Sẵn sàng dùng ({usable.length})
              </h2>
              <div className="grid gap-3 lg:grid-cols-2">
                {usable.map((a) => (
                  <article
                    key={a.agentId}
                    className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-bg)] p-4"
                  >
                    <div className="flex items-start justify-between gap-2">
                      <div className="min-w-0 flex-1">
                        <h3 className="font-medium">{a.agentName}</h3>
                        <p className="font-mono text-[10px] text-[var(--color-muted-fg)]">
                          {a.agentCode}
                        </p>
                        {a.description && <p className="mt-1 text-sm text-[var(--color-muted-fg)]">{a.description}</p>}
                      </div>
                      <span
                        className={`shrink-0 rounded-full border px-2 py-0.5 text-[10px] ${
                          LIFECYCLE_BADGE[a.lifecycleState] ?? "border-gray-300"
                        }`}
                      >
                        {a.lifecycleState}
                      </span>
                    </div>
                    <div className="flex flex-wrap gap-2 text-xs">
                      <span className="rounded-full border px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]">
                        {a.agentType}
                      </span>
                      <span className="rounded-full border px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]">
                        tier: {a.actionTier}
                      </span>
                      <span className="rounded-full border px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]">
                        version: {a.publishedVersion ?? "unpublished"}
                      </span>
                      <span className="rounded-full bg-emerald-600 px-2 py-0.5 text-[10px] text-white">
                        {a.accessLevel}
                      </span>
                    </div>
                    <div className="pt-1">
                      <Link
                        href={`/chat?agent=${a.agentId}`}
                        className="inline-flex items-center gap-1 text-sm text-[var(--color-primary)] hover:underline"
                      >
                        Dùng agent này →
                      </Link>
                    </div>
                  </article>
                ))}
              </div>
            </section>
          )}

          {others.length > 0 && (
            <section className="space-y-3">
              <h2 className="font-serif text-lg text-[var(--color-muted-fg)]">
                Chưa sẵn sàng ({others.length})
              </h2>
              <p className="text-xs text-[var(--color-muted-fg)]">
                Các agent này đã gán cho bạn nhưng chưa production hoặc đã tạm khoá.
              </p>
              <div className="overflow-hidden rounded border">
                <table className="w-full text-sm">
                  <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                    <tr>
                      <th className="px-3 py-2 text-left">Agent</th>
                      <th className="px-3 py-2 text-left">Trạng thái</th>
                    </tr>
                  </thead>
                  <tbody>
                    {others.map((a) => (
                      <tr key={a.agentId} className="border-t opacity-70">
                        <td className="px-3 py-2">
                          <div className="font-medium">{a.agentName}</div>
                          <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">
                            {a.agentCode} · version {a.publishedVersion ?? "unpublished"}
                          </div>
                        </td>
                        <td className="px-3 py-2">
                          {a.agentDisabled ? (
                            <span className="inline-flex items-center gap-1 rounded-full bg-red-100 px-2 py-0.5 text-[10px] text-red-700">
                              <AlertCircle className="size-3" /> Admin đã khoá agent
                            </span>
                          ) : (
                            <span
                              className={`rounded-full border px-2 py-0.5 text-[10px] ${
                                LIFECYCLE_BADGE[a.lifecycleState] ?? "border-gray-300"
                              }`}
                            >
                              {a.lifecycleState}
                            </span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}
    </div>
  );
}
