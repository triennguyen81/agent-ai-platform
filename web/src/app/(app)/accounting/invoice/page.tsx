"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { CheckCircle2, ExternalLink, FileText, Loader2, Trash2, Upload, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/input";
import { SaveToKbButton } from "@/components/save-to-kb-button";
import { AcceptInvoiceButton } from "@/components/accept-invoice-button";
import { AcceptExpenseButton } from "@/components/accept-expense-button";
import { CapabilityError, executeCapability } from "@/lib/capability";
import { isImageFilename, uploadAndExtract } from "@/lib/upload-extract";

// Real posting flow (Duyệt & ghi nhận → AR/AP) — mirrors what
// /vouchers/[id] already does, wired into this "Accounting → Hoá đơn" entry
// point too so the flow works from here instead of dead-ending at
// AI-scratchpad extraction (Trích xuất trường / Đề xuất bút toán / Lưu vào
// KB never persisted a voucher or touched AR/AP at all). Gateway only OCRs
// real image/PDF bytes, so this path requires an uploaded file — pasted
// text alone (the primary input above) has nothing to persist a voucher
// from.
type PostVoucherType = "invoice_vat_out" | "invoice_vat_in";

interface PostedVoucherLite {
  id: string;
  status: string;
  voucherType: string;
  totalAmount?: number | null;
  subtotal?: number | null;
  vatAmount?: number | null;
  currency?: string | null;
  buyerName?: string | null;
  sellerName?: string | null;
  extractedFieldsJson?: string | null;
}

interface PartnerContext {
  status: string;
  name?: string;
}

function readPartnerContext(json: string | null | undefined, key: "ar_context" | "ap_context"): PartnerContext | null {
  if (!json) return null;
  try {
    const parsed = JSON.parse(json) as Record<string, unknown>;
    const ctx = parsed[key] as Record<string, unknown> | undefined;
    if (!ctx) return null;
    const status = String(ctx.status ?? "");
    const partner = (ctx.customer ?? ctx.supplier) as Record<string, unknown> | undefined;
    return { status, name: partner?.name as string | undefined };
  } catch {
    return null;
  }
}

type PostingState =
  | { phase: "idle" }
  | { phase: "uploading" }
  | { phase: "polling"; voucherId: string }
  | { phase: "ready"; voucherId: string; voucher: PostedVoucherLite }
  | { phase: "error"; message: string };

const POST_ACCEPTED = new Set(["image/jpeg", "image/png", "image/webp", "image/bmp", "application/pdf"]);
const POLL_INTERVAL_MS = 2500;
const POLL_MAX_ATTEMPTS = 30;
const PENDING_STATUSES = new Set(["processing", "ocr_complete", "ocr_unavailable"]);

type FileStatus = "idle" | "uploading" | "reading" | "ocr" | "done" | "error";

const ACCEPTED_EXTENSIONS = [".pdf", ".docx", ".png", ".jpg", ".jpeg", ".xml"];

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

const SAMPLE = `Hoá đơn GTGT
Số: 0000123 - Ký hiệu: 1C24TYY
Ngày: 15/11/2025
Người bán: Công ty TNHH Vật tư ABC
MST: 0312345678
Người mua: Công ty TNHH Cát Tường AI
MST: 0399999999

| STT | Tên hàng hoá | ĐVT | SL | Đơn giá | Thành tiền |
|---|---|---|---|---|---|
| 1 | Bút bi xanh | Cái | 50 | 5.000 | 250.000 |
| 2 | Giấy A4 | Ream | 20 | 65.000 | 1.300.000 |
| 3 | Mực in HP | Hộp | 5 | 850.000 | 4.250.000 |

Tổng tiền hàng (chưa VAT): 5.800.000 đ
Thuế suất VAT: 10%
Tiền thuế VAT: 580.000 đ
Tổng tiền thanh toán: 6.380.000 đ

(Bằng chữ: Sáu triệu ba trăm tám mươi nghìn đồng chẵn)
Phương thức thanh toán: Chuyển khoản`;

function fmtMoney(n: number | null | undefined, currency = "VND"): string {
  return n == null ? "—" : `${Number(n).toLocaleString("vi-VN")} ${currency}`;
}

// The 5-point required posting UI (Trạng thái / Chiều hoá đơn / Đối tác /
// Accounting impact preview / Duyệt & ghi nhận), reusing the exact same
// AcceptInvoiceButton/AcceptExpenseButton the voucher detail page uses —
// same idempotent gateway endpoints, same POSTED/UNPAID wording, same
// ADMIN-only gating.
function PostingPanel({
  voucherId,
  voucher,
  postVoucherType,
  isAdmin,
  onApplied,
}: {
  voucherId: string;
  voucher: PostedVoucherLite;
  postVoucherType: PostVoucherType;
  isAdmin: boolean | null;
  onApplied: () => void;
}) {
  const currency = voucher.currency ?? "VND";
  const isSale = postVoucherType === "invoice_vat_out";
  const partner = readPartnerContext(voucher.extractedFieldsJson, isSale ? "ar_context" : "ap_context");
  const partnerName = partner?.name ?? (isSale ? voucher.buyerName : voucher.sellerName) ?? "—";
  const isPosted = voucher.status === "verified";
  const statusLabel = isPosted ? "POSTED" : voucher.status === "extracted" ? "EXTRACTED" : voucher.status.toUpperCase();

  if (voucher.status === "rejected" || voucher.status === "failed") {
    return (
      <div className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        Chứng từ #{voucherId.slice(0, 8)}… xử lý thất bại (trạng thái: {voucher.status}).{" "}
        <Link href={`/vouchers/${voucherId}`} className="underline">Xem chi tiết</Link>
      </div>
    );
  }

  return (
    <section
      className={`space-y-2 rounded-[var(--radius)] border p-4 ${
        isPosted ? "border-emerald-300 bg-emerald-50/40" : isSale ? "border-emerald-200 bg-emerald-50/20" : "border-amber-200 bg-amber-50/20"
      }`}
    >
      <div className="flex items-center justify-between">
        <div className="text-sm font-medium">Trạng thái: {statusLabel}</div>
        <Link
          href={`/vouchers/${voucherId}`}
          className="inline-flex items-center gap-1 text-xs text-[var(--color-primary)] hover:underline"
        >
          Xem chi tiết <ExternalLink className="size-3" />
        </Link>
      </div>
      <div className="text-xs text-[var(--color-muted-fg)]">
        Chiều hoá đơn:{" "}
        <b className={isSale ? "text-emerald-800" : "text-amber-800"}>
          {isSale ? "BÁN → Phải thu (AR)" : "MUA → Phải trả (AP)"}
        </b>
      </div>
      <div className="text-xs text-[var(--color-muted-fg)]">
        Đối tác: <b>{partnerName}</b>
      </div>

      {partner && partner.status !== "found" && (
        <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          {isSale ? "Khách hàng" : "Nhà cung cấp"} chưa có trong hệ thống — không thể ghi
          {isSale ? " AR" : " AP"} cho tới khi tạo hồ sơ {isSale ? "khách hàng" : "NCC"}.
        </p>
      )}

      <div className="space-y-0.5 rounded border bg-[var(--color-bg)] px-2 py-1.5 font-mono text-xs">
        <div>
          {isSale ? "Phải thu" : "Phải trả"} {partnerName}:{" "}
          <b className={isSale ? "text-emerald-800" : "text-amber-800"}>
            +{fmtMoney(voucher.totalAmount, currency)}
          </b>
        </div>
        {voucher.subtotal != null && (
          <div>{isSale ? "Doanh thu" : "Hàng hoá/dịch vụ mua vào"}: {fmtMoney(voucher.subtotal, currency)}</div>
        )}
        {voucher.vatAmount != null && (
          <div>{isSale ? "VAT đầu ra" : "VAT đầu vào"}: {fmtMoney(voucher.vatAmount, currency)}</div>
        )}
      </div>

      {isPosted ? (
        <div className="flex items-center gap-2 rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-3 py-2 text-sm text-emerald-900">
          <CheckCircle2 className="size-4 shrink-0" />
          <div>
            Đã ghi nhận {isSale ? "phải thu" : "phải trả"}: <b>{fmtMoney(voucher.totalAmount, currency)}</b> — Thanh
            toán: <b>CHƯA THANH TOÁN</b>.
          </div>
        </div>
      ) : isAdmin === false ? (
        <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          Chỉ ADMIN cùng phòng ban mới có thể Duyệt &amp; ghi nhận.
        </p>
      ) : isAdmin === null ? (
        <p className="text-xs text-[var(--color-muted-fg)]">Đang kiểm tra quyền…</p>
      ) : partner?.status === "found" || (partner === null && voucher.status === "extracted") ? (
        isSale ? (
          <AcceptInvoiceButton voucherId={voucherId} onApplied={onApplied} />
        ) : (
          <AcceptExpenseButton voucherId={voucherId} voucherType="invoice_vat_in" onApplied={onApplied} />
        )
      ) : (
        <p className="text-xs text-[var(--color-muted-fg)]">Đang chờ trích xuất hoàn tất…</p>
      )}
    </section>
  );
}

export default function InvoicePage() {
  const [rawText, setRawText] = useState(SAMPLE);
  const [extracted, setExtracted] = useState("");
  const [journalSuggest, setJournalSuggest] = useState("");
  const [loading, setLoading] = useState<"extract" | "journal" | null>(null);
  const [error, setError] = useState<string | null>(null);

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [fileStatus, setFileStatus] = useState<FileStatus>("idle");
  const [fileError, setFileError] = useState<string | null>(null);
  const [extractedFileInfo, setExtractedFileInfo] = useState<{
    charCount: number;
    filename: string;
    extractionMethod: string;
  } | null>(null);
  // MinIO key of the already-uploaded source file (voucher-uploads bucket,
  // accounting/invoice-staging/ prefix — see gateway AccountingEndpoints.cs).
  // There is no invoice/voucher record for this page to attach it to (this
  // flow has never persisted anything — see page header comment), so this
  // is kept only as on-page source-of-truth metadata, not sent anywhere
  // downstream today. Editing rawText afterward does NOT clear it; only
  // removing the file ("Xóa file") or picking a different one does.
  const [sourceObjectKey, setSourceObjectKey] = useState<string | null>(null);

  const [postVoucherType, setPostVoucherType] = useState<PostVoucherType>("invoice_vat_out");
  const [posting, setPosting] = useState<PostingState>({ phase: "idle" });
  const [isAdmin, setIsAdmin] = useState<boolean | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch("/api/me")
      .then((res) => (res.ok ? res.json() : null))
      .then((me: { roles?: string[] } | null) => {
        if (!cancelled) setIsAdmin((me?.roles ?? []).includes("admin"));
      })
      .catch(() => {
        if (!cancelled) setIsAdmin(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const pollVoucher = useCallback(async (voucherId: string, attempt: number) => {
    try {
      const res = await fetch(`/api/vouchers/${voucherId}`, { cache: "no-store" });
      if (!res.ok) {
        setPosting({ phase: "error", message: `Không tải được chứng từ (HTTP ${res.status})` });
        return;
      }
      const detail = (await res.json()) as { voucher: PostedVoucherLite };
      const status = detail.voucher.status;
      if (PENDING_STATUSES.has(status) && attempt < POLL_MAX_ATTEMPTS) {
        setPosting({ phase: "polling", voucherId });
        setTimeout(() => void pollVoucher(voucherId, attempt + 1), POLL_INTERVAL_MS);
        return;
      }
      setPosting({ phase: "ready", voucherId, voucher: detail.voucher });
    } catch (err) {
      setPosting({
        phase: "error",
        message: err instanceof Error ? err.message : "Network error khi kiểm tra chứng từ",
      });
    }
  }, []);

  const postToLedger = useCallback(async () => {
    if (!file) {
      setPosting({ phase: "error", message: "Cần chọn file ảnh/PDF hoá đơn — không dùng được với văn bản dán tay." });
      return;
    }
    if (!POST_ACCEPTED.has(file.type)) {
      setPosting({
        phase: "error",
        message: "Ghi sổ thật chỉ nhận ảnh (JPG/PNG/WebP/BMP) hoặc PDF — không nhận Word/XML.",
      });
      return;
    }
    setPosting({ phase: "uploading" });
    try {
      const fd = new FormData();
      fd.append("file", file, file.name);
      const res = await fetch(`/api/vouchers/upload?voucherType=${postVoucherType}`, {
        method: "POST",
        body: fd,
      });
      if (!res.ok) {
        const text = await res.text();
        setPosting({ phase: "error", message: text.slice(0, 300) || `HTTP ${res.status}` });
        return;
      }
      const body = (await res.json()) as { voucherId: string };
      setPosting({ phase: "polling", voucherId: body.voucherId });
      void pollVoucher(body.voucherId, 0);
    } catch (err) {
      setPosting({ phase: "error", message: err instanceof Error ? err.message : "Network error" });
    }
  }, [file, postVoucherType, pollVoucher]);

  function pickFile() {
    fileInputRef.current?.click();
  }

  function onFileSelected(e: React.ChangeEvent<HTMLInputElement>) {
    const picked = e.target.files?.[0] ?? null;
    e.target.value = ""; // allow re-selecting the same file later
    if (!picked) return;
    setFile(picked);
    setFileStatus("idle");
    setFileError(null);
    setExtractedFileInfo(null);
    setSourceObjectKey(null);
    setPosting({ phase: "idle" });
  }

  function clearFile() {
    setFile(null);
    setFileStatus("idle");
    setFileError(null);
    setExtractedFileInfo(null);
    setSourceObjectKey(null);
    setPosting({ phase: "idle" });
    // rawText is deliberately left untouched — manual input stays available.
  }

  const extractFromFile = useCallback(async () => {
    if (!file || fileStatus === "uploading" || fileStatus === "reading" || fileStatus === "ocr") return;
    setFileStatus("uploading");
    setFileError(null);

    const formData = new FormData();
    formData.append("file", file);

    const outcome = await uploadAndExtract("/api/accounting/invoice/extract-text", formData, () => {
      setFileStatus(isImageFilename(file.name) ? "ocr" : "reading");
    });

    if (!outcome.ok) {
      setFileStatus("error");
      setFileError(
        outcome.status === 415
          ? "Định dạng file không được hỗ trợ."
          : outcome.status === 422
            ? outcome.detail || "Không trích xuất được nội dung từ file này."
            : outcome.status === 413
              ? "File quá lớn."
              : `${outcome.detail} (HTTP ${outcome.status || "network"})`,
      );
      return; // rawText (any manual edits) is preserved — never touched here.
    }

    setRawText(outcome.body.text);
    setExtractedFileInfo({
      charCount: outcome.body.charCount,
      filename: outcome.body.originalFilename,
      extractionMethod: outcome.body.extractionMethod,
    });
    setSourceObjectKey(outcome.body.sourceObjectKey);
    setFileStatus("done");
  }, [file, fileStatus]);

  const fileBusy = fileStatus === "uploading" || fileStatus === "reading" || fileStatus === "ocr";
  const fileStatusLabel: Record<FileStatus, string> = {
    idle: "",
    uploading: "Đang tải...",
    reading: "Đang đọc nội dung...",
    ocr: "OCR...",
    done: "Hoàn tất",
    error: "Lỗi",
  };

  // D6: converted from the legacy no-agent-id /agent/run call to the
  // canonical accounting.invoice capability.
  async function callAgent(task: string): Promise<string> {
    try {
      return await executeCapability("accounting.invoice", task, 4);
    } catch (err) {
      throw err instanceof CapabilityError ? new Error(err.message) : err;
    }
  }

  async function extract() {
    setLoading("extract");
    setError(null);
    try {
      const task = `Đây là nội dung hoá đơn:

---
${rawText}
---

Trích xuất các trường sau dạng bảng Markdown:
- Số hoá đơn / ký hiệu
- Ngày hoá đơn
- Người bán + MST
- Người mua + MST
- Tổng tiền chưa VAT
- Thuế suất VAT
- Tiền VAT
- Tổng thanh toán
- Phương thức thanh toán

Sau bảng trích xuất, đề xuất loại hàng (hàng hoá / NVL / CCDC / TSCĐ / chi phí) để định khoản đúng TK.`;
      setExtracted(await callAgent(task));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(null);
    }
  }

  async function buildJournal() {
    setLoading("journal");
    setError(null);
    try {
      const task = `Đây là nội dung hoá đơn:

---
${rawText}
---

Đề xuất bút toán định khoản chuẩn TT200. Áp dụng skill journal-entry-formatter:
- Format 2-3 dòng riêng cho mỗi vế Nợ / Có
- Tách dòng VAT (TK 133 đầu vào) bắt buộc
- Chọn TK chính phù hợp: 156 nếu hàng hoá để bán, 152 nếu NVL sản xuất, 153 nếu CCDC, 211 nếu TSCĐ, 642 nếu chi phí QLDN, 641 nếu chi phí bán hàng
- Nếu thanh toán chuyển khoản → Có TK 112; nếu chưa thanh toán → Có TK 331

Trả về bút toán đầy đủ kèm cơ sở TT200.`;
      setJournalSuggest(await callAgent(task));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(null);
    }
  }

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Hoá đơn → định khoản</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Tải lên file PDF / Word / ảnh / XML để tự động trích xuất nội dung,
          hoặc paste trực tiếp bên dưới → AI trích xuất các trường + đề xuất
          bút toán chuẩn TT200.
        </p>
      </header>

      <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <div className="space-y-2 rounded-[var(--radius)] border border-dashed border-[var(--color-border)] bg-[var(--color-muted)]/30 p-4">
          <input
            ref={fileInputRef}
            type="file"
            accept={ACCEPTED_EXTENSIONS.join(",")}
            onChange={onFileSelected}
            className="hidden"
          />
          {!file ? (
            <Button type="button" variant="outline" onClick={pickFile} className="w-full">
              <Upload className="size-4" /> Chọn file PDF / Word / ảnh / XML
            </Button>
          ) : (
            <div className="space-y-2">
              <div className="flex items-center gap-2 text-sm">
                <FileText className="size-4 shrink-0 text-[var(--color-primary)]" />
                <span className="min-w-0 flex-1 truncate">
                  <span className="font-medium">{file.name}</span>{" "}
                  <span className="text-[var(--color-muted-fg)]">({formatFileSize(file.size)})</span>
                </span>
              </div>

              {fileStatusLabel[fileStatus] && (
                <p
                  className={`text-xs ${
                    fileStatus === "error"
                      ? "text-red-700"
                      : fileStatus === "done"
                        ? "text-emerald-700"
                        : "text-[var(--color-muted-fg)]"
                  }`}
                >
                  {fileBusy && <Loader2 className="mr-1 inline size-3 animate-spin" />}
                  Status: {fileStatusLabel[fileStatus]}
                </p>
              )}

              {fileStatus === "done" && extractedFileInfo && (
                <p className="text-xs text-emerald-700">
                  Đã trích xuất {extractedFileInfo.charCount.toLocaleString("vi-VN")} ký tự từ{" "}
                  {extractedFileInfo.filename}
                </p>
              )}
              {fileStatus === "error" && fileError && (
                <p className="text-xs text-red-700">{fileError}</p>
              )}

              <div className="flex flex-wrap gap-2">
                <Button type="button" size="sm" onClick={() => void extractFromFile()} disabled={fileBusy}>
                  {fileBusy ? <Loader2 className="size-3.5 animate-spin" /> : <Wand2 className="size-3.5" />}
                  {fileStatus === "error" ? "Thử lại" : "Trích xuất nội dung"}
                </Button>
                <Button type="button" size="sm" variant="outline" onClick={pickFile} disabled={fileBusy}>
                  Chọn file khác
                </Button>
                <Button type="button" size="sm" variant="ghost" onClick={clearFile} disabled={fileBusy}>
                  <Trash2 className="size-3.5" /> Xóa file
                </Button>
              </div>

              {/* Real posting flow — ghi một chứng từ thật (voucher) vào hệ
                  thống rồi mở ra Duyệt & ghi nhận, khác hẳn "Trích xuất
                  trường"/"Đề xuất bút toán" phía dưới (chỉ chạy AI, không
                  lưu gì). Chỉ nhận ảnh/PDF — giống /vouchers/upload. */}
              <div className="mt-2 space-y-2 border-t pt-3">
                <div className="flex flex-wrap items-center gap-2">
                  <label className="text-xs font-medium text-[var(--color-muted-fg)]">Chiều hoá đơn:</label>
                  <select
                    value={postVoucherType}
                    onChange={(e) => setPostVoucherType(e.target.value as PostVoucherType)}
                    disabled={posting.phase === "uploading" || posting.phase === "polling"}
                    className="rounded-[var(--radius)] border px-2 py-1 text-xs"
                  >
                    <option value="invoice_vat_out">BÁN → Phải thu (AR)</option>
                    <option value="invoice_vat_in">MUA → Phải trả (AP)</option>
                  </select>
                  <Button
                    type="button"
                    size="sm"
                    onClick={() => void postToLedger()}
                    disabled={posting.phase === "uploading" || posting.phase === "polling"}
                  >
                    {posting.phase === "uploading" || posting.phase === "polling" ? (
                      <Loader2 className="size-3.5 animate-spin" />
                    ) : (
                      <Upload className="size-3.5" />
                    )}
                    Ghi vào hệ thống &amp; duyệt
                  </Button>
                </div>
                <p className="text-[10px] text-[var(--color-muted-fg)]">
                  Tạo chứng từ thật trong hệ thống (OCR + trích xuất chạy nền), sau đó cho phép
                  ADMIN cùng phòng ban Duyệt &amp; ghi nhận vào AR/AP — khác với "Trích xuất
                  trường"/"Đề xuất bút toán" bên dưới (chỉ để tham khảo, không lưu).
                </p>
              </div>
            </div>
          )}
        </div>

        {posting.phase === "error" && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
            Lỗi: {posting.message}
          </p>
        )}
        {(posting.phase === "uploading" || posting.phase === "polling") && (
          <p className="flex items-center gap-2 rounded-[var(--radius)] border border-blue-200 bg-blue-50 px-3 py-2 text-xs text-blue-800">
            <Loader2 className="size-3.5 animate-spin" />
            {posting.phase === "uploading"
              ? "Đang tải chứng từ lên hệ thống…"
              : "OCR + trích xuất đang chạy nền — tự kiểm tra lại mỗi vài giây…"}
          </p>
        )}
        {posting.phase === "ready" && (
          <PostingPanel
            voucherId={posting.voucherId}
            voucher={posting.voucher}
            postVoucherType={postVoucherType}
            isAdmin={isAdmin}
            onApplied={() => void pollVoucher(posting.voucherId, 0)}
          />
        )}

        <label className="text-sm font-medium">Nội dung hoá đơn</label>
        <Textarea
          rows={14}
          value={rawText}
          onChange={(e) => setRawText(e.target.value)}
          className="font-mono text-xs"
        />
        <p className="text-[10px] text-[var(--color-muted-fg)]">
          {rawText.length.toLocaleString("vi-VN")} ký tự
          {sourceObjectKey && " · nguồn: file đã tải lên"} — có thể sửa trực
          tiếp trước khi trích xuất.
        </p>
        <div className="flex gap-2">
          <Button onClick={extract} disabled={loading !== null || !rawText}>
            {loading === "extract" ? (
              <Loader2 className="size-4 animate-spin" />
            ) : (
              <Wand2 className="size-4" />
            )}
            Trích xuất trường
          </Button>
          <Button variant="outline" onClick={buildJournal} disabled={loading !== null || !rawText}>
            {loading === "journal" ? (
              <Loader2 className="size-4 animate-spin" />
            ) : (
              <Wand2 className="size-4" />
            )}
            Đề xuất bút toán
          </Button>
        </div>
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </p>
        )}
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
          <div className="flex items-center justify-between">
            <p className="font-medium">Trích xuất trường</p>
            {extracted && (
              <SaveToKbButton
                defaultTitle="Trích xuất hoá đơn"
                defaultContent={`# Trích xuất hoá đơn\n\n_Nguồn: input text_\n\n${extracted}\n\n---\n\n## Nội dung gốc\n\n\`\`\`\n${rawText}\n\`\`\``}
                defaultTags="kế toán, hoá đơn, trích xuất"
                label="Lưu vào KB"
              />
            )}
          </div>
          {extracted ? (
            <div className="prose prose-sm max-w-none [&_table]:my-2 [&_table]:w-full [&_th]:border [&_th]:bg-[var(--color-bg)] [&_th]:px-2 [&_th]:py-1 [&_td]:border [&_td]:px-2 [&_td]:py-1">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{extracted}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Chưa có dữ liệu — bấm <strong>Trích xuất trường</strong>.
            </p>
          )}
        </div>

        <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
          <div className="flex items-center justify-between">
            <p className="font-medium">Bút toán đề xuất</p>
            {journalSuggest && (
              <SaveToKbButton
                defaultTitle="Bút toán cho hoá đơn"
                defaultContent={`# Bút toán từ hoá đơn\n\n${journalSuggest}`}
                defaultTags="kế toán, định khoản, hoá đơn, bút toán"
                label="Lưu vào KB"
              />
            )}
          </div>
          {journalSuggest ? (
            <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{journalSuggest}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Chưa có dữ liệu — bấm <strong>Đề xuất bút toán</strong>.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
