import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Trigger agentic workflow: read contract → apply contract-checklist-builder
 * skill → call save_checklist_items tool to persist.
 *
 * The agent loop does the heavy lifting; this endpoint just builds the
 * task prompt and forwards.
 */
export async function POST(
  _request: Request,
  context: { params: Promise<{ id: string }> },
) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id } = await context.params;

  const task = [
    "Bạn là project manager kế toán. Thực hiện 3 bước cho contract id sau:",
    "",
    `Contract ID: \`${id}\``,
    "",
    "**Bước 1** — Gọi tool `get_contract` với contract_id ở trên. Lấy về toàn bộ clauses + metadata (signed_date, effective_date, end_date).",
    "",
    "**Bước 2** — Áp dụng skill `contract-checklist-builder` để sinh JSON array các checklist items. Lưu ý:",
    "- Mỗi clause có deadline / penalty / thanh toán định kỳ / báo cáo định kỳ → 1+ items",
    "- Sinh tối đa 20 items quan trọng nhất",
    "- Mỗi item phải có clause_ordinal khớp với clause gốc",
    "",
    "**Bước 3** — Gọi tool `save_checklist_items` với contract_id ở trên, items = JSON array vừa sinh, replace_existing=true.",
    "",
    "Cuối cùng, trả 1 đoạn tổng kết ngắn cho user:",
    "- Số items đã sinh",
    "- Phân loại (vd '12 thanh toán + 4 báo cáo + 2 cảnh báo')",
    "- Item nào có due date gần nhất (trong 30 ngày)",
  ].join("\n");

  const res = await agent.runTask(token, {
    task,
    skills: ["contract-checklist-builder", "vietnamese-style"],
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
