import { NextResponse, type NextRequest } from "next/server";
import { agent, gateway } from "@/lib/api";
import { decodeUserId, getSession, namespacedThreadId } from "@/lib/auth";

interface NoteRecord {
  id: string;
  title: string;
  content: string;
  tags?: string[];
}

interface NotesResponse {
  items?: NoteRecord[];
}

// Cloudflare edge has a hard 100s timeout on the free plan. Claude subscription
// CLI cold-start alone is ~20s; each extra 1000 chars of context adds ~2-3s of
// LLM latency. Cap aggressively so realistic RAG-heavy chats finish under 60s.
const MAX_NOTES_CHARS = 12_000;
const MAX_CHARS_PER_NOTE = 2_500;

function buildKnowledgeBlock(notes: NoteRecord[]): string {
  if (notes.length === 0) return "";

  const formatted = notes
    .map((n, i) => {
      const tags = (n.tags ?? []).filter(Boolean).join(", ");
      const header = `### Note ${i + 1}: ${n.title}` +
        (tags ? `  \n_Tags: ${tags}_` : "");
      const body = n.content.trim();
      const truncated =
        body.length > MAX_CHARS_PER_NOTE
          ? body.slice(0, MAX_CHARS_PER_NOTE) + "\n\n_(nội dung đã được cắt ngắn)_"
          : body;
      return `${header}\n\n${truncated}`;
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

  // Optional agent scope.
  const agentId = typeof body.agent_id === "string" && body.agent_id.length > 0
    ? body.agent_id
    : null;

  // Hardening phase (docs/ACCOUNTING_AGENT_AUDIT.md, F-02): when agentId is
  // present, the Python /chat handler resolves a signed ExecutionManifest
  // and — by explicit design (agent/src/aiq_agent/api/routes.py: "All
  // client runtime knobs and client knowledge are intentionally ignored
  // here. The signed Gateway manifest is the sole authority.") — discards
  // any `knowledge_context` this route sends. That manifest-driven path
  // builds its own knowledge context from the agent's real, tenant-isolated
  // Knowledge Base (agent/src/aiq_agent/knowledge/service.py retrieve_
  // context), which is the ONE knowledge retrieval path for agent-bound
  // chat. So for an agent-bound request this route must not fetch/rank/
  // send the legacy per-note "knowledge_context" at all — doing so was
  // previously silent, wasted work with zero effect on the answer, which
  // could mislead an admin who attached a note via the legacy per-agent
  // "Attach Knowledge" UI into believing it does something for real chat.
  // The legacy per-note path stays fully live for the ONE case where the
  // agent actually reads knowledge_context: plain chat with no agentId
  // (admin/super_admin direct chat, gated server-side — see require_
  // principal's is_user_only check).
  let knowledgeBlock = "";
  if (!agentId) {
    // Legacy path: caller's own notes (admin+ direct chat, no agent_id).
    let allNotes: NoteRecord[] = [];
    try {
      const notesRes = await gateway.listNotes(token);
      if (notesRes.ok) {
        const payload = (await notesRes.json()) as NotesResponse;
        allNotes = payload.items ?? [];
      }
    } catch {
      // swallow — RAG is optional, never fail the chat over a notes outage
    }

    // M6: ask the agent's embedding service for top-K relevant notes only.
    // Falls back to full-context if the service is disabled or fails.
    let topK: NoteRecord[] | null = null;
    if (allNotes.length > 0) {
      const userId = decodeUserId(token);
      if (userId) {
        try {
          const embRes = await agent.queryEmbeddings(token, {
            query: prompt,
            user_id: userId,
            notes: allNotes.map((n) => ({
              id: n.id,
              title: n.title,
              content: n.content,
            })),
            k: 3,
          });
          if (embRes.ok) {
            const embBody = (await embRes.json()) as {
              top_k: { note_id: string }[];
              enabled: boolean;
            };
            if (embBody.enabled && embBody.top_k.length > 0) {
              const byId = new Map(allNotes.map((n) => [n.id, n]));
              topK = embBody.top_k
                .map((hit) => byId.get(hit.note_id))
                .filter((n): n is NoteRecord => Boolean(n));
            }
          }
        } catch {
          // swallow — fall through to full-context
        }
      }
    }

    const notesForContext = topK ?? allNotes;
    if (notesForContext.length > 0) {
      knowledgeBlock = buildKnowledgeBlock(notesForContext);
    }
  }

  // Send the user's original question as `prompt` so the agent stores it
  // unchanged in memory (used for the sidebar thread title). The knowledge
  // block goes via the side-channel `knowledge_context` field — read only
  // by the no-agentId plain chat path (see comment above).
  const res = await agent.chat(token, {
    prompt,
    knowledge_context: knowledgeBlock || undefined,
    thread_id: namespacedThreadId(token, body.thread_id),
    temperature: body.temperature ?? 0.2,
    agent_id: agentId ?? undefined,
  });

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
