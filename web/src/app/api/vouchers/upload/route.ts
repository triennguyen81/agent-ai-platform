import { NextResponse, type NextRequest } from "next/server";
import { agent, gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Voucher upload flow (Phase 1 — hoá đơn VAT):
 *   1. Web client posts multipart to /api/vouchers/upload with the image file
 *   2. This route forwards to Gateway /vouchers/upload — Gateway stages the
 *      file, runs /ocr/image, and returns { voucherId, rawText, ocrConfidence }
 *   3. This route then calls /agent/run with the raw text + `save_voucher`
 *      tool so the agent extracts + patches the voucher record
 *   4. Response back to client contains voucherId → client redirects to detail
 *
 * The two-hop keeps OCR in Gateway (single upload byte pipe) and LLM
 * extraction in Agent, matching the contract flow.
 */
export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const url = new URL(request.url);
  const voucherType = url.searchParams.get("voucherType") ?? "invoice_vat";

  const formData = await request.formData();
  const file = formData.get("file");
  if (!(file instanceof File) || file.size === 0) {
    return NextResponse.json({ error: "file trống" }, { status: 400 });
  }

  // Step 1 — OCR via Gateway
  const uploadForm = new FormData();
  uploadForm.append("file", file, file.name);
  const uploadRes = await gateway.uploadVoucher(token, uploadForm, voucherType);
  if (!uploadRes.ok) {
    const errText = await uploadRes.text();
    return new NextResponse(errText, { status: uploadRes.status });
  }
  const uploadJson = (await uploadRes.json()) as {
    voucherId: string;
    status: string;
    rawText?: string;
    ocrConfidence?: number;
    error?: string;
  };

  if (uploadJson.status === "rejected" || !uploadJson.rawText) {
    return NextResponse.json(uploadJson, { status: 200 });
  }

  // Step 2 — LLM extract + save via agent.
  // voucher_id is passed to save_voucher so the tool routes to
  // PATCH /vouchers/{id}/apply-extraction (updates the stub) instead
  // of POST /vouchers (creates a duplicate row).
  const task = [
    `Voucher stub đã tồn tại với id=${uploadJson.voucherId}. Thực hiện 3 bước:`,
    "",
    `**Bước 1** — Áp dụng skill \`voucher-invoice-vat\` để trích xuất JSON từ raw OCR text bên dưới. voucher_type = "${voucherType}".`,
    "",
    "**Bước 2** — Gọi tool `save_voucher` với:",
    `  - voucher_id = "${uploadJson.voucherId}"  (BẮT BUỘC — để UPDATE stub, không tạo duplicate)`,
    "  - fields = JSON đã extract (bao gồm voucher_type)",
    "  - raw_text = nguyên văn raw OCR text bên dưới",
    "",
    `**Bước 3** — Gọi tool \`suggest_journal_entry\` với voucher_id="${uploadJson.voucherId}" để lấy bút toán N/C theo TT200. Reply summary ngắn:`,
    "  - Số hoá đơn / ngày / bên bán / tổng tiền",
    "  - Bút toán suggest (Nợ/Có/Số tiền)",
    "  - Cảnh báo nếu có (VD: MST không hợp lệ, thiếu ngày)",
    "",
    "===== RAW OCR TEXT =====",
    (uploadJson.rawText || "").slice(0, 50_000),
    "===== END =====",
  ].join("\n");

  const agentRes = await agent.runTask({
    task,
    user_token: token,
    skills: [
      "voucher-invoice-vat",
      "accounting-vietnam",
      "journal-entry-formatter",
      "vietnamese-style",
    ],
    max_turns: 8,
  });
  const agentText = await agentRes.text();

  return NextResponse.json({
    voucherId: uploadJson.voucherId,
    status: "processed",
    ocrConfidence: uploadJson.ocrConfidence,
    agentResponse: agentText,
  });
}
