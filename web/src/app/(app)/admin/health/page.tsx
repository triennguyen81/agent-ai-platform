import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { HealthDashboard } from "@/components/health-dashboard";

export default async function HealthPage() {
  const token = await requireSession();
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    if (!(me.roles ?? []).includes("super_admin")) {
      redirect("/chat");
    }
  }

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">System health</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Trạng thái các service backend. Refresh tự động mỗi 30 giây. Chỉ
          super_admin xem được.
        </p>
      </header>

      <HealthDashboard />
    </div>
  );
}
