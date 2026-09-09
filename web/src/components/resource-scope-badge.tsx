import { Building2, Globe2 } from "lucide-react";

export function ResourceScopeBadge({ scope }: { scope: string }) {
  const global = scope === "platform" || scope === "global" || scope === "platform_template";
  const Icon = global ? Globe2 : Building2;
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-[var(--color-muted)] px-2 py-0.5 text-[10px] font-medium text-[var(--color-muted-fg)]">
      <Icon className="size-3" /> {global ? "Hệ thống" : "Phòng ban"}
    </span>
  );
}
