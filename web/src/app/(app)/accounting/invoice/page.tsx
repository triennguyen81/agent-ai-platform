"use client";

import { useCallback, useRef, useState } from "react";
import { FileText, Loader2, Trash2, Upload, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/input";
import { SaveToKbButton } from "@/components/save-to-kb-button";
import { CapabilityError, executeCapability } from "@/lib/capability";
import { isImageFilename, uploadAndExtract } from "@/lib/upload-extract";

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
  }

  function clearFile() {
    setFile(null);
    setFileStatus("idle");
    setFileError(null);
    setExtractedFileInfo(null);
    setSourceObjectKey(null);
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
            </div>
          )}
        </div>

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
