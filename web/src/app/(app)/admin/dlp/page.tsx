import Link from "next/link";
import { Lock, ShieldCheck, TrendingUp } from "lucide-react";
import { GATEWAY_BASE } from "@/lib/api";
import { requireSession } from "@/lib/auth";

// Sprint 7 Phase B — DLP audit dashboard.
// Hiển thị summary PII events được scrub/unmask trong N ngày gần nhất.
// Policy backend "tenant_governance" → chỉ tenant_owner + super_admin
// vào được URL này; user role khác sẽ bị 403.

interface DlpSummaryResponse {
  totalEvents: number;
  totalPiiDetected: number;
  typeCounts: Record<string, number>;
  dailyBuckets: Array<{ date: string; eventCount: number; piiCount: number }>;
  windowDays: number;
}

interface PageProps {
  searchParams: Promise<{ days?: string }>;
}

const TYPE_LABELS: Record<string, string> = {
  Cmnd: "CMND",
  Cccd: "CCCD",
  Mst: "Mã số thuế",
  Phone: "Số điện thoại",
  Email: "Email",
  BankAccount: "STK ngân hàng",
  CreditCard: "Credit card",
  Passport: "Hộ chiếu",
  Bhxh: "BHXH",
  DateOfBirth: "Ngày sinh",
};

export default async function DlpDashboardPage({ searchParams }: PageProps) {
  const params = await searchParams;
  const days = Math.max(1, Math.min(90, parseInt(params.days ?? "7", 10) || 7));
  const token = await requireSession();

  const res = await fetch(`${GATEWAY_BASE}/dlp/summary?days=${days}`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
  });

  if (!res.ok) {
    const errText = await res.text().catch(() => "");
    return (
      <div className="space-y-4">
        <h1 className="font-serif text-3xl">
          <ShieldCheck className="mr-2 inline size-6" /> DLP Dashboard
        </h1>
        <div className="rounded-[var(--radius)] border border-red-300 bg-red-50 p-4 text-sm text-red-800">
          Không tải được dữ liệu DLP. Status: {res.status}. {errText.slice(0, 200)}
        </div>
      </div>
    );
  }

  const body: DlpSummaryResponse = await res.json();
  const sortedTypes = Object.entries(body.typeCounts).sort((a, b) => b[1] - a[1]);
  const topType = sortedTypes[0];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">
          <ShieldCheck className="mr-2 inline size-6 text-emerald-600" /> DLP — Data Loss Prevention
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Thống kê PII được mask trước khi gửi ra LLM cloud (compliance NĐ13/2023). Window: {body.windowDays} ngày gần nhất.
        </p>
      </header>

      {/* Window control */}
      <form action="/admin/dlp" method="GET" className="flex items-center gap-2 text-sm">
        <label htmlFor="days" className="text-[var(--color-muted-fg)]">
          Khoảng thời gian:
        </label>
        <input
          id="days"
          name="days"
          type="number"
          min={1}
          max={90}
          defaultValue={days}
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
      <section className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-[var(--radius)] border p-4">
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <TrendingUp className="size-3.5 text-emerald-600" /> Tổng events
          </div>
          <div className="mt-1 font-mono text-2xl font-bold">{body.totalEvents.toLocaleString("vi-VN")}</div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">calls đã scrub/unmask</div>
        </div>

        <div className="rounded-[var(--radius)] border p-4">
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <Lock className="size-3.5 text-indigo-600" /> Tổng PII bị mask
          </div>
          <div className="mt-1 font-mono text-2xl font-bold">{body.totalPiiDetected.toLocaleString("vi-VN")}</div>
          <div className="text-[11px] text-[var(--color-muted-fg)]">số lần PII bị thay bằng token</div>
        </div>

        <div className="rounded-[var(--radius)] border p-4">
          <div className="flex items-center gap-2 text-xs text-[var(--color-muted-fg)]">
            <ShieldCheck className="size-3.5 text-amber-600" /> Loại phổ biến nhất
          </div>
          {topType ? (
            <>
              <div className="mt-1 font-mono text-2xl font-bold">{TYPE_LABELS[topType[0]] ?? topType[0]}</div>
              <div className="text-[11px] text-[var(--color-muted-fg)]">
                {topType[1].toLocaleString("vi-VN")} lần
              </div>
            </>
          ) : (
            <div className="mt-1 text-sm text-[var(--color-muted-fg)]">(không có data)</div>
          )}
        </div>
      </section>

      {/* Type breakdown */}
      <section className="rounded-[var(--radius)] border p-4">
        <h2 className="font-serif text-lg">Chi tiết theo loại PII</h2>
        {sortedTypes.length === 0 ? (
          <p className="mt-2 text-sm text-[var(--color-muted-fg)]">
            Chưa có PII event nào trong {body.windowDays} ngày. Có thể DLP đang disable hoặc chưa có voucher upload.
          </p>
        ) : (
          <table className="mt-3 w-full text-sm">
            <thead className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="pb-2 text-left">Loại</th>
                <th className="pb-2 text-right">Số lần mask</th>
                <th className="pb-2 text-right">% tổng</th>
              </tr>
            </thead>
            <tbody>
              {sortedTypes.map(([type, count]) => (
                <tr key={type} className="border-t">
                  <td className="py-2">{TYPE_LABELS[type] ?? type}</td>
                  <td className="py-2 text-right font-mono">{count.toLocaleString("vi-VN")}</td>
                  <td className="py-2 text-right text-[var(--color-muted-fg)]">
                    {body.totalPiiDetected > 0
                      ? ((count / body.totalPiiDetected) * 100).toFixed(1) + "%"
                      : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {/* Daily buckets */}
      <section className="rounded-[var(--radius)] border p-4">
        <h2 className="font-serif text-lg">Hoạt động theo ngày</h2>
        {body.dailyBuckets.length === 0 ? (
          <p className="mt-2 text-sm text-[var(--color-muted-fg)]">Không có hoạt động.</p>
        ) : (
          <table className="mt-3 w-full text-sm">
            <thead className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="pb-2 text-left">Ngày</th>
                <th className="pb-2 text-right">Events</th>
                <th className="pb-2 text-right">PII masked</th>
              </tr>
            </thead>
            <tbody>
              {body.dailyBuckets.map((b) => (
                <tr key={b.date} className="border-t">
                  <td className="py-2">{b.date}</td>
                  <td className="py-2 text-right font-mono">{b.eventCount.toLocaleString("vi-VN")}</td>
                  <td className="py-2 text-right font-mono text-indigo-700">
                    {b.piiCount.toLocaleString("vi-VN")}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {/* Compliance footer */}
      <section className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-50/50 p-4 text-sm">
        <div className="flex items-start gap-2">
          <ShieldCheck className="mt-0.5 size-4 text-emerald-700" />
          <div>
            <p className="font-medium text-emerald-900">Compliance NĐ13/2023 — Luật Bảo vệ dữ liệu cá nhân</p>
            <p className="mt-1 text-emerald-800">
              Mọi PII (CMND, CCCD, MST, SĐT, email, STK, hộ chiếu, ngày sinh) được thay bằng token placeholder{" "}
              <code className="rounded bg-emerald-100 px-1 text-xs">&lt;TYPE_xxxxxxxxxx&gt;</code> trước khi gửi
              ra LLM cloud. Response trả về được un-mask để user vẫn thấy PII thật.
              Audit log <code>sim_dlp_events</code> immutable (INSERT-only), không lưu PII values thật.
            </p>
            <p className="mt-2 text-xs text-emerald-700">
              <Link href="/docs/dlp-architecture" className="underline">
                Xem chi tiết kiến trúc DLP →
              </Link>
            </p>
          </div>
        </div>
      </section>
    </div>
  );
}
