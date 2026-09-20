import { NextResponse, type NextRequest } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Accounting Invoice page — file-upload path. Thin relay to the gateway
 * (validates, stores the original into the existing voucher-uploads MinIO
 * bucket under a staging key, and calls the agent's PDF text-layer/OCR/
 * DOCX/XML extraction) — same shape as /api/contracts/extract-text and
 * /api/vouchers/upload.
 *
 * Never creates or applies any accounting mutation. Returns extracted text
 * for the browser to put into the existing "Nội dung hoá đơn" textarea for
 * review/edit before the user clicks "Trích xuất trường" / "Đề xuất bút
 * toán" (both unchanged — still /api/agent/run via the accounting.invoice
 * capability).
 */
export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const formData = await request.formData();
  const file = formData.get("file");
  if (!(file instanceof File) || file.size === 0) {
    return NextResponse.json({ error: "file trống" }, { status: 400 });
  }

  const uploadForm = new FormData();
  uploadForm.append("file", file, file.name);
  const res = await gateway.extractInvoiceText(token, uploadForm);

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
