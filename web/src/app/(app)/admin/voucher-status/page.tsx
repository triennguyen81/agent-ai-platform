import Link from "next/link";
import { AlertCircle, CheckCircle2, Clock, TrendingDown, XCircle } from "lucide-react";
import { GATEWAY_BASE } from "@/lib/api";
import { requireSession } from "@/lib/auth";

// Sprint 6' A6 — báo cáo tổng hợp trạng thái chứng từ theo bucket
// nghiệp vụ mà anh Toàn nhấn mạnh trong 2 tài liệu tham khảo:
//   - đã đối soát (verified)
//   - còn treo chưa xử lý (pending)
//   - quá hạn (pending + document_date < now - overdueDays)
//   - lỗi cần xem (ocr_unavailable, processing timeout)
//   - bị reject (dup, invalid)
//
// Deep-link ra /vouchers với filter tương ứng để KT trưởng bấm vào drill-down.

interface StatusReport {
  verifiedCount: number;
  pendingCount: number;
  overdueCount: number;
  failedCount: number;
  rejectedCount: number;
}

interface StatusReportResponse {
  report: StatusReport;
  overdueDays: number;
  overdueThresholdDate: string;
}

interface PageProps {
  searchParams: Promise<{ overdueDays?: string }>;
}

export default async function VoucherStatusReportPage({ searchParams }: PageProps) {
  const params = await searchParams;
  const overdueDays = Math.max(1, Math.min(365, parseInt(params.overdueDays ?? "30", 10) || 30));
  const token = await requireSession();

  const res = await fetch(
    `${GATEWAY_BASE}/vouchers/status-report?overdueDays=${overdueDays}`,
    {
      headers: { Authorization: `Bearer ${token}` },
      cache: "no-store",
    },
  );
  const body: StatusReportResponse = res.ok
    ? await res.json()
    : {
        report: {
          verifiedCount: 0,
          pendingCount: 0,
          overdueCount: 0,
          failedCount: 0,
          rejectedCount: 0,
        },
        overdueDays,
        overdueThresholdDate: "",
      };

  const total =
    body.report.verifiedCount +
    body.report.pendingCount +
    body.report.failedCount +
    body.report.rejectedCount;

  const pct = (n: number) => (total > 0 ? ((n / total) * 100).toFixed(1) : "0");

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">
          <TrendingDown className="mr-2 inline size-6" /> Báo cáo trạng thái chứng từ
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Tổng hợp toàn tenant. Overdue window: {body.overdueDays} ngày (ngày ranh giới:{" "}
          {body.overdueThresholdDate}). Bấm vào card để drill-down xem danh sách.
        </p>
      </header>

      {/* Overdue window control */}
      <form action="/admin/voucher-status" method="GET" className="flex items-center gap-2 text-sm">
        <label htmlFor="overdueDays" className="text-[var(--color-muted-fg)]">
          Quá hạn nếu tồn treo &gt;
        </label>
        <input
          id="overdueDays"
          name="overdueDays"
          type="number"
          min={1}
          max={365}
          defaultValue={overdueDays}
          className="w-20 rounded-[var(--radius)] border px-2 py-1"
        />
        <span className="text-[var(--color-muted-fg)]">ngày</span>
        <button
          type="submit"
          className="rounded-[var(--radius)] border border-[var(--color-primary)] px-2 py-1 text-xs text-[var(--color-primary)] hover:bg-[var(--color-primary)]/10"
        >
          Cập nhật
        </button>
      </form>

      {/* Summary cards */}
      <section className="grid gap-3 sm:grid-cols-3 lg:grid-cols-5">
        <Link
          href="/vouchers?status=verified"
          className="rounded-[var(--radius)] border p-4 transition hover:border-emerald-500 hover:shadow"
        >
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <CheckCircle2 className="size-3.5 text-emerald-600" /> Đã đối soát
          </div>
          <div className="mt-1 font-mono text-2xl font-bold text-emerald-700">
            {body.report.verifiedCount}
          </div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">{pct(body.report.verifiedCount)}%</div>
        </Link>

        <Link
          href="/vouchers?status=extracted"
          className="rounded-[var(--radius)] border p-4 transition hover:border-amber-500 hover:shadow"
        >
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <Clock className="size-3.5 text-amber-600" /> Còn treo
          </div>
          <div className="mt-1 font-mono text-2xl font-bold text-amber-700">
            {body.report.pendingCount}
          </div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">
            {pct(body.report.pendingCount)}%
          </div>
        </Link>

        <Link
          href={`/vouchers?status=extracted&documentDateTo=${body.overdueThresholdDate}`}
          className={`rounded-[var(--radius)] border p-4 transition hover:border-rose-500 hover:shadow ${
            body.report.overdueCount > 0
              ? "border-rose-300 bg-rose-50/50 dark:bg-rose-950/20"
              : ""
          }`}
        >
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <AlertCircle className="size-3.5 text-rose-600" /> Quá hạn
          </div>
          <div className="mt-1 font-mono text-2xl font-bold text-rose-700">
            {body.report.overdueCount}
          </div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">
            &gt; {body.overdueDays} ngày chưa Accept
          </div>
        </Link>

        <Link
          href="/vouchers?status=ocr_unavailable"
          className="rounded-[var(--radius)] border p-4 transition hover:border-slate-500 hover:shadow"
        >
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <XCircle className="size-3.5 text-slate-500" /> Lỗi OCR
          </div>
          <div className="mt-1 font-mono text-2xl font-bold text-slate-700">
            {body.report.failedCount}
          </div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">Cần xem manual</div>
        </Link>

        <Link
          href="/vouchers?status=rejected"
          className="rounded-[var(--radius)] border p-4 transition hover:border-red-500 hover:shadow"
        >
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <XCircle className="size-3.5 text-red-500" /> Bị từ chối
          </div>
          <div className="mt-1 font-mono text-2xl font-bold text-red-700">
            {body.report.rejectedCount}
          </div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">Trùng lặp / invalid</div>
        </Link>
      </section>

      {/* Health signals */}
      <section className="rounded-[var(--radius)] border p-4">
        <h2 className="font-serif text-lg">Signal cần chú ý</h2>
        <ul className="mt-2 space-y-1.5 text-sm">
          {body.report.overdueCount > 0 && (
            <li className="text-rose-700">
              🔴 {body.report.overdueCount} chứng từ đã treo &gt; {body.overdueDays} ngày — cần
              chốt gấp trước cuối tháng.
            </li>
          )}
          {body.report.failedCount > 5 && (
            <li className="text-amber-700">
              🟡 {body.report.failedCount} chứng từ lỗi OCR — check xem có scan mờ hàng loạt
              hay pipeline sự cố.
            </li>
          )}
          {body.report.rejectedCount > 0 && (
            <li className="text-slate-600">
              ℹ️ {body.report.rejectedCount} chứng từ bị reject — thường do trùng HĐ.
            </li>
          )}
          {total === body.report.verifiedCount && total > 0 && (
            <li className="text-emerald-700">
              🟢 Tất cả {total} chứng từ đã đối soát — sổ sạch, không có tồn treo.
            </li>
          )}
          {total === 0 && (
            <li className="text-[var(--color-muted-fg)]">
              Chưa có chứng từ nào trong tenant.
            </li>
          )}
        </ul>
      </section>
    </div>
  );
}
