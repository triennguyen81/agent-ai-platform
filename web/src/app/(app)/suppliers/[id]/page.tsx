import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ArrowLeft, Building2 } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { BankAccountsSection, type BankAccountView } from "@/components/bank-accounts-section";
import { SupplierForm } from "@/components/supplier-form";
import { SupplierLifecycleActions } from "@/components/supplier-lifecycle-actions";

interface OpenApInvoice {
  id: string;
  documentNumber: string;
  documentDate: string;
  dueDate: string | null;
  credit: number;
  paidAmount: number;
  status: string;
  daysOverdue: number;
  contractId: string | null;
}

interface SupplierPayablesReport {
  openInvoices: OpenApInvoice[];
  overdueAmount: number;
}

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
  supplierType: string;
  representative: string | null;
  phone: string | null;
  email: string | null;
  address: string | null;
  status: string;
  notes: string | null;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
}

interface Props {
  params: Promise<{ id: string }>;
}

export default async function SupplierDetailPage({ params }: Props) {
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

  const res = await gateway.getSupplier(token, id);
  if (res.status === 404) notFound();
  if (!res.ok) redirect("/suppliers");
  const supplier = (await res.json()) as SupplierRecord;

  const bankAccountsRes = await gateway.listBankAccounts(token, "suppliers", id);
  const bankAccounts = bankAccountsRes.ok ? ((await bankAccountsRes.json()) as BankAccountView[]) : [];
  const payablesRes = await gateway.supplierPayables(token, id);
  const payables = payablesRes.ok
    ? ((await payablesRes.json()) as SupplierPayablesReport)
    : null;

  const canEdit = (isAdmin || supplier.createdBy === viewerId) && supplier.status === "active";

  return (
    <div className="space-y-6">
      <Link href="/suppliers" className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:underline">
        <ArrowLeft className="size-3.5" /> Nhà cung cấp
      </Link>

      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-serif text-3xl">
            <Building2 className="mr-2 inline size-6" /> {supplier.name}
          </h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Mã NCC {supplier.supplierCode} · MST {supplier.mst ?? "—"}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span
            className={`rounded-full border px-2 py-0.5 text-xs ${
              supplier.status === "active"
                ? "border-emerald-300 bg-emerald-50 text-emerald-700"
                : "border-gray-300 bg-gray-50 text-gray-600"
            }`}
          >
            {supplier.status === "active" ? "Đang hợp tác" : "Ngừng hợp tác"}
          </span>
          {isAdmin && <SupplierLifecycleActions supplierId={supplier.id} status={supplier.status} />}
        </div>
      </header>

      <section className="grid gap-4 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 sm:grid-cols-2">
        <div><span className="text-xs text-[var(--color-muted-fg)]">Loại</span><p>{supplier.supplierType === "individual" ? "Cá nhân" : "Doanh nghiệp"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Người đại diện</span><p>{supplier.representative ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Điện thoại</span><p>{supplier.phone ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Email</span><p>{supplier.email ?? "—"}</p></div>
        <div className="sm:col-span-2"><span className="text-xs text-[var(--color-muted-fg)]">Địa chỉ</span><p>{supplier.address ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Công nợ phải trả (331)</span><p className="font-mono">{supplier.currentAp.toLocaleString("vi-VN")} {supplier.currency}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Điều khoản thanh toán</span><p>{supplier.paymentTerms ?? (supplier.paymentTermsDays > 0 ? `Net ${supplier.paymentTermsDays}` : "—")}</p></div>
        {supplier.notes && <div className="sm:col-span-2"><span className="text-xs text-[var(--color-muted-fg)]">Ghi chú</span><p>{supplier.notes}</p></div>}
      </section>

      <BankAccountsSection owner="suppliers" ownerId={supplier.id} ownerName={supplier.name} accounts={bankAccounts} canManage={isAdmin} />

      <section className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="mb-3 font-serif text-xl">Công nợ phải trả mở (AP)</h2>
        {payables && payables.openInvoices.length > 0 ? (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-2 py-1 text-left">Số hoá đơn</th>
                  <th className="px-2 py-1 text-left">Ngày</th>
                  <th className="px-2 py-1 text-left">Hạn TT</th>
                  <th className="px-2 py-1 text-right">Số tiền</th>
                  <th className="px-2 py-1 text-right">Đã trả</th>
                  <th className="px-2 py-1 text-right">Còn lại</th>
                  <th className="px-2 py-1 text-center">Trạng thái</th>
                </tr>
              </thead>
              <tbody>
                {payables.openInvoices.map((inv) => (
                  <tr key={inv.id} className="border-t">
                    <td className="px-2 py-1 font-mono text-xs">
                      {inv.contractId ? (
                        <Link href={`/contracts/${inv.contractId}`} className="hover:underline">{inv.documentNumber}</Link>
                      ) : inv.documentNumber}
                    </td>
                    <td className="px-2 py-1 text-xs">{inv.documentDate}</td>
                    <td className={`px-2 py-1 text-xs ${inv.daysOverdue > 0 ? "text-red-700" : ""}`}>
                      {inv.dueDate ?? "—"}{inv.daysOverdue > 0 && ` (quá ${inv.daysOverdue} ngày)`}
                    </td>
                    <td className="px-2 py-1 text-right font-mono">{inv.credit.toLocaleString("vi-VN")}</td>
                    <td className="px-2 py-1 text-right font-mono">{inv.paidAmount.toLocaleString("vi-VN")}</td>
                    <td className="px-2 py-1 text-right font-mono">{(inv.credit - inv.paidAmount).toLocaleString("vi-VN")}</td>
                    <td className="px-2 py-1 text-center">
                      <span className={`rounded-full border px-2 py-0.5 text-[10px] ${
                        inv.status === "partial" ? "border-amber-300 bg-amber-50 text-amber-700" : "border-gray-200"
                      }`}>
                        {inv.status === "partial" ? "Trả một phần" : "Chưa trả"}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="text-sm text-[var(--color-muted-fg)]">Không có công nợ phải trả mở.</p>
        )}
      </section>

      {canEdit ? (
        <section className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="mb-3 font-serif text-xl">Sửa thông tin</h2>
          <SupplierForm
            supplierId={supplier.id}
            initial={{
              name: supplier.name,
              mst: supplier.mst,
              address: supplier.address,
              phone: supplier.phone,
              email: supplier.email,
              supplierType: supplier.supplierType,
              representative: supplier.representative,
              paymentTerms: supplier.paymentTerms,
              paymentTermsDays: supplier.paymentTermsDays,
              notes: supplier.notes,
            }}
          />
        </section>
      ) : (
        supplier.status === "active" && (
          <p className="text-sm text-[var(--color-muted-fg)]">
            Bạn không phải người tạo nhà cung cấp này nên không thể chỉnh sửa.
          </p>
        )
      )}
    </div>
  );
}
