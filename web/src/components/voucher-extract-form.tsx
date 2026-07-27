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
}: VoucherExtractFormProps): React.ReactElement {
  const router = useRouter();
  const [rawText, setRawText] = useState(initialRawText || SAMPLE_INVOICE_TEXT);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState("");
  const [error, setError] = useState<string | null>(null);

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

  return (
    <section className="space-y-3 rounded-[var(--radius)] border border-amber-200 bg-amber-50/50 p-5">
      <div className="flex items-start gap-2">
        <Wand2 className="mt-0.5 size-4 shrink-0 text-amber-700" />
        <div>
          <h2 className="font-serif text-lg text-amber-900">
            OCR chưa khả dụng — paste raw text để AI trích xuất
          </h2>
          <p className="mt-1 text-xs text-amber-800">
            Pipeline OCR (PaddleOCR) chưa cài trong agent runtime. Bạn có thể copy
            nội dung chứng từ từ PDF/ảnh và paste vào đây, AI sẽ trích xuất fields
            + đề xuất bút toán N/C theo TT200.
          </p>
        </div>
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="raw-text">Nội dung chứng từ</Label>
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
