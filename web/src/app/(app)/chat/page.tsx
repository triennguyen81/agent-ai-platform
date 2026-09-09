import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { ChatRoom } from "@/components/chat-room";

export default async function ChatPage({ searchParams }: { searchParams: Promise<{ agent?: string }> }) {
  const viewer = await requireViewer(["admin", "user"]);
  const { agent } = await searchParams;
  // D3: both a plain USER (direct assignment chain) and an ADMIN (department
  // entitlement chain) must pick a specific, actually-entitled agent before
  // reaching the chat UI — /me/agents already branches on role server-side
  // (see AgentEndpoints.MyAgentsAsync), so the same gate works for both.
  const isDepartmentMember = viewer.roles.includes("user") || viewer.roles.includes("admin");
  if (isDepartmentMember) {
    if (!agent) redirect("/my-agents?error=select-agent");
    const response = await gateway.listMyAgents(viewer.token);
    const body = response.ok ? await response.json() as { items?: Array<{ agentId: string; lifecycleState: string; agentDisabled: boolean }> } : {};
    const allowed = body.items?.some((item) => item.agentId === agent && item.lifecycleState === "production" && !item.agentDisabled);
    if (!allowed) redirect("/forbidden");
  }
  // thread_id stable per browser session via localStorage on the client.
  return <ChatRoom />;
}
