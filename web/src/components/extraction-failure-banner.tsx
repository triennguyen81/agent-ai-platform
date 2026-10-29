"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { AlertTriangle, Loader2, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { gatewayErrorMessage } from "@/lib/voucher-view";

// Safe reason per server code — the provider's own message never reaches
// the browser (see Gateway ExtractionFailures).
const REASONS: Record<string, string> = {
  provider_unavailable: "Nhà cung cấp AI tạm thời không khả dụng",
  provider_rate_limited: "Nhà cung cấp AI đang giới hạn tần suất — thử lại sau",
  provider_session_limit: "Nhà cung cấp AI đã hết hạn mức phiên — thử lại sau khi hạn mức được làm mới",
  provider_timeout: "Quá thời gian xử lý",
  agent_execution_failed: "Trợ lý không chạy được yêu cầu trích xuất",
  extraction_output_invalid: "Kết quả trích xuất không hợp lệ",
  processing_agent_unavailable: "Không thể chạy trợ lý đã ghi nhận cho chứng từ này",
  processing_agent_version_unavailable: "Không thể chạy phiên bản Agent đã ghi nhận",
  processing_provenance_invalid: "Thông tin phiên bản xử lý của chứng từ không hợp lệ",
};

export function ExtractionFailureBanner({
  voucherId,
  voucherType,
  code,
  failedAt,
  keptPrevious,
}: {
  voucherId: string;
  voucherType: string;
  code: string;
  failedAt: string | null;
  keptPrevious: boolean;
}) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function retry() {
    setBusy(true);
    setError(null);
    try {
      // Gateway decides who may re-extract (creator USER or department ADMIN).
      const res = await fetch(`/api/vouchers/${voucherId}/extract`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ voucherType }),
      });
      if (!res.ok) {
        setError(
          res.status === 403
            ? "Bạn không có quyền trích xuất lại chứng từ này."
            : res.status === 409
              ? gatewayErrorMessage(await res.text())
              : `HTTP ${res.status}`,
        );
        return;
      }
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div data-testid="extraction-failure" className="flex items-start gap-3 rounded-[var(--radius)] border border-red-200 bg-red-50 p-3 text-sm text-red-800">
      <AlertTriangle className="mt-0.5 size-4 shrink-0" />
      <div className="flex-1 space-y-1">
        <p className="font-medium">{keptPrevious ? "Trích xuất lại thất bại" : "Trích xuất thất bại"}</p>
        <p data-testid="extraction-failure-reason">
          {REASONS[code] ?? "Lỗi xử lý"}
          {failedAt ? ` · ${new Date(failedAt).toLocaleString("vi-VN")}` : ""}
        </p>
        {keptPrevious && <p className="text-xs">Kết quả trích xuất thành công trước đó vẫn được giữ nguyên.</p>}
        {error && <p className="text-xs">{error}</p>}
      </div>
      <Button size="sm" variant="outline" onClick={retry} disabled={busy}>
        {busy ? <Loader2 className="size-4 animate-spin" /> : <RotateCcw className="size-4" />} Trích xuất lại
      </Button>
    </div>
  );
}
