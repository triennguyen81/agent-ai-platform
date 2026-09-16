import Link from "next/link";
import { AlertTriangle, Building2, Search, Truck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { SupplierAddPanel } from "@/components/supplier-add-panel";

// UC3 §5.9 read-only view: kế toán viên xem toàn bộ NCC + công nợ 331 tại 1
// chỗ. Cùng pattern với /customers, đối xứng AP side. Dùng để (1) demo bức
// tranh phải trả tổng thể, (2) trưởng phòng quick-check trước khi phê duyệt
// chi trả cho NCC.

interface SupplierRecord {
  id: string;
  supplierCode: string;
  name: string;
  mst: string | null;
  country: string;
  currency: string;
  paymentTerms: string | null;
  paymentTermsDays: number;
  currentAp: number;
  status: string;
}

interface SuppliersListResponse {
  items: SupplierRecord[];
  total: number;
}

const COUNTRY_LABEL: Record<string, string> = {
  VN: "Việt Nam",
  DE: "Đức",
  JP: "Nhật",
  US: "Mỹ",
};

const COUNTRY_BADGE: Record<string, string> = {
  VN: "bg-red-50 text-red-700 border-red-200",
  DE: "bg-yellow-50 text-yellow-800 border-yellow-200",
  JP: "bg-pink-50 text-pink-700 border-pink-200",
  US: "bg-blue-50 text-blue-700 border-blue-200",
};

const CURRENCY_TONE: Record<string, string> = {
  VND: "text-slate-700",
  EUR: "text-yellow-800",
  JPY: "text-pink-700",
  USD: "text-blue-700",
};

function fmtMoney(n: number): string {
  return n.toLocaleString("vi-VN");
}

function payableTone(ap: number): string {
  if (ap >= 500_000_000) return "text-red-700 font-bold";
  if (ap >= 100_000_000) return "text-amber-700 font-bold";
  if (ap > 0) return "text-slate-800";
  return "text-emerald-700";
}

interface PageProps {
  searchParams: Promise<{ country?: string; q?: string }>;
}

export default async function SuppliersPage({ searchParams }: PageProps) {
  const { country, q } = await searchParams;
  const token = await requireSession();
  const res = await gateway.listSuppliers(token, { country, q });
  const body = res.ok
    ? ((await res.json()) as SuppliersListResponse)
    : { items: [], total: 0 };

  // Aggregate stats for summary strip.
  const totalAp = body.items.reduce((sum, s) => sum + s.currentAp, 0);
  const largeSuppliers = body.items.filter((s) => s.currentAp >= 500_000_000).length;
  const importSuppliers = body.items.filter((s) => s.country !== "VN").length;

  const countries: Array<{ value: string | null; label: string }> = [
    { value: null, label: "Tất cả" },
    { value: "VN", label: "🇻🇳 VN" },
    { value: "DE", label: "🇩🇪 DE" },
    { value: "JP", label: "🇯🇵 JP" },
    { value: "US", label: "🇺🇸 US" },
  ];

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <h1 className="font-serif text-3xl">
            <Truck className="mr-2 inline size-6" /> Nhà cung cấp
          </h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Danh mục NCC + công nợ phải trả 331 hiện hành. Data đọc từ hệ thống master
            (giai đoạn pilot: simulator, giai đoạn triển khai: MCP sync từ ERP thật).
          </p>
        </div>
        <SupplierAddPanel />
      </header>

      <section className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Tổng phải trả (TK 331)</div>
          <div className="mt-1 font-mono text-2xl font-bold">
            {fmtMoney(totalAp)} <span className="text-sm font-normal">VNĐ</span>
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            trên {body.items.length} nhà cung cấp
          </div>
        </div>
        <div
          className={`rounded-[var(--radius)] border p-4 ${
            largeSuppliers > 0 ? "border-red-300 bg-red-50" : ""
          }`}
        >
          <div className="text-xs text-[var(--color-muted-fg)]">NCC nợ ≥ 500M</div>
          <div
            className={`mt-1 font-mono text-2xl font-bold ${
              largeSuppliers > 0 ? "text-red-700" : ""
            }`}
          >
            {largeSuppliers}
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            {largeSuppliers > 0 ? "Cần plan thanh toán ưu tiên" : "Không có NCC nợ lớn"}
          </div>
        </div>
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">NCC nhập khẩu</div>
          <div className="mt-1 font-mono text-2xl font-bold text-blue-700">
            {importSuppliers}
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            Cần theo dõi tỷ giá EUR/JPY/USD
          </div>
        </div>
      </section>

      <div className="flex flex-wrap items-center gap-2">
        {countries.map((c) => {
          const active = (country ?? null) === (c.value ?? null);
          const href = c.value
            ? `/suppliers?country=${c.value}${q ? `&q=${encodeURIComponent(q)}` : ""}`
            : q
              ? `/suppliers?q=${encodeURIComponent(q)}`
              : "/suppliers";
          return (
            <Link
              key={c.label}
              href={href}
              className={`rounded-full border px-3 py-1 text-xs ${
                active
                  ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-white"
                  : "hover:bg-[var(--color-muted)]"
              }`}
            >
              {c.label}
            </Link>
          );
        })}
        <form className="ml-auto flex items-center gap-2" action="/suppliers">
          {country && <input type="hidden" name="country" value={country} />}
          <div className="relative">
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-muted-fg)]" />
            <input
              name="q"
              defaultValue={q ?? ""}
              placeholder="Tìm tên NCC / MST / mã"
              className="rounded-[var(--radius)] border py-1.5 pl-7 pr-2 text-xs"
            />
          </div>
        </form>
      </div>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center text-sm text-[var(--color-muted-fg)]">
          Không tìm thấy nhà cung cấp nào.
        </div>
      ) : (
        <div className="overflow-x-auto rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Mã NCC</th>
                <th className="px-3 py-2 text-left">Tên nhà cung cấp</th>
                <th className="px-3 py-2 text-left">MST</th>
                <th className="px-3 py-2 text-left">Quốc gia</th>
                <th className="px-3 py-2 text-center">Currency</th>
                <th className="px-3 py-2 text-right">Công nợ 331</th>
                <th className="px-3 py-2 text-center">Điều khoản TT</th>
              </tr>
            </thead>
            <tbody>
              {body.items.map((s) => {
                const isLarge = s.currentAp >= 500_000_000;
                return (
                  <tr key={s.id} className="border-t hover:bg-[var(--color-muted)]/30">
                    <td className="px-3 py-2 font-mono text-xs">
                      <Link href={`/suppliers/${s.id}`} className="hover:underline">{s.supplierCode}</Link>
                    </td>
                    <td className="px-3 py-2">
                      <Link href={`/suppliers/${s.id}`} className="font-medium hover:underline">
                        <Building2 className="mr-1 inline size-3.5 text-[var(--color-muted-fg)]" />
                        {s.name}
                      </Link>
                      {s.status === "inactive" && (
                        <span className="ml-2 rounded-full border px-1.5 py-0.5 text-[10px] text-[var(--color-muted-fg)]">Ngừng HT</span>
                      )}
                      {s.paymentTerms && (
                        <div className="mt-0.5 text-[10px] italic text-[var(--color-muted-fg)]">
                          {s.paymentTerms}
                        </div>
                      )}
                    </td>
                    <td className="px-3 py-2 font-mono text-xs">{s.mst ?? "—"}</td>
                    <td className="px-3 py-2">
                      <span
                        className={`inline-block rounded-full border px-2 py-0.5 text-[10px] ${
                          COUNTRY_BADGE[s.country] ?? "border-gray-200"
                        }`}
                      >
                        {COUNTRY_LABEL[s.country] ?? s.country}
                      </span>
                    </td>
                    <td
                      className={`px-3 py-2 text-center font-mono text-xs ${
                        CURRENCY_TONE[s.currency] ?? ""
                      }`}
                    >
                      {s.currency}
                    </td>
                    <td className={`px-3 py-2 text-right font-mono ${payableTone(s.currentAp)}`}>
                      {fmtMoney(s.currentAp)}
                      {isLarge && <AlertTriangle className="ml-1 inline size-3" />}
                    </td>
                    <td className="px-3 py-2 text-center text-xs">
                      {s.paymentTermsDays > 0 ? `Net ${s.paymentTermsDays}` : "Trả trước"}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-[11px] italic text-[var(--color-muted-fg)]">
        <b>Lưu ý pilot:</b> data NCC và công nợ phải trả 331 là simulator để demo flow.
        Khi triển khai thật, sẽ kéo trực tiếp từ ERP (Misa/Fast/Bravo/SAP) qua MCP
        adapter — cấu trúc dữ liệu giữ nguyên, chỉ đổi nguồn.
      </p>
    </div>
  );
}
