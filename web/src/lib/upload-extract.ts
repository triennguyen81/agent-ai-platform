// Shared by the Contract Upload and Accounting Invoice pages — both wrap a
// file in FormData, POST it to their own "extract-text" Next.js route
// (which thinly relays to the gateway), and want the SAME real-progress
// status transitions (Đang tải... -> Đang đọc nội dung.../OCR... -> Hoàn
// tất/Lỗi). Extracted here once a second page needed the identical logic.

export interface ExtractResponse {
  text: string;
  charCount: number;
  mimeType: string;
  extractionMethod: string;
  originalFilename: string;
  sourceObjectKey: string;
}

export type ExtractOutcome =
  | { ok: true; body: ExtractResponse }
  | { ok: false; status: number; detail: string };

/**
 * XMLHttpRequest (not fetch) so the "uploading" status reflects real
 * upload progress, and the "processing" status only shows once the file
 * has actually finished uploading and the server is working on it.
 */
export function uploadAndExtract(
  endpoint: string,
  formData: FormData,
  onUploadComplete: () => void,
): Promise<ExtractOutcome> {
  return new Promise((resolve) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", endpoint);
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && e.loaded >= e.total) onUploadComplete();
    };
    xhr.onload = () => {
      let parsed: Record<string, unknown> = {};
      try {
        parsed = JSON.parse(xhr.responseText || "{}");
      } catch {
        // fall through with empty parsed body
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve({ ok: true, body: parsed as unknown as ExtractResponse });
      } else {
        const detail =
          (typeof parsed.detail === "string" && parsed.detail) ||
          (typeof parsed.error === "string" && parsed.error) ||
          `HTTP ${xhr.status}`;
        resolve({ ok: false, status: xhr.status, detail });
      }
    };
    xhr.onerror = () => resolve({ ok: false, status: 0, detail: "Network error" });
    xhr.send(formData);
  });
}

export function isImageFilename(name: string): boolean {
  const n = name.toLowerCase();
  return n.endsWith(".png") || n.endsWith(".jpg") || n.endsWith(".jpeg");
}
