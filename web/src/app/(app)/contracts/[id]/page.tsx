import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AlertTriangle, ArrowLeft, Calendar, FileText, Scale } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

interface ContractRecord {
  id: string;
  title: string;
  partyA?: string | null;
  partyB?: string | null;
  signedDate?: string | null;
  effectiveDate?: string | null;
  endDate?: string | null;
  totalValue?: number | null;
  currency?: string | null;
  status: string;
  tags?: string[];
  rawText?: string | null;
  sourcePdfPath?: string | null;
  createdAt: string;
  updatedAt: string;
}

interface ClauseRecord {
  id: string;
  ordinal: number;
  heading?: string | null;
  body: string;
  hasDeadline: boolean;
  hasPenalty: boolean;
}

interface ContractWithClauses {
  contract: ContractRecord;
  clauses: ClauseRecord[];
}

function formatVnd(amount: number | null | undefined, currency?: string | null): string {
  if (amount == null) return "—";
  return `${amount.toLocaleString("vi-VN")} ${currency ?? "VND"}`;
}

function formatDate(s: string | null | undefined): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

interface Props {
  params: Promise<{ id: string }>;
}

export default async function ContractDetailPage({ params }: Props) {
  const { id } = await params;
  const token = await requireSession();

  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    const roles = me.roles ?? [];
    if (!roles.includes("super_admin") && !roles.includes("admin")) {
      redirect("/chat");
    }
  }

  const res = await gateway.getContract(token, id);
  if (res.status === 404) notFound();
  if (!res.ok) {
    return (
      <p className="text-sm text-red-700">
        Không tải được hợp đồng (HTTP {res.status}).
      </p>
    );
  }

  const data = (await res.json()) as ContractWithClauses;
  const { contract, clauses } = data;

  return (
    <div className="space-y-6">
      <div>
        <Link
          href="/contracts"
          className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        >
          <ArrowLeft className="size-3.5" /> Tất cả hợp đồng
        </Link>
      </div>

      <header className="space-y-2">
        <h1 className="font-serif text-3xl">{contract.title}</h1>
        <div className="flex flex-wrap items-center gap-2">
          <span className="rounded-full border px-2 py-0.5 text-xs">
            {contract.status}
          </span>
          {contract.tags?.map((t) => (
            <span key={t} className="rounded-full border px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]">
              {t}
            </span>
          ))}
        </div>
      </header>

      <div className="grid gap-4 lg:grid-cols-2">
        <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="font-serif text-lg">Các bên</h2>
          <div className="space-y-1 text-sm">
            <div>
              <span className="text-[var(--color-muted-fg)]">Bên A:</span>{" "}
              <span className="font-medium">{contract.partyA ?? "—"}</span>
            </div>
            <div>
              <span className="text-[var(--color-muted-fg)]">Bên B:</span>{" "}
              <span className="font-medium">{contract.partyB ?? "—"}</span>
            </div>
          </div>
        </section>

        <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="font-serif text-lg">
            <Calendar className="mr-2 inline size-4 text-[var(--color-primary)]" />
            Thời gian
          </h2>
          <div className="space-y-1 text-sm">
            <div>Ngày ký: <span className="font-medium">{formatDate(contract.signedDate)}</span></div>
            <div>Hiệu lực: <span className="font-medium">{formatDate(contract.effectiveDate)}</span></div>
            <div>Kết thúc: <span className="font-medium">{formatDate(contract.endDate)}</span></div>
          </div>
        </section>

        <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="font-serif text-lg">
            <Scale className="mr-2 inline size-4 text-[var(--color-primary)]" />
            Giá trị
          </h2>
          <p className="font-mono text-lg">
            {formatVnd(contract.totalValue, contract.currency)}
          </p>
        </section>

        <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="font-serif text-lg">Metadata</h2>
          <div className="space-y-1 text-xs text-[var(--color-muted-fg)]">
            <div>ID: <code>{contract.id}</code></div>
            <div>Tạo: {formatDate(contract.createdAt)}</div>
            <div>Cập nhật: {formatDate(contract.updatedAt)}</div>
            {contract.sourcePdfPath && <div>PDF: {contract.sourcePdfPath}</div>}
          </div>
        </section>
      </div>

      <section className="space-y-3">
        <h2 className="font-serif text-xl">
          <FileText className="mr-2 inline size-5 text-[var(--color-primary)]" />
          Điều khoản ({clauses.length})
        </h2>
        {clauses.length === 0 ? (
          <p className="rounded-[var(--radius)] border border-dashed p-6 text-center text-sm text-[var(--color-muted-fg)]">
            Chưa có clause nào được trích xuất.
          </p>
        ) : (
          <div className="space-y-2">
            {clauses.map((clause) => (
              <div
                key={clause.id}
                className={`rounded-[var(--radius)] border bg-[var(--color-bg)] p-4 ${
                  clause.hasPenalty ? "border-l-4 border-l-red-500" : ""
                }`}
              >
                <div className="flex items-start justify-between gap-2">
                  <h3 className="font-medium">
                    {clause.heading ?? `Điều ${clause.ordinal}`}
                  </h3>
                  <div className="flex shrink-0 gap-1">
                    {clause.hasDeadline && (
                      <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2 py-0.5 text-[10px] text-amber-700">
                        <Calendar className="size-3" /> Có deadline
                      </span>
                    )}
                    {clause.hasPenalty && (
                      <span className="inline-flex items-center gap-1 rounded-full bg-red-100 px-2 py-0.5 text-[10px] text-red-700">
                        <AlertTriangle className="size-3" /> Có phạt
                      </span>
                    )}
                  </div>
                </div>
                <p className="mt-2 whitespace-pre-wrap text-sm text-[var(--color-fg)]">
                  {clause.body}
                </p>
              </div>
            ))}
          </div>
        )}
      </section>

      {contract.rawText && (
        <section className="space-y-2">
          <details className="rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
            <summary className="cursor-pointer font-medium">
              Raw text gốc ({contract.rawText.length} ký tự)
            </summary>
            <pre className="mt-3 max-h-96 overflow-auto whitespace-pre-wrap text-xs">
              {contract.rawText}
            </pre>
          </details>
        </section>
      )}
    </div>
  );
}
