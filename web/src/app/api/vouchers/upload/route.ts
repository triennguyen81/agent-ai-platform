import { NextResponse, type NextRequest } from "next/server";
import { agent, gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Voucher upload flow (Phase 1 — hoá đơn VAT):
 *   1. Web posts multipart to gateway /vouchers/upload → stub row + S3 key
 *   2. Ask agent to (a) extract JSON per voucher-invoice-vat, then
 *      (b) suggest N/C entries per journal-entry-formatter. We do NOT
 *      rely on the model calling save_voucher/save_journal_suggestions
 *      as MCP tools — Claude subscription CLI doesn't always dispatch
 *      tool_use blocks reliably, especially when the response fits in
 *      one text turn. Instead we parse the JSON blocks from the agent's
 *      text reply and POST them to gateway ourselves. Deterministic.
 *   3. PATCH /vouchers/{id}/apply-extraction with the extracted fields
 *      (updates the stub row, no duplicate).
 *   4. POST /vouchers/{id}/journal-suggestions with the entries array.
 *   5. Response back to client contains voucherId → client redirects.
 */

interface UploadResponse {
  voucherId: string;
  status: string;
  rawText?: string;
  ocrConfidence?: number;
  error?: string;
}

interface AgentRunResponse {
  final_answer?: string;
  steps?: Array<{ text?: string }>;
}

/**
 * Pull the first two JSON blocks (```json { ... } ```) from the agent's
 * markdown reply. The prompt below asks the model for:
 *   [BLOCK 1] the extracted voucher fields object
 *   [BLOCK 2] the journal entries array
 */
function extractJsonBlocks(text: string): { fields: unknown | null; entries: unknown[] | null } {
  const blocks: string[] = [];
  const fence = /```(?:json)?\s*([\s\S]*?)```/g;
  let match: RegExpExecArray | null;
  while ((match = fence.exec(text)) !== null) {
    blocks.push(match[1].trim());
  }

  // Fallback: if no fenced blocks, try to find raw JSON `{...}` and `[...]`.
  if (blocks.length === 0) {
    const obj = text.match(/\{[\s\S]*?\}(?=\s*(?:\n|$))/);
    if (obj) blocks.push(obj[0]);
    const arr = text.match(/\[[\s\S]*?\](?=\s*(?:\n|$))/);
    if (arr) blocks.push(arr[0]);
  }

  let fields: unknown | null = null;
  let entries: unknown[] | null = null;

  for (const raw of blocks) {
    try {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) {
        entries = parsed;
      } else if (parsed && typeof parsed === "object") {
        // Fields object: heuristic — has voucher_type OR document_number.
        const o = parsed as Record<string, unknown>;
        if (o.voucher_type !== undefined || o.document_number !== undefined || o.seller_name !== undefined) {
          fields = parsed;
        } else if (o.entries && Array.isArray(o.entries)) {
          entries = o.entries;
        }
      }
    } catch {
      // Skip malformed block — try the next one.
    }
  }

  return { fields, entries };
}

function toGatewayFieldsPayload(fields: Record<string, unknown>, rawText: string): Record<string, unknown> {
  const lineItems = Array.isArray(fields.line_items)
    ? fields.line_items.map((li, idx) => {
        const l = li as Record<string, unknown>;
        return {
          ordinal: Number(l.ordinal ?? idx + 1),
          itemCode: l.item_code ?? null,
          description: String(l.description ?? "").slice(0, 500),
          unit: l.unit ?? null,
          quantity: l.quantity ?? null,
          unitPrice: l.unit_price ?? null,
          subtotal: l.subtotal ?? null,
          vatRate: l.vat_rate ?? null,
          vatAmount: l.vat_amount ?? null,
          totalAmount: l.total_amount ?? null,
        };
      })
    : [];

  return {
    voucherType: fields.voucher_type ?? "invoice_vat",
    documentNumber: fields.document_number ?? null,
    documentSeries: fields.document_series ?? null,
    documentDate: fields.document_date ?? null,
    sellerName: fields.seller_name ?? null,
    sellerMst: fields.seller_mst ?? null,
    sellerAddress: fields.seller_address ?? null,
    buyerName: fields.buyer_name ?? null,
    buyerMst: fields.buyer_mst ?? null,
    buyerAddress: fields.buyer_address ?? null,
    paymentMethod: fields.payment_method ?? null,
    subtotal: fields.subtotal ?? null,
    vatRate: fields.vat_rate ?? null,
    vatAmount: fields.vat_amount ?? null,
    discountAmount: fields.discount_amount ?? null,
    totalAmount: fields.total_amount ?? null,
    currency: fields.currency ?? "VND",
    amountInWords: fields.amount_in_words ?? null,
    rawOcrText: rawText,
    extractedFields: fields,
    tags: Array.isArray(fields.tags) ? fields.tags : [],
    lineItems,
  };
}

function toJournalPayload(entries: unknown[]): { entries: unknown[]; replaceExisting: boolean } {
  const normalized = entries
    .map((raw, idx) => {
      if (!raw || typeof raw !== "object") return null;
      const e = raw as Record<string, unknown>;
      const debit = String(e.debit_account ?? "").trim();
      const credit = String(e.credit_account ?? "").trim();
      const amount = Number(e.amount ?? 0);
      if (!debit || !credit || !amount || amount <= 0) return null;
      return {
        entryNo: Number(e.entry_no ?? idx + 1),
        debitAccount: debit,
        creditAccount: credit,
        amount,
        description: e.description ?? null,
        confidence: e.confidence ?? null,
      };
    })
    .filter((e): e is NonNullable<typeof e> => e !== null);
  return { entries: normalized, replaceExisting: true };
}

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

  // Step 1 — upload to gateway (S3 + OCR)
  const uploadForm = new FormData();
  uploadForm.append("file", file, file.name);
  const uploadRes = await gateway.uploadVoucher(token, uploadForm, voucherType);
  if (!uploadRes.ok) {
    const errText = await uploadRes.text();
    return new NextResponse(errText, { status: uploadRes.status });
  }
  const uploadJson = (await uploadRes.json()) as UploadResponse;

  if (uploadJson.status === "rejected" || !uploadJson.rawText) {
    return NextResponse.json(uploadJson, { status: 200 });
  }

  // The agent extraction + PATCH + POST easily takes 60-90s. Cloudflare's
  // edge cuts off at 100s. Instead of awaiting the whole pipeline here,
  // return the voucherId immediately with status="processing" and run the
  // extract-and-save flow as a background promise. The detail page polls
  // for status transitions (processing → extracted).
  const rawText = uploadJson.rawText || "";
  void runExtractionAndSave({
    voucherId: uploadJson.voucherId,
    voucherType,
    rawText,
    token,
  });

  return NextResponse.json({
    voucherId: uploadJson.voucherId,
    status: "processing",
    ocrConfidence: uploadJson.ocrConfidence,
    extractedFieldsOk: false,
    journalEntriesSaved: 0,
  });
}

// Runs the agent extraction + PATCH + POST after we've already responded
// to the client. Any failure is logged, not surfaced to the user — the
// detail page will just keep showing status="processing".
async function runExtractionAndSave(args: {
  voucherId: string;
  voucherType: string;
  rawText: string;
  token: string;
}): Promise<void> {
  const { voucherId, voucherType, rawText, token } = args;
  try {
    const task = [
      "Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 hoá đơn GTGT bên dưới.",
      "",
      "**BLOCK 1** — Áp dụng skill `voucher-invoice-vat` để trích xuất fields.",
      `Trả về JSON object có voucher_type="${voucherType}", document_number, document_series, document_date (YYYY-MM-DD), seller_name, seller_mst, seller_address, buyer_name, buyer_mst, buyer_address, payment_method (CK/TM/MIXED), subtotal, vat_rate, vat_amount, discount_amount, total_amount, currency, amount_in_words, line_items[] (mỗi item: ordinal, description, unit, quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount), tags[].`,
      "",
      "**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200.",
      "Trả về JSON array các entries. Mỗi entry: {entry_no, debit_account, credit_account, amount, description, confidence}.",
      "Quy tắc: Hoá đơn VAT đầu vào (mua) → Nợ 156/152/641/642 + Nợ 1331 / Có 331 (công nợ) hoặc 111/112 (trả ngay). Hoá đơn đầu ra (bán) → Nợ 131/111/112 / Có 511 + Có 3331.",
      "",
      "**QUAN TRỌNG**: chỉ trả 2 code block JSON, KHÔNG kèm markdown khác:",
      "```json",
      "{ ...fields object... }",
      "```",
      "```json",
      "[ ...entries array... ]",
      "```",
      "",
      "===== RAW OCR TEXT =====",
      rawText.slice(0, 20_000),
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
      max_turns: 3,
    });
    const agentJson = (await agentRes.json().catch(() => ({}))) as AgentRunResponse;
    const agentText = agentJson.final_answer ?? "";
    const { fields, entries } = extractJsonBlocks(agentText);

    if (fields && typeof fields === "object") {
      const payload = toGatewayFieldsPayload(fields as Record<string, unknown>, rawText);
      await fetch(
        `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${voucherId}/apply-extraction`,
        {
          method: "PATCH",
          headers: {
            Authorization: `Bearer ${token}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify(payload),
          cache: "no-store",
        },
      );
    }

    if (entries && Array.isArray(entries) && entries.length > 0) {
      const payload = toJournalPayload(entries);
      if (payload.entries.length > 0) {
        await gateway.saveVoucherJournal(token, voucherId, payload);
      }
    }
  } catch (err) {
    console.error(`[voucher ${voucherId}] extraction failed:`, err);
  }
}
