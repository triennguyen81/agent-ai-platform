import Link from "next/link";
import { scheduleStatusLabel } from "@/lib/contract-schedule";
import { notFound, redirect } from "next/navigation";
import { AlertTriangle, ArrowLeft, Calendar, FileText, Scale } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { ContractChecklist } from "@/components/contract-checklist";
import { ContractDocuments } from "@/components/contract-documents";
import { ContractReviewActions } from "@/components/contract-review-actions";
import { ContractPaymentDiscount } from "@/components/contract-payment-discount";

interface ContractRecord {
  id: string;
  userId: string;
  title: string;
  partyA?: string | null;
  partyB?: string | null;
  signedDate?: string | null;
  effectiveDate?: string | null;
  endDate?: string | null;
  totalValue?: number | null;
  currency?: string | null;
  status: string;
  workflowState: string;
  documentType: string;
  direction?: string | null;
  contractNumber?: string | null;
  subtotal?: number | null;
  vatRate?: number | null;
  vatAmount?: number | null;
  retentionPercent?: number | null;
  penaltyPercent?: number | null;
  progressPercent?: number | null;
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
  parties: Array<{ id:string; partyRole:string; name:string; taxCode?:string|null }>;
  payments: Array<{ id:string; ordinal:number; description:string; percentage:number; totalAmount:number; dueDate?:string|null; triggerEvent?:string|null; offsetDays?:number|null; expectedDate?:string|null; status:string; penaltyOverdueAmount?:number|null; penaltyOverdueDays?:number|null; penaltyRate?:number|null; penaltyAmount?:number|null; penaltyCalculatedAt?:string|null }>;
  milestones: Array<{ id:string; ordinal:number; name:string; dueDate?:string|null; reminderDays:number; triggerEvent?:string|null; durationMonths?:number|null; expectedDate?:string|null; status:string }>;
  alerts: Array<{ id:string; code:string; severity:string; message:string; resolvedAt?:string|null }>;
  counterpartyProposals: Array<{ id:string; proposedAction:string; rationale:string; status:string }>;
  handoffs: Array<{ id:string; payload:string; status:string; createdAt:string }>;
  history: Array<{ id:string; eventType:string; fromState?:string|null; toState?:string|null; createdAt:string }>;
  documents: Array<{id:string;documentType:string;referencedCode?:string|null;originalFilename:string;sizeBytes:number;createdAt:string}>;
}

interface ChecklistItem {
  id: string;
  contractId: string;
  clauseId: string | null;
  title: string;
  description: string | null;
  dueDate: string | null;
  remindDaysBefore: number;
  assigneeEmail: string | null;
  status: "open" | "in_progress" | "done" | "skipped";
  completedAt: string | null;
  completedBy: string | null;
  createdAt: string;
  updatedAt: string;
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
  let viewerId = "";
  let isAdmin = false;

  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { sub?: string; roles?: string[] };
    const roles = me.roles ?? [];
    viewerId = me.sub ?? "";
    isAdmin = roles.includes("admin");
    if (!roles.includes("admin") && !roles.includes("user")) {
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
  const { contract, clauses, parties, payments, milestones, alerts, counterpartyProposals, documents, handoffs, history } = data;

  // Best-effort: load checklist via gateway helper. Failures fall through
  // to empty initial state (the client component will refetch on mount
  // if needed).
  const checklistRes = await gateway.listChecklist(token, id);
  const checklist = checklistRes.ok
    ? ((await checklistRes.json()) as { items: ChecklistItem[] }).items
    : [];

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
            {contract.workflowState}
          </span>
          {contract.direction && (
            <span className={`rounded-full border px-2 py-0.5 text-xs ${
              contract.direction === "sales" ? "border-emerald-300 bg-emerald-50 text-emerald-700"
              : contract.direction === "purchase" ? "border-blue-300 bg-blue-50 text-blue-700"
              : "border-amber-300 bg-amber-50 text-amber-700"
            }`}>
              {contract.direction === "sales" ? "Bán (Sales)" : contract.direction === "purchase" ? "Mua (Purchase)" : "Cần xác minh chiều hợp đồng"}
            </span>
          )}
          {contract.tags?.map((t) => (
            <span key={t} className="rounded-full border px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]">
              {t}
            </span>
          ))}
        </div>
      </header>
      {isAdmin && <ContractReviewActions contractId={contract.id} state={contract.workflowState} />}

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
            {parties.length > 0 && (
              <div className="mt-2 space-y-1 border-t pt-2">
                {parties.map((p) => (
                  <div key={p.id}>
                    <span className="text-[var(--color-muted-fg)]">{p.partyRole}:</span>{" "}
                    <span className="font-medium">{p.name}</span>
                    {p.taxCode && <span className="text-[var(--color-muted-fg)]"> — MST {p.taxCode}</span>}
                  </div>
                ))}
              </div>
            )}
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
          {(contract.subtotal != null || contract.vatAmount != null) && (
            <div className="text-xs text-[var(--color-muted-fg)]">
              Trước thuế: {formatVnd(contract.subtotal, contract.currency)}
              {contract.vatRate != null && <> · VAT {contract.vatRate}%</>}
              {contract.vatAmount != null && <> ({formatVnd(contract.vatAmount, contract.currency)})</>}
            </div>
          )}
          <div className="space-y-0.5 text-xs text-[var(--color-muted-fg)]">
            {contract.penaltyPercent != null && <div>Phạt chậm: {contract.penaltyPercent}%/ngày</div>}
            {contract.retentionPercent != null && <div>Tiền giữ lại: {contract.retentionPercent}%</div>}
            <div>Tiến độ thực hiện: <span data-testid="contract-progress">{(contract.progressPercent ?? 0)}</span>%</div>
          </div>
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

      <nav className="flex flex-wrap gap-2 text-sm" aria-label="Contract detail sections">
        {[['overview','Tổng quan'],['payments','Thanh toán'],['milestones','Mốc theo dõi'],['alerts','Cảnh báo'],['counterparty','Đối tác'],['handoff','Bàn giao'],['history','Lịch sử']].map(([id,label]) => <a key={id} href={`#${id}`} className="rounded-full border px-3 py-1 hover:bg-[var(--color-muted)]">{label}</a>)}
      </nav>

      <section id="overview" className="space-y-3">
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

      <section id="payments" className="space-y-3"><h2 className="font-serif text-xl">Thanh toán</h2>{payments.length===0?<p className="text-sm text-[var(--color-muted-fg)]">Chưa tạo lịch thanh toán.</p>:payments.map(p=><div key={p.id} className="rounded border p-3 text-sm"><b>{p.ordinal}. {p.description}</b><span className="float-right">{p.percentage}% · {formatVnd(p.totalAmount,contract.currency)}</span><div className="text-[var(--color-muted-fg)]">{p.triggerEvent?<>Chờ: {p.triggerEvent}{p.offsetDays!=null?` (+${p.offsetDays} ngày)`:""}{p.expectedDate?<> · Dự kiến: {formatDate(p.expectedDate)}</>:null}</>:<>Đến hạn: {formatDate(p.dueDate)}</>} · <span data-testid="contract-schedule-status" data-schedule-id={p.id} data-status={p.status}>{scheduleStatusLabel(p.status)}</span></div>{p.penaltyAmount!=null&&<div className="mt-1 text-xs text-red-700">Quá hạn {p.penaltyOverdueDays} ngày · phạt {p.penaltyRate}%/ngày trên {formatVnd(p.penaltyOverdueAmount,contract.currency)} = {formatVnd(p.penaltyAmount,contract.currency)} (tính lúc {formatDate(p.penaltyCalculatedAt)})</div>}</div>)}</section>
      <ContractPaymentDiscount contractId={contract.id} />
      <section id="milestones" className="space-y-3"><h2 className="font-serif text-xl">Mốc theo dõi</h2>{milestones.length===0?<p className="text-sm text-[var(--color-muted-fg)]">Chưa có mốc.</p>:milestones.map(m=><div key={m.id} className="rounded border p-3 text-sm"><b>{m.name}</b><div className="text-[var(--color-muted-fg)]">{m.triggerEvent?<>Chờ: {m.triggerEvent}{m.durationMonths!=null?` (${m.durationMonths} tháng)`:""}{m.expectedDate?<> · Dự kiến: {formatDate(m.expectedDate)}</>:null}</>:<>{formatDate(m.dueDate)} · nhắc trước {m.reminderDays} ngày</>}</div></div>)}</section>
      <section id="alerts" className="space-y-3"><h2 className="font-serif text-xl">Cảnh báo</h2>{alerts.length===0?<p className="text-sm text-emerald-700">Không có sai lệch đang mở.</p>:alerts.map(a=>{const cls=a.severity==="error"?"border-red-300 bg-red-50":a.severity==="warning"?"border-amber-300 bg-amber-50":"border-blue-200 bg-blue-50";const label=a.severity==="error"?"Chặn":a.severity==="warning"?"Cảnh báo":"Thông tin";return <div key={a.id} className={`rounded border p-3 text-sm ${cls}`}><span className="mr-2 rounded-full border px-1.5 py-0.5 text-[10px] uppercase tracking-wide">{label}</span><b>{a.code}</b>: {a.message}</div>;})}</section>
      <section id="counterparty" className="space-y-3"><h2 className="font-serif text-xl">Đối tác</h2>{counterpartyProposals.length===0?<p className="text-sm text-[var(--color-muted-fg)]">Chưa có đề xuất đối tác.</p>:counterpartyProposals.map(p=><div key={p.id} className="rounded border p-3 text-sm"><b>{p.proposedAction}</b><p>{p.rationale}</p></div>)}</section>
      <section id="handoff" className="space-y-3"><h2 className="font-serif text-xl">Bàn giao</h2><p className="text-xs text-[var(--color-muted-fg)]">Chỉ là gói dữ liệu xuất sau phê duyệt; không tạo bút toán, hóa đơn hay tờ khai.</p>{handoffs.map(h=><div key={h.id} className="rounded border p-3 text-sm">{h.status} · {formatDate(h.createdAt)}</div>)}</section>
      <section id="history" className="space-y-3"><h2 className="font-serif text-xl">Lịch sử</h2>{history.map(h=><div key={h.id} className="border-l-2 pl-3 text-sm">{h.eventType==="payment_schedule_status"?"Trạng thái đợt thanh toán (từ đối trừ công nợ)":h.eventType}: {h.fromState??'—'} → {h.toState??'—'} · {formatDate(h.createdAt)}</div>)}</section>

      <ContractDocuments contractId={contract.id} initialDocuments={documents} editable={(isAdmin||contract.userId===viewerId)&&!['APPROVED','HANDED_OFF','REJECTED'].includes(contract.workflowState)} />

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <ContractChecklist contractId={contract.id} initialItems={checklist} />
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
