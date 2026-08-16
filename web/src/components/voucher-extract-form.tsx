"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { Loader2, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Label, Textarea } from "@/components/ui/input";

interface VoucherExtractFormProps {
  voucherId: string;
  voucherType: string;
  initialRawText?: string | null;
  /** Voucher.status — decides which panel to show (processing / OCR done /
   *  OCR unavailable). Was previously ignored, so users saw "OCR chưa khả
   *  dụng" + a sample placeholder even while the OCR job was still running.
   */
  voucherStatus?: string;
}

interface ExtractResponse {
  voucherId: string;
  status: string;
  agentResponse?: string;
  error?: string;
}

const SAMPLE_INVOICE_TEXT = `HÓA ĐƠN GIÁ TRỊ GIA TĂNG
Ký hiệu: 1C25TAA
Số: 00001234
Ngày 15 tháng 11 năm 2025

Đơn vị bán hàng: CÔNG TY TNHH THƯƠNG MẠI ABC
Mã số thuế: 0301234567
Địa chỉ: 123 Nguyễn Trãi, Q.1, TP.HCM

Người mua hàng: Nguyễn Văn A
Tên đơn vị: CÔNG TY CỔ PHẦN XYZ
Mã số thuế: 0312345678

STT   Tên hàng           ĐVT   SL   Đơn giá    Thành tiền
 1    Máy in Canon        cái   2   5,000,000  10,000,000

Cộng tiền hàng:              10,000,000
Thuế GTGT (10%):              1,000,000
Tổng cộng tiền thanh toán:   11,000,000
Bằng chữ: Mười một triệu đồng chẵn
Hình thức thanh toán: CK`;

export function VoucherExtractForm({
  voucherId,
  voucherType,
  initialRawText,
  voucherStatus,
}: VoucherExtractFormProps): React.ReactElement {
  const router = useRouter();
  const hasRealOcr = Boolean(initialRawText && initialRawText.trim().length > 0);
  const [rawText, setRawText] = useState(hasRealOcr ? initialRawText! : SAMPLE_INVOICE_TEXT);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState("");
  const [error, setError] = useState<string | null>(null);

  const isProcessing = voucherStatus === "processing" || voucherStatus === "uploading";
  const isOcrReady = voucherStatus === "ocr_complete" && hasRealOcr;
  const isOcrUnavailable = voucherStatus === "ocr_unavailable" || !hasRealOcr;

  async function submit(): Promise<void> {
    if (loading || rawText.trim().length < 20) return;
    setLoading(true);
    setError(null);
    setResult("");

    try {
      const res = await fetch(`/api/vouchers/${voucherId}/extract`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ rawText, voucherType }),
      });
      if (!res.ok) {
        const text = await res.text();
        setError(`HTTP ${res.status}: ${text.slice(0, 300)}`);
        return;
      }
      const body = (await res.json()) as ExtractResponse;
      setResult(body.agentResponse || body.status);
      // Refresh so the server component picks up the freshly-extracted fields.
      setTimeout(() => router.refresh(), 1200);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setLoading(false);
    }
  }

  // Processing panel — no textarea, just a spinner. Prevents user seeing
  // the sample placeholder while the OCR job is still working.
  if (isProcessing) {
    return (
      <section className="space-y-3 rounded-[var(--radius)] border border-blue-200 bg-blue-50/50 p-5">
        <div className="flex items-start gap-2">
          <Loader2 className="mt-0.5 size-4 shrink-0 animate-spin text-blue-700" />
          <div>
            <h2 className="font-serif text-lg text-blue-900">
              Đang xử lý chứng từ…
            </h2>
            <p className="mt-1 text-xs text-blue-800">
              File đã lên S3, Hangfire đang chạy OCR + trích xuất fields ở nền.
              Trang tự refresh mỗi 5 giây — chờ 30-90s tuỳ độ phức tạp của ảnh.
              Không cần bấm gì.
            </p>
          </div>
        </div>
      </section>
    );
  }

  // OCR done + text available. Fields not yet populated → let user trigger
  // extraction manually (or wait for the chained extract job to finish).
  const heading = isOcrReady
    ? "OCR xong — AI đang trích fields, hoặc bấm nút để chạy lại"
    : "OCR chưa khả dụng — paste raw text để AI trích xuất";
  const subtext = isOcrReady
    ? "Text đã đọc từ ảnh sẵn ở ô dưới. Nếu fields chưa xuất hiện sau 60s (extract job chậm hoặc LLM trả rỗng) — bấm nút để chạy lại thủ công."
    : "Pipeline OCR (PaddleOCR) hoặc không cài hoặc gặp lỗi runtime. Bạn có thể copy nội dung chứng từ từ PDF/ảnh và paste vào đây, AI sẽ trích xuất fields + đề xuất bút toán N/C theo TT200.";
  const bannerTone = isOcrReady
    ? "border-blue-200 bg-blue-50/50"
    : "border-amber-200 bg-amber-50/50";
  const iconTone = isOcrReady ? "text-blue-700" : "text-amber-700";
  const headingTone = isOcrReady ? "text-blue-900" : "text-amber-900";
  const bodyTone = isOcrReady ? "text-blue-800" : "text-amber-800";

  return (
    <section className={`space-y-3 rounded-[var(--radius)] border p-5 ${bannerTone}`}>
      <div className="flex items-start gap-2">
        <Wand2 className={`mt-0.5 size-4 shrink-0 ${iconTone}`} />
        <div>
          <h2 className={`font-serif text-lg ${headingTone}`}>{heading}</h2>
          <p className={`mt-1 text-xs ${bodyTone}`}>{subtext}</p>
        </div>
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="raw-text">
          Nội dung chứng từ {!hasRealOcr && "(mẫu — thay bằng nội dung thật)"}
        </Label>
        <Textarea
          id="raw-text"
          value={rawText}
          onChange={(e) => setRawText(e.target.value)}
          rows={14}
          className="font-mono text-xs"
        />
        <p className="text-[10px] text-[var(--color-muted-fg)]">
          {rawText.length.toLocaleString("vi-VN")} ký tự
          {rawText.length > 50_000 && " — sẽ cắt ở 50K"}
        </p>
      </div>

      <Button
        onClick={submit}
        disabled={loading || rawText.trim().length < 20}
        className="w-full"
      >
        {loading ? (
          <>
            <Loader2 className="size-4 animate-spin" /> Đang chạy AI extract…
          </>
        ) : (
          <>
            <Wand2 className="size-4" /> Trích xuất + đề xuất bút toán
          </>
        )}
      </Button>

      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          {error}
        </p>
      )}

      {result && (
        <div className="space-y-2 rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 p-3">
          <p className="text-xs font-medium text-emerald-700">✓ Kết quả AI</p>
          <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{result}</ReactMarkdown>
          </div>
          <p className="text-[10px] italic text-emerald-700">
            Trang sẽ tự refresh trong 1s để hiển thị fields đã trích xuất…
          </p>
        </div>
      )}
    </section>
  );
}
