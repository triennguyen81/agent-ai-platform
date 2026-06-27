import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { CheckCircle2, Inbox, ShieldAlert, ThumbsDown, ThumbsUp } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface ReviewRequest {
  id: string;
  tenantId: string;
  agentId: string;
  agentCode: string;
  agentName: string;
  boardId: string;
  boardName: string;
  requestedBy: string;
  requestedByEmail: string | null;
  targetState: string;
  status: string;
  rationale: string | null;
  decidedAt: string | null;
  createdAt: string;
  approveCount: number;
  rejectCount: number;
  quorum: number;
}

async function voteAction(formData: FormData): Promise<void> {
  "use server";
  const requestId = String(formData.get("requestId") ?? "");
  const vote = String(formData.get("vote") ?? "");
  const comment = String(formData.get("comment") ?? "");
  if (!requestId || (vote !== "approve" && vote !== "reject")) {
    redirect("/admin/review-queue?error=invalid");
  }
  const token = await requireSession();
  const res = await gateway.castReviewVote(token, requestId, {
    vote,
    comment: comment || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/review-queue?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/review-queue");
  redirect(`/admin/review-queue?ok=${vote}`);
}

function formatDateTime(s: string | null): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleString("vi-VN");
  } catch {
    return s;
  }
}

export default async function ReviewQueuePage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const token = await requireSession();
  const res = await gateway.myReviewQueue(token);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: ReviewRequest[]; total: number })
    : { items: [], total: 0 };
  const { ok, error } = await searchParams;

  return (
    <div className="space-y-6">
      <header className="space-y-1">
        <h1 className="font-serif text-3xl">Review Queue</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          {body.total} request pending mà bạn là reviewer. Vote approve khi đủ quorum →
          agent được promote lên production.
        </p>
      </header>

      {ok && (
        <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
          Vote đã ghi nhận.
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
            Không có request nào chờ bạn vote.
          </p>
        </div>
      ) : (
        <ul className="space-y-4">
          {body.items.map((r) => (
            <li
              key={r.id}
              className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5"
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <Link
                    href={`/admin/agents/${r.agentId}`}
                    className="font-medium hover:underline"
                  >
                    {r.agentName}
                  </Link>
                  <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">
                    {r.agentCode} · target: {r.targetState}
                  </div>
                  <div className="mt-1 text-xs text-[var(--color-muted-fg)]">
                    Submitted by {r.requestedByEmail ?? r.requestedBy} ·{" "}
                    {formatDateTime(r.createdAt)}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">
                    <ThumbsUp className="size-3" /> {r.approveCount}
                  </span>
                  <span className="inline-flex items-center gap-1 rounded-full border border-red-300 bg-red-50 px-2 py-0.5 text-xs text-red-700">
                    <ThumbsDown className="size-3" /> {r.rejectCount}
                  </span>
                  <span className="text-xs text-[var(--color-muted-fg)]">
                    / quorum {r.quorum}
                  </span>
                </div>
              </div>

              {r.rationale && (
                <p className="rounded border-l-4 border-[var(--color-primary)] bg-[var(--color-muted)] px-3 py-2 text-sm">
                  {r.rationale}
                </p>
              )}

              <div className="grid gap-3 lg:grid-cols-2">
                <form action={voteAction} className="space-y-2">
                  <input type="hidden" name="requestId" value={r.id} />
                  <input type="hidden" name="vote" value="approve" />
                  <Label htmlFor={`a-${r.id}`}>Comment (optional)</Label>
                  <Input id={`a-${r.id}`} name="comment" placeholder="LGTM..." />
                  <Button type="submit" size="sm" className="w-full">
                    <CheckCircle2 className="size-4" /> Approve
                  </Button>
                </form>
                <form action={voteAction} className="space-y-2">
                  <input type="hidden" name="requestId" value={r.id} />
                  <input type="hidden" name="vote" value="reject" />
                  <Label htmlFor={`r-${r.id}`}>Lý do reject</Label>
                  <Input id={`r-${r.id}`} name="comment" placeholder="Cần fix..." />
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
