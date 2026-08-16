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

  // P1 (part 2): gateway chains OCR job → extract job (both Hangfire).
  // Web just relays the fast response — no polling, no bg promise, no
  // Next.js lifecycle risk. Detail page auto-refreshes while pipeline
  // runs and stops once status='extracted'.
  const uploadForm = new FormData();
  uploadForm.append("file", file, file.name);
  const uploadRes = await gateway.uploadVoucher(token, uploadForm, voucherType);
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

interface VoucherStatusResponse {
  voucher?: {
    status?: string;
    rawOcrText?: string | null;
    ocrConfidence?: number | null;
    errorMessage?: string | null;
  };
}

// Poll gateway /vouchers/{id} until OCR completes (max ~5 minutes), then
// run the LLM extraction + AR enrichment + 3-way match + save. Total wall
// time is bounded by node process lifetime; failures just leave the row
// at status='ocr_complete' with no extraction (user can retry manually).
async function pollAndExtract(args: {
  voucherId: string;
  voucherType: string;
  token: string;
}): Promise<void> {
  const { voucherId, voucherType, token } = args;
  const start = Date.now();
  const maxWaitMs = 5 * 60 * 1000;
  const pollIntervalMs = 3_000;

  let rawText = "";
  while (Date.now() - start < maxWaitMs) {
    await new Promise((r) => setTimeout(r, pollIntervalMs));
    try {
      const res = await gateway.getVoucher(token, voucherId);
      if (!res.ok) continue;
      const body = (await res.json()) as VoucherStatusResponse;
      const status = body.voucher?.status ?? "";
      if (status === "ocr_complete" && body.voucher?.rawOcrText) {
        rawText = body.voucher.rawOcrText;
        break;
      }
      if (status === "ocr_unavailable" || status === "rejected") {
        console.warn(
          `[voucher ${voucherId}] OCR job ended in ${status}: ${body.voucher?.errorMessage ?? "?"}`,
        );
        return;
      }
    } catch (err) {
      console.error(`[voucher ${voucherId}] poll error:`, err);
    }
  }

  if (!rawText) {
    console.warn(`[voucher ${voucherId}] OCR did not complete within ${maxWaitMs}ms`);
    return;
  }

  await runExtractionAndSave({ voucherId, voucherType, rawText, token });
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

    // UC-ACC-001 §5.4-5.7: enrich fields with customer + A/R context so the
    // detail page can show current balance / aging / credit limit / risk flags,
    // and the accountant sees whether this invoice pushes the customer over
    // their credit limit before approving the suggested journal.
    let arContext: Record<string, unknown> | null = null;
    if (fields && typeof fields === "object") {
      const fieldsObj = fields as Record<string, unknown>;
      const buyerMst = typeof fieldsObj.buyer_mst === "string" ? fieldsObj.buyer_mst.trim() : "";
      const invoiceTotal = typeof fieldsObj.total_amount === "number" ? fieldsObj.total_amount : 0;
      const documentNumber = typeof fieldsObj.document_number === "string" ? fieldsObj.document_number.trim() : "";
      if (buyerMst.length > 0) {
        arContext = await enrichWithCustomerAr(token, buyerMst, invoiceTotal);
        if (arContext) {
          fieldsObj.ar_context = arContext;
        }
      }
      // UC-ACC-001 §5.6: 3-way match by document_number
      if (documentNumber.length > 0) {
        const matchContext = await enrichWith3WayMatch(token, documentNumber);
        if (matchContext) {
          fieldsObj.match_context = matchContext;
        }
      }
      const payload = toGatewayFieldsPayload(fieldsObj, rawText);
      const applyRes = await fetch(
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
      // UC-ACC-001 E2: duplicate — flip this stub to rejected with the
      // duplicate hint. Skip journal save; the detail page will render the
      // error_message so the accountant sees the collision.
      if (applyRes.status === 409) {
        const body = await applyRes.text();
        console.warn(`[voucher ${voucherId}] duplicate detected: ${body}`);
        await fetch(
          `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${voucherId}`,
          {
            method: "PATCH",
            headers: {
              Authorization: `Bearer ${token}`,
              "Content-Type": "application/json",
            },
            body: JSON.stringify({
              status: "rejected",
              errorMessage: `Trùng hoá đơn: ${body.slice(0, 400)}`,
            }),
            cache: "no-store",
          },
        );
        return; // skip journal save for duplicate
      }
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

interface CustomerLite {
  id: string;
  customerCode: string;
  name: string;
  creditLimit: number;
  currentAr: number;
  category: string;
}

interface AgingBuckets {
  current: number;
  bucket_0_30: number;
  bucket_31_60: number;
  bucket_61_90: number;
  over90: number;
}

interface ReceivablesReport {
  customer: CustomerLite;
  currentBalance131: number;
  creditLimit: number;
  creditUsedPercent: number;
  aging: AgingBuckets;
  overdueAmount: number;
  lastPaymentDate: string | null;
  paymentTermDefault: number;
  openInvoices: Array<{ documentNumber: string; debit: number; dueDate: string | null; daysOverdue: number }>;
}

async function enrichWithCustomerAr(
  token: string,
  buyerMst: string,
  invoiceTotal: number,
): Promise<Record<string, unknown> | null> {
  try {
    const custRes = await gateway.customerByMst(token, buyerMst);
    if (!custRes.ok) {
      // Unknown MST — UC-ACC-001 A1 alt flow. Frontend can prompt to create.
      return { status: "customer_not_found", buyer_mst: buyerMst };
    }
    const customer = (await custRes.json()) as CustomerLite;

    const arRes = await gateway.customerReceivables(token, customer.id);
    if (!arRes.ok) {
      return { status: "ar_lookup_failed", customer };
    }
    const report = (await arRes.json()) as ReceivablesReport;

    // UC-ACC-001 §5.7 + BR-5: risk flag rules.
    const projectedBalance = report.currentBalance131 + invoiceTotal;
    const projectedCreditUsed = report.creditLimit > 0
      ? Math.round((projectedBalance / report.creditLimit) * 100)
      : 0;
    const flags: string[] = [];
    if (invoiceTotal > 500_000_000) flags.push("invoice_over_500m_requires_l2_approval");
    if (report.aging.over90 > 0) flags.push("has_overdue_90d");
    if (projectedBalance > report.creditLimit && report.creditLimit > 0) {
      flags.push("would_breach_credit_limit");
    } else if (projectedCreditUsed >= 80) {
      flags.push("credit_used_gte_80pct");
    }
    if (report.overdueAmount > 0) flags.push("has_overdue_amount");

    return {
      status: "found",
      customer,
      current_balance_131: report.currentBalance131,
      credit_limit: report.creditLimit,
      credit_used_percent: report.creditUsedPercent,
      projected_balance_131: projectedBalance,
      projected_credit_used_percent: projectedCreditUsed,
      aging: report.aging,
      overdue_amount: report.overdueAmount,
      last_payment_date: report.lastPaymentDate,
      payment_term_default: report.paymentTermDefault,
      open_invoices: report.openInvoices,
      risk_flags: flags,
      requires_l2_approval: flags.includes("invoice_over_500m_requires_l2_approval")
        || flags.includes("would_breach_credit_limit")
        || flags.includes("has_overdue_90d"),
    };
  } catch (err) {
    console.error(`[voucher] enrichWithCustomerAr(${buyerMst}) failed:`, err);
    return null;
  }
}

interface SalesOrderMatch {
  salesOrder: {
    orderNumber: string;
    orderDate: string;
    status: string;
    totalAmount: number;
    paymentTermsDays: number;
    salespersonCode: string | null;
    notes: string | null;
  };
  deliveryNote: {
    dnNumber: string;
    dnDate: string;
    warehouseFrom: string;
    deliveredBy: string | null;
    receivedBy: string | null;
    status: string;
  } | null;
  contract: {
    contractNumber: string;
    contractTitle: string;
    contractValue: number;
    milestoneNo: number;
    milestoneDesc: string;
    milestoneAmount: number;
    milestoneStatus: string;
  } | null;
  matchQuality: string;
}

async function enrichWith3WayMatch(
  token: string,
  documentNumber: string,
): Promise<Record<string, unknown> | null> {
  try {
    const res = await gateway.salesOrderByInvoice(token, documentNumber);
    if (!res.ok) {
      return { status: "no_match", document_number: documentNumber };
    }
    const match = (await res.json()) as SalesOrderMatch;
    return {
      status: "found",
      quality: match.matchQuality,
      sales_order: match.salesOrder,
      delivery_note: match.deliveryNote,
      contract: match.contract,
    };
  } catch (err) {
    console.error(`[voucher] enrichWith3WayMatch(${documentNumber}) failed:`, err);
    return null;
  }
}
