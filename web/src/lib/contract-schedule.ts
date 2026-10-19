// Contract payment schedules reached through the invoices a settlement touched
// (`contractSchedules` on GET .../settlement). Computed by the Gateway from
// payment_application -> invoice -> its explicit contract_payment_schedule_id;
// the browser never infers a contract from the customer.

export interface ContractScheduleInvoiceView {
  invoiceId: string;
  documentNumber: string;
  originalAmount: number;
  paidAmount: number;
  invoiceStatus: string;
  appliedByThisDocument: number;
}

export interface ContractScheduleImpactView {
  contractId: string;
  contractNumber: string | null;
  contractTitle: string | null;
  contractProgressPercent: number;
  scheduleId: string;
  ordinal: number;
  description: string;
  dueDate: string | null;
  scheduleTotal: number;
  paidAmount: number;
  status: string;
  invoices: ContractScheduleInvoiceView[];
}

export const SCHEDULE_STATUS_LABEL: Record<string, string> = {
  pending: "CHƯA THANH TOÁN",
  partial: "THANH TOÁN MỘT PHẦN",
  paid: "ĐÃ THANH TOÁN",
};

export const INVOICE_STATUS_LABEL: Record<string, string> = {
  open: "Chưa thu",
  partial: "Thu một phần",
  paid: "Đã thu đủ",
};

export function scheduleStatusLabel(status: string): string {
  return SCHEDULE_STATUS_LABEL[status] ?? status;
}

/** "Đợt 1 — tạm ứng 40%": the description alone when it already names the đợt. */
export function scheduleLabel(s: Pick<ContractScheduleImpactView, "ordinal" | "description">): string {
  const base = `Đợt ${s.ordinal}`;
  const d = (s.description ?? "").trim();
  if (!d || d === base) return base;
  return d.toLocaleLowerCase("vi").startsWith(base.toLocaleLowerCase("vi")) ? d : `${base} — ${d}`;
}

/** Remaining amount of the schedule, as reported by the Gateway (never negative). */
export function scheduleRemaining(s: Pick<ContractScheduleImpactView, "scheduleTotal" | "paidAmount">): number {
  return Math.max(s.scheduleTotal - s.paidAmount, 0);
}

/** Defensive: older Gateway responses have no `contractSchedules` field. */
export function contractSchedulesOf(context: { contractSchedules?: ContractScheduleImpactView[] | null } | null | undefined): ContractScheduleImpactView[] {
  return context?.contractSchedules ?? [];
}
