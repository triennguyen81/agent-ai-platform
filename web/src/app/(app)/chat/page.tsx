import { requireSession } from "@/lib/auth";
import { ChatRoom } from "@/components/chat-room";

export default async function ChatPage() {
  await requireSession();
  // thread_id stable per browser session via localStorage on the client.
  return <ChatRoom />;
}
