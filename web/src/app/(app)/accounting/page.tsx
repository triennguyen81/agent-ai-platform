import Link from "next/link";
import {
  Calculator,
  CalendarClock,
  FileSpreadsheet,
  FileText,
  LineChart,
} from "lucide-react";
import { requireViewer } from "@/lib/authorization";
import { fetchMyCapabilityCodes } from "@/lib/my-capabilities";

interface ToolCard {
  href: string;
  title: string;
  description: string;
  icon: React.ReactNode;
  // D7: all four AI-backed tools now carry their real D4/D6 capability
  // code (D6 wired tax/invoice/reports onto the resolver too — this
  // metadata had gone stale, still saying only "Định khoản" was capability-
  // driven). Deadline has none — it's a plain countdown utility, no AI call.
  capabilityCode?: string;
}

const TOOLS: ToolCard[] = [
  {
    href: "/accounting/tax-calc",
    title: "Calculator thuế",
    description:
      "Tính nhanh VAT (0/5/8/10%), TNCN biểu lũy tiến, TNDN với ưu đãi. Show breakdown từng bậc.",
    icon: <Calculator className="size-6" />,
    capabilityCode: "accounting.tax",
  },
  {
    href: "/accounting/deadline",
    title: "Deadline tờ khai",
    description:
      "Countdown tới hạn nộp 01/GTGT, 02/TNDN, 05/QTT-TNCN, BCTC. Đầy đủ cho năm 2026.",
    icon: <CalendarClock className="size-6" />,
  },
  {
    href: "/accounting/journal",
    title: "Định khoản",
    description:
      "Bảng nhập bút toán: cộng tự động Nợ/Có, validate cân đối, AI gợi ý dòng tiếp theo.",
    icon: <FileText className="size-6" />,
    capabilityCode: "accounting.journal",
  },
  {
    href: "/accounting/invoice",
    title: "Hoá đơn",
    description:
      "Dán nội dung hoá đơn → AI tách giá / VAT / nhà cung cấp → đề xuất định khoản chuẩn.",
    icon: <FileSpreadsheet className="size-6" />,
    capabilityCode: "accounting.invoice",
  },
  {
    href: "/accounting/reports",
    title: "Báo cáo tài chính",
    description:
      "Sinh khung BCKQHĐKD, BCĐKT, LCTT từ dữ liệu Note + KB. Output Markdown export được.",
    icon: <LineChart className="size-6" />,
    capabilityCode: "accounting.report",
  },
];

export default async function AccountingHome() {
  const viewer = await requireViewer(["super_admin", "tenant_owner", "admin", "user"]);
  // D7 §7/§17: same authorized-capability set as the layout's sub-nav —
  // `null` (owner/super_admin) shows every card; ADMIN/USER only see tools
  // they're actually authorized for, so this overview never advertises a
  // dead-end card the Gateway resolver would 403 on click.
  const codes = await fetchMyCapabilityCodes(viewer.token, viewer.roles);
  const visibleTools = codes ? TOOLS.filter((tool) => !tool.capabilityCode || codes.has(tool.capabilityCode)) : TOOLS;

  return (
    <div className="space-y-8">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl tracking-tight">Trợ lý kế toán</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          {visibleTools.length} công cụ chuyên dụng cho kế toán Việt Nam (TT200/2014/TT-BTC). Backed
          by skills + tools đã wire vào agent — kết quả dựa trên VAS hiện hành.
        </p>
      </header>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {visibleTools.map((tool) => (
          <Link
            key={tool.href}
            href={tool.href}
            className="group flex flex-col gap-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 transition hover:border-[var(--color-primary)] hover:shadow-md"
          >
            <div className="flex size-11 items-center justify-center rounded-full bg-[var(--color-primary)]/10 text-[var(--color-primary)]">
              {tool.icon}
            </div>
            <h2 className="font-serif text-lg group-hover:text-[var(--color-primary)]">{tool.title}</h2>
            <p className="text-sm text-[var(--color-muted-fg)]">
              {tool.description}
            </p>
          </Link>
        ))}
      </div>

      <section className="rounded-[var(--radius)] border border-dashed bg-[var(--color-muted)] p-4 text-sm">
        <p className="font-medium text-[var(--color-fg)]">Lưu ý</p>
        <ul className="mt-1 space-y-1 text-[var(--color-muted-fg)]">
          <li>
            • Kết quả là <strong>tham khảo</strong> dựa trên VAS hiện hành — quyết
            định cuối cùng cần kế toán viên xác nhận.
          </li>
          <li>
            • Văn bản pháp luật cập nhật liên tục — kiểm tra Note có ngày hiệu
            lực mới nhất.
          </li>
          <li>
            • Mọi tính toán đi qua tools đã verify: <code>calculate_vat</code>,
            <code> calculate_pit</code>, <code>calculate_cit</code>,
            <code> lookup_account</code>, <code>check_filing_deadline</code>,
            <code> journal_entry_validator</code>,
            <code> import_excel_ledger</code>.
          </li>
        </ul>
      </section>
    </div>
  );
}
