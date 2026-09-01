import Link from "next/link";
import { BookOpen, GraduationCap, PlayCircle } from "lucide-react";
import { ARTIFACTS, type VoucherTypeCode } from "@/data/artifacts-manifest";
import { requireSession } from "@/lib/auth";

// Sprint 4.5 — Artifact Library / Training center.
// Kế toán viên xem 10 mẫu chứng từ chuẩn TT200 với 3-step walkthrough
// interactive để học: nhận diện loại, fields extract, bút toán suggest.

const TYPE_BADGE: Record<VoucherTypeCode, string> = {
  invoice_vat_out: "bg-emerald-50 text-emerald-700 border-emerald-200",
  invoice_vat_in: "bg-blue-50 text-blue-700 border-blue-200",
  receipt: "bg-purple-50 text-purple-700 border-purple-200",
  payment: "bg-amber-50 text-amber-700 border-amber-200",
  bank_transfer: "bg-cyan-50 text-cyan-700 border-cyan-200",
  expense_claim: "bg-pink-50 text-pink-700 border-pink-200",
};

const TYPE_LABEL_SHORT: Record<VoucherTypeCode, string> = {
  invoice_vat_out: "HĐ bán",
  invoice_vat_in: "HĐ mua",
  receipt: "Phiếu thu",
  payment: "Phiếu chi TM",
  bank_transfer: "UNC / TT NH",
  expense_claim: "Hoàn ứng NV",
};

function fmtMoney(n: number): string {
  return n.toLocaleString("vi-VN");
}

export default async function ArtifactLibraryPage() {
  await requireSession();

  // Group by voucher_type for section headers.
  const grouped = ARTIFACTS.reduce<Record<string, typeof ARTIFACTS>>((acc, a) => {
    (acc[a.voucherType] ||= []).push(a);
    return acc;
  }, {});
  const orderedTypes: VoucherTypeCode[] = [
    "invoice_vat_out",
    "invoice_vat_in",
    "receipt",
    "bank_transfer",
    "payment",
    "expense_claim",
  ];

  return (
    <div className="space-y-8">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">
          <GraduationCap className="mr-2 inline size-7 text-[var(--color-primary)]" /> Chứng từ mẫu ·
          Training center
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          10 mẫu chứng từ chuẩn TT200 Việt Đăng dùng cho onboarding kế toán viên mới + demo hệ
          thống. Mỗi mẫu có 3-step walkthrough interactive: <b>Nhận diện</b> loại chứng từ →{" "}
          <b>Fields</b> LLM extract → <b>Bút toán</b> chuẩn TT200.
        </p>
      </header>

      <section className="grid gap-3 md:grid-cols-3">
        <div className="rounded-[var(--radius)] border bg-blue-50/50 p-4">
          <div className="text-xs text-blue-800">Tổng chứng từ mẫu</div>
          <div className="mt-1 font-mono text-2xl font-bold text-blue-900">{ARTIFACTS.length}</div>
          <div className="text-xs text-blue-700">phủ 6 loại voucher_type khác nhau</div>
        </div>
        <div className="rounded-[var(--radius)] border bg-emerald-50/50 p-4">
          <div className="text-xs text-emerald-800">Bước học mỗi mẫu</div>
          <div className="mt-1 font-mono text-2xl font-bold text-emerald-900">3 bước</div>
          <div className="text-xs text-emerald-700">Nhận diện → Extract → Bút toán</div>
        </div>
        <div className="rounded-[var(--radius)] border bg-amber-50/50 p-4">
          <div className="text-xs text-amber-800">Thời gian hoàn thành</div>
          <div className="mt-1 font-mono text-2xl font-bold text-amber-900">~30 phút</div>
          <div className="text-xs text-amber-700">Xem cả 10 mẫu + trả quiz</div>
        </div>
      </section>

      {orderedTypes.map((type) => {
        const items = grouped[type];
        if (!items || items.length === 0) return null;
        return (
          <section key={type} className="space-y-3">
            <h2 className="font-serif text-xl">
              <span
                className={`mr-2 inline-block rounded-full border px-3 py-0.5 text-xs ${TYPE_BADGE[type]}`}
              >
                {TYPE_LABEL_SHORT[type]}
              </span>
              {items.length} mẫu
            </h2>
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
              {items.map((a) => (
                <Link
                  key={a.id}
                  href={`/mau-chung-tu/${a.id}`}
                  className="group overflow-hidden rounded-[var(--radius)] border transition hover:border-[var(--color-primary)] hover:shadow-lg"
                >
                  <div className="relative aspect-[3/4] overflow-hidden bg-[var(--color-muted)]">
                    <img
                      src={`/demo-templates/${a.filename}`}
                      alt={a.title}
                      className="h-full w-full object-cover object-top transition group-hover:scale-105"
                      style={{ imageRendering: "auto" }}
                    />
                    <div className="absolute inset-0 flex items-center justify-center bg-black/0 opacity-0 transition group-hover:bg-black/40 group-hover:opacity-100">
                      <div className="flex items-center gap-2 rounded-full bg-white/95 px-4 py-2 text-sm font-medium text-[var(--color-primary)] shadow-lg">
                        <PlayCircle className="size-4" /> Bắt đầu học
                      </div>
                    </div>
                  </div>
                  <div className="space-y-2 p-3">
                    <div className="flex items-start justify-between gap-2">
                      <h3 className="font-serif text-sm font-medium leading-tight">{a.title}</h3>
                      <span className="shrink-0 font-mono text-[10px] text-[var(--color-muted-fg)]">
                        #{a.id.split("-")[0]}
                      </span>
                    </div>
                    <p className="line-clamp-2 text-xs text-[var(--color-muted-fg)]">
                      {a.description}
                    </p>
                    <div className="flex items-center justify-between border-t pt-2 text-[11px]">
                      <span className="font-mono text-[var(--color-muted-fg)]">
                        {fmtMoney(a.amount)} {a.currency}
                      </span>
                      {a.riskFlags.length > 0 && (
                        <span className="rounded-full bg-red-50 px-2 py-0.5 text-red-700">
                          {a.riskFlags.length} rủi ro
                        </span>
                      )}
                    </div>
                  </div>
                </Link>
              ))}
            </div>
          </section>
        );
      })}

      <div className="rounded-[var(--radius)] border border-dashed p-5 text-sm">
        <BookOpen className="mr-2 inline size-4 text-[var(--color-muted-fg)]" />
        <b>Mẹo học:</b> Bắt đầu từ <Link href="/mau-chung-tu/01-hoa-don-vat-nha-xinh-30tr" className="text-[var(--color-primary)] underline">mẫu 01 (HĐ bán cơ bản)</Link> → làm tuần tự tới mẫu 10 để hiểu progression từ AR đơn giản (bán chịu → thu tiền) → AP phức tạp (mua nhập khẩu, hoàn ứng, phát hiện vi phạm TT78).
      </div>
    </div>
  );
}
