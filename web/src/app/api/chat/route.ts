import { NextResponse, type NextRequest } from "next/server";
import { agent, gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

interface NoteRecord {
  id: string;
  title: string;
  content: string;
  tags?: string[];
}

interface NotesResponse {
  items?: NoteRecord[];
}

// Cap total knowledge size — Claude Haiku handles a lot but no need to
// ship megabytes on every turn.
const MAX_NOTES_CHARS = 60_000;

function buildKnowledgeBlock(notes: NoteRecord[]): string {
  if (notes.length === 0) return "";

  const formatted = notes
    .map((n, i) => {
      const tags = (n.tags ?? []).filter(Boolean).join(", ");
      const header = `### Note ${i + 1}: ${n.title}` +
        (tags ? `  \n_Tags: ${tags}_` : "");
      return `${header}\n\n${n.content.trim()}`;
    })
    .join("\n\n---\n\n");

  return formatted.length > MAX_NOTES_CHARS
    ? formatted.slice(0, MAX_NOTES_CHARS) + "\n\n_(knowledge truncated)_"
    : formatted;
}

export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const body = await request.json();
  const prompt = String(body?.prompt ?? "").trim();
  if (!prompt) {
    return NextResponse.json({ error: "missing prompt" }, { status: 400 });
  }

  // Pull the user's saved notes from Gateway. Failure here is non-fatal —
  // we still fall back to a vanilla chat without RAG.
  let knowledgeBlock = "";
  try {
    const notesRes = await gateway.listNotes(token);
    if (notesRes.ok) {
      const payload = (await notesRes.json()) as NotesResponse;
      knowledgeBlock = buildKnowledgeBlock(payload.items ?? []);
    }
  } catch {
    // swallow — RAG is optional, never fail the chat over a notes outage
  }

  // Prepend the knowledge as a framing instruction so the PEC orchestrator
  // sees it in every stage (planner, executor, critic).
  const augmentedPrompt = knowledgeBlock
    ? [
        "Bạn là trợ lý AI cá nhân. Dưới đây là toàn bộ ghi chú (knowledge base) mà người dùng đã lưu — luôn ưu tiên thông tin từ đây khi câu hỏi liên quan.",
        "",
        "===== USER KNOWLEDGE BASE =====",
        knowledgeBlock,
        "===== END KNOWLEDGE BASE =====",
        "",
        "Câu hỏi của người dùng:",
        prompt,
        "",
        "Trả lời ngắn gọn, trực tiếp, trích dẫn tên Note nếu dùng thông tin từ đó. Nếu câu hỏi không liên quan đến knowledge, trả lời bình thường.",
      ].join("\n")
    : prompt;

  const res = await agent.chat({
    prompt: augmentedPrompt,
    thread_id: body.thread_id,
    temperature: body.temperature ?? 0.2,
  });

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
