import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { AlertOctagon, CheckCircle2, Inbox, ShieldAlert } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface PendingAction {
  id: string;
  tenantId: string;
  gateId: string;
  gateName: string;
  agentId: string;
  agentCode: string;
  agentName: string;
  triggeredBy: string | null;
  triggeredByEmail: string | null;
  toolCode: string;
  payloadJson: string;
  status: string;
  createdAt: string;
  expiresAt: string | null;
}

async function decideAction(formData: FormData): Promise<void> {
  "use server";
  const actionId = String(formData.get("actionId") ?? "");
  const decision = String(formData.get("decision") ?? "");
  const comment = String(formData.get("comment") ?? "");
  if (!actionId || (decision !== "approve" && decision !== "reject")) {
    redirect("/admin/hitl-queue?error=invalid");
  }
  const token = await requireSession();
  const res = await gateway.decideHitlAction(token, actionId, {
    decision,
    comment: comment || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/hitl-queue?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/hitl-queue");
  redirect(`/admin/hitl-queue?ok=${decision}d`);
}

function formatDateTime(s: string | null): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleString("vi-VN");
  } catch {
    return s;
  }
}

function prettyJson(s: string): string {
  try {
    return JSON.stringify(JSON.parse(s), null, 2);
  } catch {
    return s;
  }
}

export default async function HitlQueuePage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const token = await requireSession();
  const res = await gateway.hitlQueue(token);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: PendingAction[]; total: number })
    : { items: [], total: 0 };
  const { ok, error } = await searchParams;

  return (
    <div className="space-y-6">
      <header className="space-y-1">
        <h1 className="font-serif text-3xl">HITL Queue</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          {body.total} hành động đang đợi duyệt từ con người. Hành động bị chặn cho tới khi
          approve/reject.
        </p>
      </header>

      {ok && (
        <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
          Đã ghi nhận quyết định.
        </p>
      )}
      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {decodeURIComponent(error)}
        </p>
      )}

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center">
          <Inbox className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="text-sm text-[var(--color-muted-fg)]">
            Không có action nào pending.
          </p>
        </div>
      ) : (
        <ul className="space-y-4">
          {body.items.map((a) => (
            <li
              key={a.id}
              className="space-y-3 rounded-[var(--radius)] border border-amber-300 bg-amber-50/30 p-5"
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <AlertOctagon className="size-4 text-amber-700" />
                    <span className="font-medium">{a.gateName}</span>
                    <span className="font-mono text-[10px] text-[var(--color-muted-fg)]">
                      {a.toolCode}
                    </span>
                  </div>
                  <div className="mt-1 text-xs text-[var(--color-muted-fg)]">
                    Agent:{" "}
                    <Link href={`/admin/agents/${a.agentId}`} className="underline">
                      {a.agentName}
                    </Link>{" "}
                    · Triggered by {a.triggeredByEmail ?? a.triggeredBy ?? "—"} ·{" "}
                    {formatDateTime(a.createdAt)}
                    {a.expiresAt && <span> · Expires {formatDateTime(a.expiresAt)}</span>}
                  </div>
                </div>
              </div>

              <details className="rounded border bg-[var(--color-bg)] p-3">
                <summary className="cursor-pointer text-xs font-medium">
                  Payload
                </summary>
                <pre className="mt-2 max-h-64 overflow-auto text-xs">
                  {prettyJson(a.payloadJson)}
                </pre>
              </details>

              <div className="grid gap-3 lg:grid-cols-2">
                <form action={decideAction} className="space-y-2">
                  <input type="hidden" name="actionId" value={a.id} />
                  <input type="hidden" name="decision" value="approve" />
                  <Label htmlFor={`a-${a.id}`}>Comment (optional)</Label>
                  <Input id={`a-${a.id}`} name="comment" placeholder="OK to proceed..." />
                  <Button type="submit" size="sm" className="w-full">
                    <CheckCircle2 className="size-4" /> Approve
                  </Button>
                </form>
                <form action={decideAction} className="space-y-2">
                  <input type="hidden" name="actionId" value={a.id} />
                  <input type="hidden" name="decision" value="reject" />
                  <Label htmlFor={`r-${a.id}`}>Lý do reject</Label>
                  <Input id={`r-${a.id}`} name="comment" placeholder="Không hợp lệ..." />
                  <Button type="submit" size="sm" variant="outline" className="w-full">
                    <ShieldAlert className="size-4" /> Reject
                  </Button>
                </form>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
