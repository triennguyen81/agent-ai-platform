import { NextResponse, type NextRequest } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

interface UploadBody {
  raw_text?: string;
  title_hint?: string;
}

/**
 * Phase 1 upload flow:
 *   - User pastes raw contract text (after OCR or copy/paste from PDF)
 *   - Web hands the text to /agent/run with a task to extract fields and
 *     save via the `save_contract` tool
 *   - Agent runs the contract-extractor skill, calls save_contract → POST
 *     /contracts on gateway → returns final summary
 *
 * Phase 1.5 will add a binary PDF dropzone that pipes through agent's
 * /ocr/pdf first, then this same task.
 */
export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const body = (await request.json()) as UploadBody;
  const rawText = String(body?.raw_text ?? "").trim();
  if (!rawText || rawText.length < 50) {
    return NextResponse.json(
      { error: "raw_text quá ngắn (cần ≥ 50 ký tự)" },
      { status: 400 },
    );
  }

  const titleHint = body?.title_hint?.trim();
  const task = [
    "Bạn là chuyên viên pháp chế. Thực hiện 2 bước:",
    "",
    "**Bước 1** — Áp dụng skill `contract-extractor` để trích xuất JSON từ raw text bên dưới.",
    `${titleHint ? `User gợi ý title: "${titleHint}"\n` : ""}`,
    "**Bước 2** — Gọi tool `save_contract` với fields = JSON đã extract + raw_text = nguyên văn raw text bên dưới. Đảm bảo party_a, party_b, ngày tháng, total_value đầy đủ nhất có thể.",
    "",
    "Sau khi save xong, trả về 1 đoạn tổng kết ngắn cho user về:",
    "- ID hợp đồng đã lưu",
    "- Số clauses extract được",
    "- 2-3 điểm cần user review (vd ngày bị thiếu, giá trị cần xác nhận)",
    "",
    "===== RAW TEXT HỢP ĐỒNG =====",
    rawText.slice(0, 50_000),
    "===== END =====",
  ].join("\n");

  // D6: routes through the capability resolver (contract.processing)
  // instead of the legacy no-agent-id path — skills/tools now come from
  // whatever agent the resolver authorizes, not this request.
  const res = await agent.runTask(token, {
    task,
    capability: "contract.processing",
    max_turns: 6,
  });

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
