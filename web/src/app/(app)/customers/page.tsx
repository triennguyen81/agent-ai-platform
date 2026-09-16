import Link from "next/link";
import { AlertTriangle, Building2, Search, Users } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { CustomerAddPanel } from "@/components/customer-add-panel";

// UC-ACC-001 §5.9 read-only view: sales team + accounting can see the full
// customer master with A/R exposure at a glance. Feeds into: (1) demo showing
// "hệ thống biết công nợ khách nào cao nhất", (2) sales rep quick-check
// before offering credit terms.

interface CustomerRecord {
  id: string;
  customerCode: string;
  name: string;
  mst: string | null;
  address: string | null;
  phone: string | null;
  email: string | null;
  category: string;
  creditLimit: number;
  creditTermsDays: number;
  currentAr: number;
  status: string;
}

interface CustomersListResponse {
  items: CustomerRecord[];
  total: number;
}

const CATEGORY_LABEL: Record<string, string> = {
  phong_kham: "Phòng khám",
  benh_vien: "Bệnh viện",
  dai_ly: "Đại lý",
  truong_hoc: "Trường Y-Nha",
};

const CATEGORY_BADGE: Record<string, string> = {
  phong_kham: "bg-blue-50 text-blue-700 border-blue-200",
  benh_vien: "bg-purple-50 text-purple-700 border-purple-200",
  dai_ly: "bg-amber-50 text-amber-700 border-amber-200",
  truong_hoc: "bg-emerald-50 text-emerald-700 border-emerald-200",
};

function fmtMoney(n: number): string {
  return n.toLocaleString("vi-VN");
}

function creditUsedPct(currentAr: number, creditLimit: number): number {
  if (creditLimit <= 0) return 0;
  return Math.round((currentAr / creditLimit) * 100);
}

function creditUsedTone(pct: number): string {
  if (pct >= 100) return "text-red-700 font-bold";
  if (pct >= 80) return "text-amber-700 font-bold";
  if (pct >= 50) return "text-amber-600";
  return "text-emerald-700";
}

interface PageProps {
  searchParams: Promise<{ category?: string; q?: string }>;
}

export default async function CustomersPage({ searchParams }: PageProps) {
  const { category, q } = await searchParams;
  const token = await requireSession();
  const res = await gateway.listCustomers(token, { category, q });
  const body = res.ok
    ? ((await res.json()) as CustomersListResponse)
    : { items: [], total: 0 };

  // Aggregate stats for the summary strip.
  const totalAr = body.items.reduce((sum, c) => sum + c.currentAr, 0);
  const overLimit = body.items.filter(
    (c) => c.creditLimit > 0 && c.currentAr > c.creditLimit,
  ).length;
  const near80 = body.items.filter((c) => {
    const p = creditUsedPct(c.currentAr, c.creditLimit);
    return p >= 80 && p < 100;
  }).length;

  const categories: Array<{ value: string | null; label: string }> = [
    { value: null, label: "Tất cả" },
    { value: "phong_kham", label: "Phòng khám" },
    { value: "benh_vien", label: "Bệnh viện" },
    { value: "dai_ly", label: "Đại lý" },
  ];

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <h1 className="font-serif text-3xl">
            <Users className="mr-2 inline size-6" /> Khách hàng
          </h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Danh mục khách hàng + công nợ hiện hành. Data đọc từ hệ thống master (giai
            đoạn pilot: simulator, giai đoạn triển khai: MCP sync từ ERP thật).
          </p>
        </div>
        <CustomerAddPanel />
      </header>

      <section className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Tổng công nợ hiện tại</div>
          <div className="mt-1 font-mono text-2xl font-bold">{fmtMoney(totalAr)} <span className="text-sm font-normal">VNĐ</span></div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            trên {body.items.length} khách hàng
          </div>
        </div>
        <div className={`rounded-[var(--radius)] border p-4 ${overLimit > 0 ? "border-red-300 bg-red-50" : ""}`}>
          <div className="text-xs text-[var(--color-muted-fg)]">Vượt hạn mức tín dụng</div>
          <div className={`mt-1 font-mono text-2xl font-bold ${overLimit > 0 ? "text-red-700" : ""}`}>
            {overLimit}
          </div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            {overLimit > 0 ? "Cần review trước khi bán chịu tiếp" : "Không có khách quá hạn mức"}
          </div>
        </div>
        <div className={`rounded-[var(--radius)] border p-4 ${near80 > 0 ? "border-amber-300 bg-amber-50" : ""}`}>
          <div className="text-xs text-[var(--color-muted-fg)]">Sử dụng ≥ 80% hạn mức</div>
          <div className={`mt-1 font-mono text-2xl font-bold ${near80 > 0 ? "text-amber-700" : ""}`}>{near80}</div>
          <div className="text-xs text-[var(--color-muted-fg)]">Cảnh báo — sát trần</div>
        </div>
      </section>

      <div className="flex flex-wrap items-center gap-2">
        {categories.map((c) => {
          const active = (category ?? null) === (c.value ?? null);
          const href = c.value
            ? `/customers?category=${c.value}${q ? `&q=${encodeURIComponent(q)}` : ""}`
            : q
              ? `/customers?q=${encodeURIComponent(q)}`
              : "/customers";
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
        <form className="ml-auto flex items-center gap-2" action="/customers">
          {category && <input type="hidden" name="category" value={category} />}
          <div className="relative">
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-muted-fg)]" />
            <input
              name="q"
              defaultValue={q ?? ""}
              placeholder="Tìm tên / MST / mã KH"
              className="rounded-[var(--radius)] border py-1.5 pl-7 pr-2 text-xs"
            />
          </div>
        </form>
      </div>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center text-sm text-[var(--color-muted-fg)]">
          Không tìm thấy khách hàng nào.
        </div>
      ) : (
        <div className="overflow-x-auto rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Mã KH</th>
                <th className="px-3 py-2 text-left">Tên khách hàng</th>
                <th className="px-3 py-2 text-left">MST</th>
                <th className="px-3 py-2 text-left">Loại</th>
                <th className="px-3 py-2 text-right">Công nợ hiện tại</th>
                <th className="px-3 py-2 text-right">Hạn mức</th>
                <th className="px-3 py-2 text-center">Dùng %</th>
                <th className="px-3 py-2 text-center">Điều khoản TT</th>
              </tr>
            </thead>
            <tbody>
              {body.items.map((c) => {
                const pct = creditUsedPct(c.currentAr, c.creditLimit);
                const overLimit = c.creditLimit > 0 && c.currentAr > c.creditLimit;
                return (
                  <tr key={c.id} className="border-t hover:bg-[var(--color-muted)]/30">
                    <td className="px-3 py-2 font-mono text-xs">
                      <Link href={`/customers/${c.id}`} className="hover:underline">{c.customerCode}</Link>
                    </td>
                    <td className="px-3 py-2">
                      <Link href={`/customers/${c.id}`} className="font-medium hover:underline">
                        <Building2 className="mr-1 inline size-3.5 text-[var(--color-muted-fg)]" />
                        {c.name}
                      </Link>
                      {c.status === "inactive" && (
                        <span className="ml-2 rounded-full border px-1.5 py-0.5 text-[10px] text-[var(--color-muted-fg)]">Ngừng HĐ</span>
                      )}
                      {c.address && (
                        <div className="mt-0.5 text-[10px] text-[var(--color-muted-fg)]">
                          {c.address}
                        </div>
                      )}
                    </td>
                    <td className="px-3 py-2 font-mono text-xs">{c.mst ?? "—"}</td>
                    <td className="px-3 py-2">
                      <span
                        className={`inline-block rounded-full border px-2 py-0.5 text-[10px] ${
                          CATEGORY_BADGE[c.category] ?? "border-gray-200"
                        }`}
                      >
                        {CATEGORY_LABEL[c.category] ?? c.category}
                      </span>
                    </td>
                    <td className="px-3 py-2 text-right font-mono">{fmtMoney(c.currentAr)}</td>
                    <td className="px-3 py-2 text-right font-mono text-[var(--color-muted-fg)]">
                      {fmtMoney(c.creditLimit)}
                    </td>
                    <td className={`px-3 py-2 text-center font-mono ${creditUsedTone(pct)}`}>
                      {pct}%
                      {overLimit && <AlertTriangle className="ml-1 inline size-3" />}
                    </td>
                    <td className="px-3 py-2 text-center text-xs">Net {c.creditTermsDays}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-[11px] italic text-[var(--color-muted-fg)]">
        <b>Lưu ý pilot:</b> data khách hàng và công nợ hiện tại là simulator để demo
        flow. Khi triển khai thật, sẽ kéo trực tiếp từ ERP (Misa/Fast/Bravo/SAP) qua
        MCP adapter — cấu trúc dữ liệu giữ nguyên, chỉ đổi nguồn.
      </p>
    </div>
  );
}
