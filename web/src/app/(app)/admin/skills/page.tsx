import { redirect } from "next/navigation";
import { Brain, Globe, KeyRound, Zap } from "lucide-react";
import { agent, gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

interface SkillSummary {
  name: string;
  description: string;
  keywords: string[];
  always_on: boolean;
  requires_knowledge_context: boolean;
}

export default async function AdminSkillsPage() {
  const token = await requireSession();
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    if (!(me.roles ?? []).includes("super_admin")) {
      redirect("/chat");
    }
  }

  const skillsRes = await agent.listSkills();
  const skills: SkillSummary[] = skillsRes.ok ? await skillsRes.json() : [];

  // Sort: always_on first, then by name
  const sorted = [...skills].sort((a, b) => {
    if (a.always_on !== b.always_on) return a.always_on ? -1 : 1;
    return a.name.localeCompare(b.name);
  });

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Skills</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Tổng {skills.length} skill đang active. Skill loader tự chọn skill phù
          hợp theo <code>always_on</code> / <code>when</code> / <code>keywords</code>.
          Xem hướng dẫn viết skill mới tại{" "}
          <code className="text-[var(--color-primary)]">docs/skills_authoring.md</code>.
        </p>
      </header>

      {sorted.length === 0 ? (
        <p className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
          Không có skill nào — kiểm tra thư mục <code>agent/skills/</code>.
        </p>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {sorted.map((skill) => (
            <SkillCard key={skill.name} skill={skill} />
          ))}
        </div>
      )}

      <section className="rounded-[var(--radius)] border border-dashed bg-[var(--color-muted)] p-4 text-sm">
        <p className="font-medium">Quy tắc kích hoạt skill</p>
        <ul className="mt-1 space-y-1 text-[var(--color-muted-fg)]">
          <li>
            <Zap className="mr-1 inline size-3.5 text-[var(--color-primary)]" />
            <code>always_on: true</code> → mọi turn đều active
          </li>
          <li>
            <Globe className="mr-1 inline size-3.5 text-[var(--color-accent)]" />
            <code>when: [has_knowledge_context]</code> → chỉ active khi turn có
            khối KB
          </li>
          <li>
            <KeyRound className="mr-1 inline size-3.5 text-[var(--color-muted-fg)]" />
            <code>keywords</code> → substring match trên user prompt (case-insensitive)
          </li>
        </ul>
      </section>
    </div>
  );
}

function SkillCard({ skill }: { skill: SkillSummary }) {
  const badge = skill.always_on
    ? { label: "Always on", className: "bg-emerald-100 text-emerald-700", Icon: Zap }
    : skill.requires_knowledge_context
    ? { label: "Needs KB", className: "bg-blue-100 text-blue-700", Icon: Globe }
    : skill.keywords.length > 0
    ? {
        label: `${skill.keywords.length} keywords`,
        className: "bg-amber-100 text-amber-700",
        Icon: KeyRound,
      }
    : { label: "Inactive", className: "bg-gray-100 text-gray-600", Icon: Brain };

  const BadgeIcon = badge.Icon;

  return (
    <div className="flex flex-col gap-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-4">
      <div className="flex items-start justify-between gap-2">
        <h2 className="font-serif text-lg leading-tight">{skill.name}</h2>
        <span
          className={`inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-xs ${badge.className}`}
        >
          <BadgeIcon className="size-3" />
          {badge.label}
        </span>
      </div>

      {skill.description && (
        <p className="line-clamp-3 text-xs text-[var(--color-muted-fg)]">
          {skill.description}
        </p>
      )}

      {skill.keywords.length > 0 && (
        <details>
          <summary className="cursor-pointer text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]">
            Xem {skill.keywords.length} keywords
          </summary>
          <div className="mt-2 flex flex-wrap gap-1">
            {skill.keywords.map((kw) => (
              <span
                key={kw}
                className="rounded-full border px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]"
              >
                {kw}
              </span>
            ))}
          </div>
        </details>
      )}
    </div>
  );
}
