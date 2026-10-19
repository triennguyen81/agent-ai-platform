import Link from "next/link";
import {
  INVOICE_STATUS_LABEL,
  scheduleLabel,
  scheduleRemaining,
  scheduleStatusLabel,
  type ContractScheduleImpactView,
} from "@/lib/contract-schedule";

const fmt = (n: number | null | undefined) => (n == null ? "—" : Number(n).toLocaleString("vi-VN"));

const STATUS_CLASS: Record<string, string> = {
  pending: "border-slate-300 bg-slate-50 text-slate-700",
  partial: "border-amber-300 bg-amber-50 text-amber-800",
  paid: "border-emerald-300 bg-emerald-50 text-emerald-800",
};

/**
 * Hợp đồng / đợt thanh toán affected by this document's allocations. Rendered
 * only for invoices explicitly linked to a schedule; nothing is shown (or
 * guessed) for invoices without a contract link.
 */
export function ContractScheduleImpact({ schedules }: { schedules: ContractScheduleImpactView[] }) {
  if (schedules.length === 0) return null;
  return (
    <div className="space-y-2" data-testid="contract-schedule-impact">
      <div className="text-sm font-medium">Đợt thanh toán hợp đồng liên quan</div>
      {schedules.map((s) => (
        <div key={s.scheduleId} className="rounded border px-3 py-2 text-sm" data-schedule-id={s.scheduleId} data-schedule-status={s.status}>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <span>
              Hợp đồng:{" "}
              <Link href={`/contracts/${s.contractId}`} className="font-mono font-medium underline">
                {s.contractNumber ?? s.contractTitle ?? s.contractId.slice(0, 8)}
              </Link>
            </span>
            <span>
              Đợt thanh toán: <b>{scheduleLabel(s)}</b>
            </span>
            <span className={`rounded border px-1.5 py-0.5 text-xs font-medium ${STATUS_CLASS[s.status] ?? ""}`} data-testid="schedule-status">
              {scheduleStatusLabel(s.status)}
            </span>
          </div>
          <div className="mt-1 text-xs text-[var(--color-muted-fg)]">
            Đã thu cho đợt <b className="font-mono">{fmt(s.paidAmount)}</b> / <span className="font-mono">{fmt(s.scheduleTotal)}</span>
            {" · "}Còn lại <b className="font-mono">{fmt(scheduleRemaining(s))}</b>
            {" · "}Tiến độ HĐ <b className="font-mono" data-testid="contract-progress">{fmt(s.contractProgressPercent)}%</b>
          </div>
          <table className="mt-1 w-full text-xs">
            <thead className="text-left text-[var(--color-muted-fg)]">
              <tr><th className="py-0.5">Hoá đơn</th><th className="py-0.5 text-right">Chứng từ này đối trừ</th><th className="py-0.5 text-right">Đã thu / Giá trị HĐ</th><th className="py-0.5 pl-2">Trạng thái HĐ</th></tr>
            </thead>
            <tbody>
              {s.invoices.map((i) => (
                <tr key={i.invoiceId} className="border-t">
                  <td className="py-0.5 font-mono">{i.documentNumber}</td>
                  <td className="py-0.5 text-right font-mono">{fmt(i.appliedByThisDocument)}</td>
                  <td className="py-0.5 text-right font-mono">{fmt(i.paidAmount)} / {fmt(i.originalAmount)}</td>
                  <td className="py-0.5 pl-2">{INVOICE_STATUS_LABEL[i.invoiceStatus] ?? i.invoiceStatus}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
    </div>
  );
}
