import { NextResponse, type NextRequest } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Contract Upload page — file-upload path. Thin relay to the gateway
 * (which validates, stores the original into MinIO under a staging key,
 * and calls the agent's OCR/DOCX/text-layer extraction) — same "web just
 * forwards the multipart body" shape as /api/vouchers/upload.
 *
 * Never creates a contract. Returns extracted text for the browser to put
 * into the existing textarea for review/edit before the user clicks
 * "Trích xuất + lưu hợp đồng" (still /api/contracts/upload).
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
  const res = await gateway.extractContractText(token, uploadForm);

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
