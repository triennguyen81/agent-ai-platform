import Link from "next/link";
import type { ReactNode } from "react";

export function AuthShell({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle: string;
  children: ReactNode;
}) {
  return (
    <main className="grid min-h-dvh grid-cols-1 lg:grid-cols-[1.05fr_1fr]">
      {/* Brand panel */}
      <aside className="relative hidden flex-col justify-between overflow-hidden bg-[var(--color-fg)] p-12 text-[var(--color-bg)] lg:flex">
        {/* Decorative gradient blobs */}
        <div className="pointer-events-none absolute -left-32 -top-32 size-96 rounded-full bg-[var(--color-primary)] opacity-30 blur-3xl" />
        <div className="pointer-events-none absolute -right-24 bottom-12 size-96 rounded-full bg-[var(--color-accent)] opacity-20 blur-3xl" />

        <Link href="/" className="relative z-10 inline-flex items-center gap-2">
          <span className="inline-block size-2 rounded-full bg-[var(--color-accent)]" />
          <span className="font-serif text-2xl tracking-tight">Cát Tường AI</span>
        </Link>

        <div className="relative z-10 space-y-8">
          <h2 className="font-serif text-4xl leading-tight tracking-tight">
            Trợ lý AI cá nhân,
            <br />
            <span className="text-[var(--color-accent)]">biết những gì bạn biết.</span>
          </h2>
          <p className="max-w-md text-base text-white/70">
            Lưu ghi chú, ý tưởng, dữ kiện. Hỏi bất cứ gì. Cát Tường AI nhớ những gì
            bạn viết và trả lời trong ngữ cảnh của riêng bạn.
          </p>

          <ul className="space-y-3 text-sm">
            {[
              ["Knowledge riêng tư", "Notes, snippets — chỉ của bạn"],
              ["Memory đa lượt", "Mỗi cuộc trò chuyện đều nhớ context"],
              ["Powered by Claude", "Claude Haiku 4.5 — nhanh, đa ngôn ngữ"],
            ].map(([t, b]) => (
              <li key={t} className="flex gap-3">
                <span className="mt-1.5 size-1.5 shrink-0 rounded-full bg-[var(--color-accent)]" />
                <div>
                  <p className="font-medium text-white">{t}</p>
                  <p className="text-white/60">{b}</p>
                </div>
              </li>
            ))}
          </ul>
        </div>

        <p className="relative z-10 text-xs text-white/40">
          © Cát Tường AI · {new Date().getFullYear()}
        </p>
      </aside>

      {/* Form panel */}
      <section className="grid place-items-center px-6 py-10">
        <div className="w-full max-w-sm space-y-8">
          {/* Mobile-only brand mark */}
          <Link href="/" className="flex items-center justify-center gap-2 lg:hidden">
            <span className="inline-block size-2 rounded-full bg-[var(--color-primary)]" />
            <span className="font-serif text-xl">Cát Tường AI</span>
          </Link>

          <header className="space-y-2">
            <h1 className="font-serif text-3xl tracking-tight">{title}</h1>
            <p className="text-sm text-[var(--color-muted-fg)]">{subtitle}</p>
          </header>

          {children}
        </div>
      </section>
    </main>
  );
}
