import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ArrowLeft, Building2 } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { BankAccountsSection, type BankAccountView } from "@/components/bank-accounts-section";
import { CustomerForm } from "@/components/customer-form";
import { CustomerLifecycleActions } from "@/components/customer-lifecycle-actions";

interface OpenArInvoice {
  id: string;
  documentNumber: string;
  documentDate: string;
  dueDate: string | null;
  debit: number;
  paidAmount: number;
  status: string;
  daysOverdue: number;
  contractId: string | null;
}

interface CustomerReceivablesReport {
  openInvoices: OpenArInvoice[];
  overdueAmount: number;
}

interface CustomerRecord {
  id: string;
  customerCode: string;
  name: string;
  mst: string | null;
  address: string | null;
  phone: string | null;
  email: string | null;
  category: string | null;
  customerType: string;
  representative: string | null;
  status: string;
  notes: string | null;
  creditLimit: number;
  creditTermsDays: number;
  currentAr: number;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
}

interface Props {
  params: Promise<{ id: string }>;
}

export default async function CustomerDetailPage({ params }: Props) {
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

  const res = await gateway.getCustomer(token, id);
  if (res.status === 404) notFound();
  if (!res.ok) redirect("/customers");
  const customer = (await res.json()) as CustomerRecord;

  const bankAccountsRes = await gateway.listBankAccounts(token, "customers", id);
  const bankAccounts = bankAccountsRes.ok ? ((await bankAccountsRes.json()) as BankAccountView[]) : [];
  const receivablesRes = await gateway.customerReceivables(token, id);
  const receivables = receivablesRes.ok
    ? ((await receivablesRes.json()) as CustomerReceivablesReport)
    : null;

  const canEdit = (isAdmin || customer.createdBy === viewerId) && customer.status === "active";

  return (
    <div className="space-y-6">
      <Link href="/customers" className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:underline">
        <ArrowLeft className="size-3.5" /> Khách hàng
      </Link>

      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-serif text-3xl">
            <Building2 className="mr-2 inline size-6" /> {customer.name}
          </h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Mã KH {customer.customerCode} · MST {customer.mst ?? "—"}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span
            className={`rounded-full border px-2 py-0.5 text-xs ${
              customer.status === "active"
                ? "border-emerald-300 bg-emerald-50 text-emerald-700"
                : "border-gray-300 bg-gray-50 text-gray-600"
            }`}
          >
            {customer.status === "active" ? "Đang hoạt động" : "Ngừng hoạt động"}
          </span>
          {isAdmin && <CustomerLifecycleActions customerId={customer.id} status={customer.status} />}
        </div>
      </header>

      <section className="grid gap-4 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 sm:grid-cols-2">
        <div><span className="text-xs text-[var(--color-muted-fg)]">Loại</span><p>{customer.customerType === "individual" ? "Cá nhân" : "Doanh nghiệp"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Phân loại</span><p>{customer.category ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Người đại diện</span><p>{customer.representative ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Điện thoại</span><p>{customer.phone ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Email</span><p>{customer.email ?? "—"}</p></div>
        <div className="sm:col-span-2"><span className="text-xs text-[var(--color-muted-fg)]">Địa chỉ</span><p>{customer.address ?? "—"}</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Công nợ hiện tại</span><p className="font-mono">{customer.currentAr.toLocaleString("vi-VN")} VNĐ</p></div>
        <div><span className="text-xs text-[var(--color-muted-fg)]">Hạn mức tín dụng</span><p className="font-mono">{customer.creditLimit.toLocaleString("vi-VN")} VNĐ</p></div>
        {customer.notes && <div className="sm:col-span-2"><span className="text-xs text-[var(--color-muted-fg)]">Ghi chú</span><p>{customer.notes}</p></div>}
      </section>

      <BankAccountsSection owner="customers" ownerId={customer.id} ownerName={customer.name} accounts={bankAccounts} canManage={isAdmin} />

      <section className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="mb-3 font-serif text-xl">Công nợ mở (AR)</h2>
        {receivables && receivables.openInvoices.length > 0 ? (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-2 py-1 text-left">Số hoá đơn</th>
                  <th className="px-2 py-1 text-left">Ngày</th>
                  <th className="px-2 py-1 text-left">Hạn TT</th>
                  <th className="px-2 py-1 text-right">Số tiền</th>
                  <th className="px-2 py-1 text-right">Đã thu</th>
                  <th className="px-2 py-1 text-right">Còn lại</th>
                  <th className="px-2 py-1 text-center">Trạng thái</th>
                </tr>
              </thead>
              <tbody>
                {receivables.openInvoices.map((inv) => (
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
                    <td className="px-2 py-1 text-right font-mono">{inv.debit.toLocaleString("vi-VN")}</td>
                    <td className="px-2 py-1 text-right font-mono">{inv.paidAmount.toLocaleString("vi-VN")}</td>
                    <td className="px-2 py-1 text-right font-mono">{(inv.debit - inv.paidAmount).toLocaleString("vi-VN")}</td>
                    <td className="px-2 py-1 text-center">
                      <span className={`rounded-full border px-2 py-0.5 text-[10px] ${
                        inv.status === "partial" ? "border-amber-300 bg-amber-50 text-amber-700" : "border-gray-200"
                      }`}>
                        {inv.status === "partial" ? "Thu một phần" : "Chưa thu"}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="text-sm text-[var(--color-muted-fg)]">Không có công nợ mở.</p>
        )}
      </section>

      {canEdit ? (
        <section className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="mb-3 font-serif text-xl">Sửa thông tin</h2>
          <CustomerForm
            customerId={customer.id}
            initial={{
              name: customer.name,
              mst: customer.mst,
              address: customer.address,
              phone: customer.phone,
              email: customer.email,
              category: customer.category,
              customerType: customer.customerType,
              representative: customer.representative,
              creditLimit: customer.creditLimit,
              creditTermsDays: customer.creditTermsDays,
              notes: customer.notes,
            }}
          />
        </section>
      ) : (
        customer.status === "active" && (
          <p className="text-sm text-[var(--color-muted-fg)]">
            Bạn không phải người tạo khách hàng này nên không thể chỉnh sửa.
          </p>
        )
      )}
    </div>
  );
}
