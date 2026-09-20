import { KnowledgeBases } from "@/components/knowledge-bases";
import { requireViewer } from "@/lib/authorization";

export default async function DepartmentKnowledgePage() {
  await requireViewer(["admin"]);

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Knowledge</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Chọn Knowledge Base để thêm tài liệu, theo dõi các phiên bản và gắn kiến thức vào Agent.
        </p>
      </header>
      <KnowledgeBases />
    </div>
  );
}
