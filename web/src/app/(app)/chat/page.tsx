import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { ChatRoom } from "@/components/chat-room";

export default async function ChatPage({ searchParams }: { searchParams: Promise<{ agent?: string }> }) {
  const viewer = await requireViewer();
  const { agent } = await searchParams;
  if (viewer.roles.length === 1 && viewer.roles.includes("user")) {
    if (!agent) redirect("/my-agents?error=select-agent");
    const response = await gateway.listMyAgents(viewer.token);
    const body = response.ok ? await response.json() as { items?: Array<{ agentId: string; lifecycleState: string; agentDisabled: boolean }> } : {};
    const allowed = body.items?.some((item) => item.agentId === agent && item.lifecycleState === "production" && !item.agentDisabled);
    if (!allowed) redirect("/forbidden");
  }
  // thread_id stable per browser session via localStorage on the client.
  return <ChatRoom />;
}
