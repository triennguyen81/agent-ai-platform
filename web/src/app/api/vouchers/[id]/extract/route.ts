import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

/**
 * Manual extraction — user clicks the "Trích xuất" button on the detail
 * page. Was previously running agent.runTask synchronously (~30-90s) which
 * hit Cloudflare's 100s edge budget and returned 504.
 *
 * Now: proxy to gateway /vouchers/{id}/reextract which enqueues the same
 * Hangfire VoucherExtractJobRunner. Returns in <2s. Detail page polls +
 * auto-refreshes until status='extracted'.
 *
 * Optional rawText body: if user pasted their own invoice text (fallback
 * when OCR is unavailable), gateway stores it as raw_ocr_text so the
 * extract job reads it from DB.
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

  if (rawText.length > 0 && rawText.length < 20) {
    return NextResponse.json(
      { error: "rawText quá ngắn (cần ≥ 20 ký tự)" },
      { status: 400 },
    );
  }

  const gatewayRes = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${voucherId}/reextract`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ rawText: rawText || null, voucherType }),
      cache: "no-store",
    },
  );

  if (!gatewayRes.ok) {
    const errText = await gatewayRes.text();
    return new NextResponse(errText, { status: gatewayRes.status });
  }

  const gwJson = (await gatewayRes.json()) as {
    voucherId: string;
    status: string;
    extractJobId?: string;
  };

  return NextResponse.json({
    voucherId: gwJson.voucherId,
    status: gwJson.status,
    agentResponse: `Đã đưa yêu cầu vào hàng đợi (job ${gwJson.extractJobId ?? "?"}). Trang sẽ tự refresh trong 30-60s.`,
  });
}
