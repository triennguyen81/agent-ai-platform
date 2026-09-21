"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { ArrowLeft, FileText, Loader2, Trash2, Upload, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Input, Label, Textarea } from "@/components/ui/input";
import { isImageFilename, uploadAndExtract } from "@/lib/upload-extract";
import { fetchCapabilityAgentSelection, type CapabilityAgentSelectionState } from "@/lib/capability";
import { CapabilityAgentSelector } from "@/components/capability-agent-selector";

const CONTRACT_CAPABILITY = "contract.processing";

interface AgentRunResponse {
  final_answer: string;
}

type FileStatus = "idle" | "uploading" | "reading" | "ocr" | "done" | "error";

const ACCEPTED_EXTENSIONS = [".pdf", ".docx", ".png", ".jpg", ".jpeg"];

const SAMPLE = `HỢP ĐỒNG CUNG CẤP DỊCH VỤ
Số: 04/2026/HĐDV
Hôm nay, ngày 15/06/2026, tại Hà Nội, chúng tôi gồm:

BÊN A (BÊN SỬ DỤNG DỊCH VỤ):
Công ty TNHH Cát Tường AI
MST: 0399999999
Địa chỉ: 123 Nguyễn Huệ, P.Bến Nghé, Q.1, TP HCM
Đại diện: Ông Nguyễn Văn A — Giám đốc

BÊN B (BÊN CUNG CẤP DỊCH VỤ):
Công ty TNHH Dịch Vụ Kế Toán XYZ
MST: 0312345678
Đại diện: Bà Trần Thị B — Giám đốc

Hai bên thoả thuận ký kết hợp đồng với các điều khoản sau:

Điều 1 — Nội dung dịch vụ
Bên B cung cấp dịch vụ kế toán định kỳ hàng tháng cho Bên A,
bao gồm: lập sổ sách, kê khai thuế GTGT, TNCN, TNDN, báo cáo
tài chính cuối năm.

Điều 2 — Giá trị hợp đồng
Tổng giá trị hợp đồng: 240.000.000 đồng (Hai trăm bốn mươi triệu
đồng chẵn) đã bao gồm VAT 10%. Thanh toán hàng tháng 20 triệu đồng
trước ngày 05 của tháng.

Điều 3 — Thời hạn hợp đồng
Hợp đồng có hiệu lực từ ngày 01/07/2026 đến hết ngày 30/06/2027.

Điều 4 — Phạt vi phạm
Trường hợp Bên A chậm thanh toán quá 15 ngày, Bên B có quyền
tính lãi suất 0,03%/ngày trên số tiền chậm trả và tạm dừng
dịch vụ. Trường hợp đơn phương chấm dứt hợp đồng trái pháp luật,
bên vi phạm phải bồi thường 10% tổng giá trị hợp đồng.

Điều 5 — Báo cáo định kỳ
Bên B nộp báo cáo công việc hàng tháng cho Bên A trước ngày 10
của tháng kế tiếp.

Điều 6 — Điều khoản chung
Hai bên cam kết tuân thủ đầy đủ các điều khoản nêu trên.
Hợp đồng được lập thành 4 bản, mỗi bên giữ 2 bản có giá trị
pháp lý như nhau.

Đại diện Bên A          Đại diện Bên B
(Đã ký)                 (Đã ký)
Nguyễn Văn A            Trần Thị B`;

export default function ContractUploadPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [titleHint, setTitleHint] = useState("");
  const [rawText, setRawText] = useState(SAMPLE);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState("");
  const [error, setError] = useState<string | null>(null);

  // "Xử lý bằng trợ lý": resolves 0/1/many eligible Agents for
  // contract.processing up front, so the user picks (when needed) before
  // ever submitting — never a substitute for the Gateway's own server-side
  // re-validation of whatever ends up selected.
  const [selection, setSelection] = useState<CapabilityAgentSelectionState>({ status: "loading" });
  useEffect(() => {
    const preselect = searchParams.get("agent");
    let cancelled = false;
    fetchCapabilityAgentSelection(CONTRACT_CAPABILITY, preselect).then((s) => {
      if (!cancelled) setSelection(s);
    });
    return () => {
      cancelled = true;
    };
    // Deliberately runs once on mount — searchParams is only ever read for
    // the initial preselect, never re-resolved as the user types.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const preferredAgentId =
    selection.status === "auto" ? selection.agent.agentId
    : selection.status === "choose" ? selection.selectedAgentId
    : null;

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [fileStatus, setFileStatus] = useState<FileStatus>("idle");
  const [fileError, setFileError] = useState<string | null>(null);
  const [extracted, setExtracted] = useState<{ charCount: number; filename: string } | null>(null);
  // Points at the file already stored in MinIO by the extract-text step —
  // carried through to save_contract as source_pdf_path so the saved
  // contract links back to its source file. Editing rawText afterward does
  // NOT clear this (the edited text still came from this file); only
  // removing the file ("Xóa file") or picking a different one does.
  const [sourceObjectKey, setSourceObjectKey] = useState<string | null>(null);

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
    setExtracted(null);
    setSourceObjectKey(null);
  }

  function clearFile() {
    setFile(null);
    setFileStatus("idle");
    setFileError(null);
    setExtracted(null);
    setSourceObjectKey(null);
    // rawText is deliberately left untouched — manual input stays available.
  }

  const extractFromFile = useCallback(async () => {
    if (!file || fileStatus === "uploading" || fileStatus === "reading" || fileStatus === "ocr") return;
    setFileStatus("uploading");
    setFileError(null);

    const formData = new FormData();
    formData.append("file", file);

    const outcome = await uploadAndExtract("/api/contracts/extract-text", formData, () => {
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
    setExtracted({ charCount: outcome.body.charCount, filename: outcome.body.originalFilename });
    setSourceObjectKey(outcome.body.sourceObjectKey);
    setFileStatus("done");
  }, [file, fileStatus]);

  async function submit() {
    if (loading || !rawText.trim()) return;
    // Belt-and-suspenders on top of the Gateway's own re-validation: never
    // even attempt to submit without a concrete Agent chosen.
    if (selection.status === "none" || selection.status === "error") return;
    if (selection.status === "choose" && !selection.selectedAgentId) return;
    setLoading(true);
    setError(null);
    setResult("");

    try {
      const res = await fetch("/api/contracts/upload", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          raw_text: rawText,
          title_hint: titleHint || undefined,
          source_pdf_path: sourceObjectKey || undefined,
          original_filename: file?.name || undefined,
          preferred_agent_id: preferredAgentId || undefined,
        }),
      });

      if (!res.ok) {
        // D6: never surface the raw upstream body (may carry internal
        // detail) — translate the capability resolver's known denial
        // statuses into a clear message, same wording as other converted
        // business pages.
        if (res.status === 409) {
          setError("Nghiệp vụ này đang được gán cho nhiều Agent. Vui lòng liên hệ quản trị để cấu hình rõ ràng.");
        } else if (res.status === 403) {
          setError("Bạn chưa được cấp Agent hỗ trợ nghiệp vụ này. Liên hệ admin/tenant owner để được cấu hình.");
        } else {
          setError(`Đã có lỗi xảy ra (HTTP ${res.status}). Vui lòng thử lại.`);
        }
        return;
      }

      const body = (await res.json()) as AgentRunResponse;
      setResult(body.final_answer);
      // Refresh contracts list after a moment so the user sees the new row
      setTimeout(() => router.refresh(), 1000);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setLoading(false);
    }
  }

  const fileBusy = fileStatus === "uploading" || fileStatus === "reading" || fileStatus === "ocr";
  const fileStatusLabel: Record<FileStatus, string> = {
    idle: "",
    uploading: "Đang tải...",
    reading: "Đang đọc nội dung...",
    ocr: "OCR...",
    done: "Hoàn tất",
    error: "Lỗi",
  };

  return (
    <div className="space-y-6">
      <div>
        <Link
          href="/contracts"
          className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        >
          <ArrowLeft className="size-3.5" /> Tất cả hợp đồng
        </Link>
      </div>

      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Thêm hợp đồng</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Tải lên file PDF / Word / ảnh để tự động trích xuất nội dung, hoặc
          paste trực tiếp nội dung hợp đồng bên dưới. AI sẽ trích xuất các
          trường + clauses và lưu vào KB.
        </p>
      </header>

      <div className="grid gap-4 lg:grid-cols-[360px_1fr]">
        <div className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <div className="space-y-1.5">
            <Label htmlFor="title-hint">Title gợi ý (tuỳ chọn)</Label>
            <Input
              id="title-hint"
              value={titleHint}
              onChange={(e) => setTitleHint(e.target.value)}
              placeholder="vd: Hợp đồng dịch vụ kế toán XYZ"
              maxLength={300}
            />
            <p className="text-[10px] text-[var(--color-muted-fg)]">
              AI sẽ ưu tiên dùng title này nếu hợp đồng không có rõ tên.
            </p>
          </div>

          {selection.status === "choose" && (
            <CapabilityAgentSelector
              agents={selection.agents}
              selectedAgentId={selection.selectedAgentId}
              onSelect={(agentId) => setSelection({ ...selection, selectedAgentId: agentId })}
              disabled={loading}
            />
          )}

          {selection.status === "none" && (
            <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
              Không có trợ lý phù hợp với nghiệp vụ Hợp đồng.
            </p>
          )}

          <Button
            onClick={submit}
            disabled={
              loading ||
              !rawText.trim() ||
              selection.status === "loading" ||
              selection.status === "none" ||
              selection.status === "error" ||
              (selection.status === "choose" && !selection.selectedAgentId)
            }
            className="w-full"
          >
            {loading ? (
              <>
                <Loader2 className="size-4 animate-spin" /> Đang xử lý…
              </>
            ) : (
              <>
                <Wand2 className="size-4" /> Trích xuất + lưu hợp đồng
              </>
            )}
          </Button>

          {error && (
            <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
              {error}
            </p>
          )}

          <p className="text-[10px] text-[var(--color-muted-fg)]">
            Quá trình: OCR text → skill `contract-extractor` → JSON →
            `save_contract` tool → POST gateway. Mất ~10-30s.
          </p>
        </div>

        <div className="space-y-3">
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
                <Upload className="size-4" /> Chọn file PDF / Word / ảnh
              </Button>
            ) : (
              <div className="space-y-2">
                <div className="flex items-center gap-2 text-sm">
                  <FileText className="size-4 shrink-0 text-[var(--color-primary)]" />
                  <span className="min-w-0 flex-1 truncate">
                    Tên file: <span className="font-medium">{file.name}</span>
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

                {fileStatus === "done" && extracted && (
                  <p className="text-xs text-emerald-700">
                    Đã trích xuất {extracted.charCount.toLocaleString("vi-VN")} ký tự từ{" "}
                    {extracted.filename}
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
              </div>
            )}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="raw-text">Nội dung hợp đồng (raw text)</Label>
            <Textarea
              id="raw-text"
              value={rawText}
              onChange={(e) => setRawText(e.target.value)}
              rows={20}
              className="font-mono text-xs"
            />
            <p className="text-[10px] text-[var(--color-muted-fg)]">
              {rawText.length.toLocaleString("vi-VN")} ký tự
              {rawText.length > 50_000 && " — sẽ cắt ở 50K"}
              {sourceObjectKey && " · nguồn: file đã tải lên"}
            </p>
          </div>

          {result && (
            <div className="space-y-2 rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 p-4">
              <p className="font-medium text-emerald-700">✓ Kết quả</p>
              <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
                <ReactMarkdown remarkPlugins={[remarkGfm]}>{result}</ReactMarkdown>
              </div>
              <Button asChild variant="outline" size="sm">
                <Link href="/contracts">Xem danh sách hợp đồng</Link>
              </Button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
