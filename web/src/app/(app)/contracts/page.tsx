import Link from "next/link";
import { redirect } from "next/navigation";
import { FileText, Plus } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";

interface ContractRecord {
  id: string;
  title: string;
  partyA?: string | null;
  partyB?: string | null;
  signedDate?: string | null;
  endDate?: string | null;
  totalValue?: number | null;
  currency?: string | null;
  status: string;
  tags?: string[];
  createdAt: string;
}

interface ContractsListResponse {
  items: ContractRecord[];
  total: number;
}

function statusBadge(status: string): string {
  switch (status) {
    case "active":
      return "bg-emerald-100 text-emerald-700";
    case "expired":
      return "bg-orange-100 text-orange-700";
    case "terminated":
      return "bg-red-100 text-red-700";
    case "archived":
      return "bg-gray-100 text-gray-600";
    default:
      return "border text-[var(--color-muted-fg)]";
  }
}

function formatVnd(amount: number | null | undefined, currency?: string | null): string {
  if (amount == null) return "—";
  const cur = currency ?? "VND";
  return `${amount.toLocaleString("vi-VN")} ${cur}`;
}

function formatDate(s: string | null | undefined): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

export default async function ContractsListPage() {
  const token = await requireSession();
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    const roles = me.roles ?? [];
    if (!roles.includes("admin") && !roles.includes("user")) {
      redirect("/chat");
    }
  }
  // Both ADMIN and USER may reach this point (see redirect above). Whether
  // intake actually succeeds is a backend decision (POST /contracts, gated
  // by contract.intake capability resolution for USER) — same "show the
  // button, let the Gateway be the authority" pattern /vouchers already
  // uses, matching D6/D11's "frontend is not an authorization boundary".
  const canManage = true;

  const res = await gateway.listContracts(token);
  const body: ContractsListResponse = res.ok
    ? await res.json()
    : { items: [], total: 0 };

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="font-serif text-3xl">Hợp đồng</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            {body.total} hợp đồng. AI tự trích xuất các trường + clauses từ PDF / text.
          </p>
        </div>
        {canManage && <Button asChild>
          <Link href="/contracts/upload">
            <Plus className="size-4" /> Thêm hợp đồng
          </Link>
        </Button>}
      </header>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-10 text-center">
          <FileText className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="font-medium">Chưa có hợp đồng nào</p>
          <p className="mt-1 text-sm text-[var(--color-muted-fg)]">
            Bấm "Thêm hợp đồng" để upload PDF hoặc paste nội dung.
          </p>
          {canManage && <Button asChild className="mt-4">
            <Link href="/contracts/upload">
              <Plus className="size-4" /> Thêm hợp đồng đầu tiên
            </Link>
          </Button>}
        </div>
      ) : (
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Tiêu đề</th>
                <th className="px-4 py-2 text-left">Bên A / Bên B</th>
                <th className="px-4 py-2 text-left">Hiệu lực</th>
                <th className="px-4 py-2 text-right">Giá trị</th>
                <th className="px-4 py-2 text-center">Trạng thái</th>
              </tr>
            </thead>
            <tbody>
              {body.items.map((c) => (
                <tr key={c.id} className="border-t hover:bg-[var(--color-muted)]/30">
                  <td className="px-4 py-3">
                    <Link
                      href={`/contracts/${c.id}`}
                      className="font-medium text-[var(--color-primary)] hover:underline"
                    >
                      {c.title}
                    </Link>
                    {c.tags && c.tags.length > 0 && (
                      <div className="mt-1 flex flex-wrap gap-1">
                        {c.tags.slice(0, 4).map((t) => (
                          <span
                            key={t}
                            className="rounded-full border px-1.5 py-0.5 text-[10px] text-[var(--color-muted-fg)]"
                          >
                            {t}
                          </span>
                        ))}
                      </div>
                    )}
                  </td>
                  <td className="px-4 py-3 text-xs">
                    <div className="font-medium">{c.partyA ?? "—"}</div>
                    <div className="text-[var(--color-muted-fg)]">⇄ {c.partyB ?? "—"}</div>
                  </td>
                  <td className="px-4 py-3 text-xs">
                    <div>Ký: {formatDate(c.signedDate)}</div>
                    <div className="text-[var(--color-muted-fg)]">Hết: {formatDate(c.endDate)}</div>
                  </td>
                  <td className="px-4 py-3 text-right font-mono text-xs">
                    {formatVnd(c.totalValue, c.currency)}
                  </td>
                  <td className="px-4 py-3 text-center">
                    <span
                      className={`inline-block rounded-full px-2 py-0.5 text-xs ${statusBadge(c.status)}`}
                    >
                      {c.status}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
