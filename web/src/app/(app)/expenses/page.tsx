import Link from "next/link";
import { AlertTriangle, ArrowUpRight, Banknote, TrendingUp, Truck, UserCircle2 } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

// Sprint 4 D4.5 — /expenses dashboard: bức tranh chi phí + phải trả NCC tổng
// thể. Data từ /suppliers endpoint. Sprint 5 sẽ thêm HITL pending queue.

interface SupplierRecord {
  id: string;
  supplierCode: string;
  name: string;
  country: string;
  currency: string;
  currentAp: number;
  paymentTermsDays: number;
}

interface SuppliersListResponse {
  items: SupplierRecord[];
  total: number;
}

interface ApAgingSummary {
  supplierCount: number;
  totalPayable: number;
  current: number;
  bucket_0_30: number;
  bucket_31_60: number;
  bucket_61_90: number;
  over90: number;
  overdueAmount: number;
  dueWithin7Days: number;
  dueWithin30Days: number;
  largeBalanceSupplierCount: number;
}

function fmtMoney(n: number): string {
  return n.toLocaleString("vi-VN");
}

function AgingStat({ label, amount, tone }: { label: string; amount: number; tone?: "amber" | "red" }) {
  const toneClass = tone === "red" && amount > 0 ? "text-red-700" : tone === "amber" && amount > 0 ? "text-amber-700" : "";
  return (
    <div>
      <div className="text-[10px] text-[var(--color-muted-fg)]">{label}</div>
      <div className={`font-mono text-sm font-bold ${toneClass}`}>{fmtMoney(amount)}</div>
    </div>
  );
}

function countryFlag(c: string): string {
  switch (c) {
    case "VN": return "🇻🇳";
    case "DE": return "🇩🇪";
    case "JP": return "🇯🇵";
    case "US": return "🇺🇸";
    default: return "🌐";
  }
}

export default async function ExpensesPage() {
  const token = await requireSession();
  const [suppliersRes, apSummaryRes, unverifiedRes] = await Promise.all([
    gateway.listSuppliers(token, {}),
    gateway.apSummary(token),
    gateway.listVouchers(token, { status: "extracted", limit: 200 }),
  ]);
  const body = suppliersRes.ok
    ? ((await suppliersRes.json()) as SuppliersListResponse)
    : { items: [], total: 0 };
  const apSummary = apSummaryRes.ok ? ((await apSummaryRes.json()) as ApAgingSummary) : null;
  const unverifiedCount = unverifiedRes.ok
    ? ((await unverifiedRes.json()) as { total: number }).total
    : null;

  // Aggregate stats. Prefer the real ap-summary aggregate (derived from
  // sim_ap_ledger) when available; fall back to the per-supplier list sum
  // only if that call failed, so the dashboard degrades gracefully instead
  // of showing nothing.
  const totalAp = apSummary?.totalPayable ?? body.items.reduce((sum, s) => sum + s.currentAp, 0);
  const largeSuppliers = body.items.filter((s) => s.currentAp >= 500_000_000);
  const importSuppliers = body.items.filter((s) => s.country !== "VN");
  const top5 = [...body.items].sort((a, b) => b.currentAp - a.currentAp).slice(0, 5);

  return (
    <div className="space-y-6">
      <header className="space-y-1">
        <h1 className="font-serif text-3xl">
          <Banknote className="mr-2 inline size-6" /> Chi phí & Công nợ NCC
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Bức tranh tổng thể phải trả TK 331. Chi tiết từng NCC ở{" "}
          <Link href="/suppliers" className="text-[var(--color-primary)] underline">
            /suppliers
          </Link>
          .
        </p>
      </header>

      <section className="grid gap-3 md:grid-cols-4">
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Tổng phải trả (TK 331)</div>
          <div className="mt-1 font-mono text-2xl font-bold">
            {fmtMoney(totalAp)} <span className="text-sm font-normal">VNĐ</span>
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            trên {body.items.length} NCC
          </div>
        </div>
        <div
          className={`rounded-[var(--radius)] border p-4 ${
            largeSuppliers.length > 0 ? "border-red-300 bg-red-50" : ""
          }`}
        >
          <div className="text-xs text-[var(--color-muted-fg)]">NCC nợ ≥ 500M</div>
          <div
            className={`mt-1 font-mono text-2xl font-bold ${
              largeSuppliers.length > 0 ? "text-red-700" : ""
            }`}
          >
            {largeSuppliers.length}
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            {largeSuppliers.length > 0
              ? "Cần plan thanh toán ưu tiên"
              : "Không có NCC nợ lớn"}
          </div>
        </div>
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">NCC nhập khẩu</div>
          <div className="mt-1 font-mono text-2xl font-bold text-blue-700">
            {importSuppliers.length}
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            Theo dõi tỷ giá EUR/JPY/USD
          </div>
        </div>
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Vouchers chưa verify</div>
          <div className="mt-1 font-mono text-2xl font-bold text-amber-700">
            {unverifiedCount === null ? "—" : unverifiedCount}
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            <Link
              href="/vouchers?status=extracted"
              className="text-[var(--color-primary)] underline"
            >
              Xem danh sách
            </Link>
          </div>
        </div>
      </section>

      {apSummary && (
        <section className="rounded-[var(--radius)] border p-5">
          <h2 className="mb-3 font-serif text-lg">Tuổi nợ phải trả (từ sim_ap_ledger)</h2>
          <div className="grid gap-3 sm:grid-cols-3 md:grid-cols-6">
            <AgingStat label="Trong hạn" amount={apSummary.current} />
            <AgingStat label="Quá hạn 1-30 ngày" amount={apSummary.bucket_0_30} tone="amber" />
            <AgingStat label="Quá hạn 31-60 ngày" amount={apSummary.bucket_31_60} tone="amber" />
            <AgingStat label="Quá hạn 61-90 ngày" amount={apSummary.bucket_61_90} tone="red" />
            <AgingStat label="Quá hạn > 90 ngày" amount={apSummary.over90} tone="red" />
            <AgingStat label="Tổng quá hạn" amount={apSummary.overdueAmount} tone="red" />
          </div>
          <div className="mt-3 flex flex-wrap gap-4 text-xs text-[var(--color-muted-fg)]">
            <span>Đến hạn trong 7 ngày tới: <b className="text-[var(--color-fg)]">{fmtMoney(apSummary.dueWithin7Days)} VNĐ</b></span>
            <span>Đến hạn trong 30 ngày tới: <b className="text-[var(--color-fg)]">{fmtMoney(apSummary.dueWithin30Days)} VNĐ</b></span>
          </div>
        </section>
      )}

      <section className="rounded-[var(--radius)] border p-5">
        <div className="mb-3 flex items-center justify-between">
          <h2 className="font-serif text-lg">
            <TrendingUp className="mr-2 inline size-4" /> Top 5 NCC nợ nhiều nhất
          </h2>
          <Link
            href="/suppliers"
            className="inline-flex items-center gap-1 text-xs text-[var(--color-primary)] hover:underline"
          >
            Xem tất cả <ArrowUpRight className="size-3" />
          </Link>
        </div>
        {top5.length === 0 ? (
          <p className="text-sm text-[var(--color-muted-fg)]">Không có NCC nào có công nợ &gt; 0.</p>
        ) : (
          <div className="space-y-2">
            {top5.map((s, idx) => (
              <div
                key={s.id}
                className="flex items-center gap-3 rounded-[var(--radius)] border bg-[var(--color-muted)]/20 px-3 py-2"
              >
                <div className="w-6 text-center font-mono text-lg font-bold text-[var(--color-muted-fg)]">
                  {idx + 1}
                </div>
                <div className="flex-1">
                  <div className="flex items-center gap-2">
                    <span className="text-lg">{countryFlag(s.country)}</span>
                    <span className="font-medium">{s.name}</span>
                    <span className="rounded-full border px-1.5 py-0.5 text-[10px] text-[var(--color-muted-fg)]">
                      {s.supplierCode}
                    </span>
                  </div>
                  <div className="text-xs text-[var(--color-muted-fg)]">
                    {s.currency} · Điều khoản{" "}
                    {s.paymentTermsDays > 0 ? `Net ${s.paymentTermsDays}` : "Trả trước"}
                  </div>
                </div>
                <div className="text-right">
                  <div
                    className={`font-mono text-lg font-bold ${
                      s.currentAp >= 500_000_000 ? "text-red-700" : ""
                    }`}
                  >
                    {fmtMoney(s.currentAp)}
                  </div>
                  <div className="text-[10px] text-[var(--color-muted-fg)]">VNĐ</div>
                </div>
                {s.currentAp >= 500_000_000 && (
                  <AlertTriangle className="size-4 text-red-600" />
                )}
              </div>
            ))}
          </div>
        )}
      </section>

      <section className="rounded-[var(--radius)] border border-dashed p-5">
        <h2 className="mb-2 font-serif text-lg">
          <UserCircle2 className="mr-2 inline size-4" /> Hoàn ứng nhân viên
        </h2>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Đề nghị hoàn ứng được upload qua{" "}
          <Link href="/vouchers" className="text-[var(--color-primary)] underline">
            /vouchers
          </Link>{" "}
          với voucherType=<span className="font-mono">expense_claim</span> — dùng chung pipeline
          OCR/trích xuất/duyệt như mọi voucher khác, không phải một luồng riêng.
          Widget tạm ứng chưa hoàn (TK 141) theo từng nhân viên chưa hiển thị ở đây: dữ liệu
          nhân viên hiện chưa gắn theo Department (chỉ có nhãn phòng ban dạng text), nên chưa
          thể hiển thị đúng theo ACL Department như các phần khác của trang này.
        </p>
      </section>

      <p className="text-[11px] italic text-[var(--color-muted-fg)]">
        <b>Lưu ý pilot:</b> data phải trả NCC hiển thị từ simulator (sim_suppliers + sim_ap_ledger).
        Sprint 5+ sẽ tích hợp thêm HITL queue, email notification, và audit dashboard.
      </p>
    </div>
  );
}
