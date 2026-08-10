import { NextResponse, type NextRequest } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Manual extraction flow (Phase 1.5 — PaddleOCR fallback).
 *
 * When the upload OCR pipeline is unavailable (agent runtime doesn't have
 * PaddleOCR installed), the voucher stub is created with status
 * `ocr_unavailable`. The user pastes the invoice text into the detail
 * page and clicks Extract — that lands here.
 *
 * Steps:
 *   1. Load session token.
 *   2. POST /agent/run with:
 *      - The pasted raw text (bounded to 50K chars).
 *      - An explicit voucher_id in the prompt so save_voucher UPDATEs
 *        the existing stub instead of creating a duplicate row.
 *   3. Return the agent's summary. The client refreshes to see the
 *      updated fields + journal suggestions.
 */
interface Body {
  rawText?: string;
  voucherType?: string;
}

interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function POST(request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const { id: voucherId } = await context.params;
  const body = (await request.json().catch(() => ({}))) as Body;
  const rawText = String(body?.rawText ?? "").trim();
  const voucherType = String(body?.voucherType ?? "invoice_vat").trim();

  if (!rawText || rawText.length < 20) {
    return NextResponse.json(
      { error: "rawText quá ngắn (cần ≥ 20 ký tự)" },
      { status: 400 },
    );
  }

  const task = [
    `Voucher đã có id=${voucherId} trong DB. Thực hiện 3 bước:`,
    "",
    `**Bước 1** — Áp dụng skill \`voucher-invoice-vat\` để trích xuất JSON từ raw text bên dưới. voucher_type = "${voucherType}".`,
    "",
    "**Bước 2** — Gọi tool `save_voucher` với:",
    `  - voucher_id = "${voucherId}"  (BẮT BUỘC để UPDATE stub row, không tạo mới)`,
    "  - fields = JSON đã extract (bao gồm voucher_type)",
    "  - raw_text = nguyên văn raw text bên dưới",
    "",
    `**Bước 3** — Gọi tool \`suggest_journal_entry\` với voucher_id="${voucherId}" để lấy prompt hướng dẫn; bạn sinh JSON array các bút toán theo skill journal-entry-formatter.`,
    "",
    `**Bước 4** — Gọi tool \`save_journal_suggestions\` với:`,
    `  - voucher_id="${voucherId}"`,
    "  - entries=JSON array vừa sinh (mỗi entry: entry_no, debit_account, credit_account, amount, description, confidence)",
    "  - replace_existing=true",
    "",
    "Kết thúc: summary ngắn:",
    "  - Số hoá đơn / ngày / bên bán / tổng tiền",
    "  - Số bút toán đã lưu",
    "  - Cảnh báo nếu có",
    "",
    "===== RAW TEXT =====",
    rawText.slice(0, 50_000),
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
    voucherId,
    status: "processed",
    agentResponse: agentText,
  });
}
