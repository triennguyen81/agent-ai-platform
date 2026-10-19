import { NextResponse, type NextRequest } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Voucher upload flow (Phase 1 — hoá đơn VAT):
 *   1. Web posts multipart to gateway /vouchers/upload → stub row + S3 key.
 *   2. Gateway chains OCR → LLM extract (Hangfire) — see
 *      VoucherExtractJobRunner.cs, which is the ONE canonical
 *      extraction/journal-suggestion implementation (docs/
 *      ACCOUNTING_AGENT_AUDIT.md F-04 hardening: a second, independent
 *      JSON-block-parsing implementation used to live in this file for the
 *      old synchronous flow — confirmed dead code, since removed, not
 *      merely disabled — the current flow never calls the agent from here
 *      at all).
 *   3. Web just relays the fast response — no polling, no bg promise, no
 *      Next.js lifecycle risk. Detail page auto-refreshes while the
 *      pipeline runs and stops once status='extracted'.
 */

interface UploadResponse {
  voucherId: string;
  status: string;
  rawText?: string;
  ocrConfidence?: number;
  error?: string;
}

export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const url = new URL(request.url);
  const voucherType = url.searchParams.get("voucherType") ?? "auto";
  // Hint only — the Gateway re-authorizes it through the uploader's own
  // capability chain before the extract job ever uses it.
  const preferredAgentId = url.searchParams.get("preferredAgentId") ?? undefined;

  const formData = await request.formData();
  const file = formData.get("file");
  if (!(file instanceof File) || file.size === 0) {
    return NextResponse.json({ error: "file trống" }, { status: 400 });
  }

  const uploadForm = new FormData();
  uploadForm.append("file", file, file.name);
  const uploadRes = await gateway.uploadVoucher(token, uploadForm, voucherType, preferredAgentId);
  if (!uploadRes.ok) {
    const errText = await uploadRes.text();
    return new NextResponse(errText, { status: uploadRes.status });
  }
  const uploadJson = (await uploadRes.json()) as UploadResponse;

  return NextResponse.json({
    voucherId: uploadJson.voucherId,
    status: "uploading",
    ocrConfidence: 0,
    extractedFieldsOk: false,
    journalEntriesSaved: 0,
  });
}
