import { NextResponse, type NextRequest } from "next/server";
import { agent, gateway } from "@/lib/api";
import { decodeUserId, getSession } from "@/lib/auth";

function namespacedThreadId(token: string, raw: string | undefined): string | undefined {
  if (!raw) return undefined;
  const userId = decodeUserId(token);
  if (!userId) return raw;
  return raw.startsWith(`${userId}:`) ? raw : `${userId}:${raw}`;
}

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

  // Send the user's original question as `prompt` so the agent stores it
  // unchanged in memory (used for the sidebar thread title). The knowledge
  // block goes via the side-channel `knowledge_context` field — the agent
  // merges it into its system prompt only.
  const res = await agent.chat({
    prompt,
    knowledge_context: knowledgeBlock || undefined,
    thread_id: namespacedThreadId(token, body.thread_id),
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
