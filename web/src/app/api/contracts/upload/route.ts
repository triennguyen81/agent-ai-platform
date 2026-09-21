import { NextResponse, type NextRequest } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

interface UploadBody {
  raw_text?: string;
  title_hint?: string;
  // Set when the text came from the file-upload path (contracts/upload's
  // extract-text step) — already-uploaded MinIO object key for the source
  // file. save_contract (agentic/contract_tools.py) already reads
  // fields.source_pdf_path and forwards it as ContractRecord.SourcePdfPath;
  // this was simply never populated by any caller until now.
  source_pdf_path?: string;
  original_filename?: string;
  // "Xử lý bằng trợ lý" selector (auto-selected when exactly one Agent is
  // eligible, user-chosen when more than one is) — the id itself is never
  // trusted here or by the agent service; the Gateway re-validates it
  // against a freshly computed eligible set before ever honoring it.
  preferred_agent_id?: string;
}

/**
 * Contract intake — final step, always text-based:
 *   - User has raw contract text in the textarea, either typed/pasted
 *     directly, or populated by the separate /api/contracts/extract-text
 *     step (file upload → PDF text-layer/OCR/DOCX extraction) and possibly
 *     hand-edited afterward — this route never knows or cares which.
 *   - Web hands the text to /agent/run with a task to extract fields and
 *     save via the `save_contract` tool
 *   - Agent runs the contract-extractor skill, calls save_contract → POST
 *     /contracts on gateway → returns final summary
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
  const sourcePdfPath = body?.source_pdf_path?.trim();
  const task = [
    "Bạn là chuyên viên pháp chế. Thực hiện 2 bước:",
    "",
    "**Bước 1** — Áp dụng skill `contract-extractor` để trích xuất JSON từ raw text bên dưới.",
    `${titleHint ? `User gợi ý title: "${titleHint}"\n` : ""}`,
    "**Bước 2** — Gọi tool `save_contract` với fields = JSON đã extract + raw_text = nguyên văn raw text bên dưới. Đảm bảo party_a, party_b, ngày tháng, total_value đầy đủ nhất có thể.",
    sourcePdfPath
      ? `Nội dung này được trích xuất từ file đã upload — thêm đúng nguyên văn key "source_pdf_path": "${sourcePdfPath}" vào object fields khi gọi save_contract (copy chính xác, không sửa đổi chuỗi này).`
      : "",
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
  // preferred_agent_id is the "Xử lý bằng trợ lý" selector's pick when more
  // than one Agent is eligible — see contracts/upload/page.tsx.
  const res = await agent.runTask(token, {
    task,
    capability: "contract.processing",
    preferred_agent_id: body?.preferred_agent_id?.trim() || undefined,
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
