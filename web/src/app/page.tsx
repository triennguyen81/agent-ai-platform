import Link from "next/link";
import { redirect } from "next/navigation";
import { getSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { landingPathFor } from "@/lib/default-agent";
import { Button } from "@/components/ui/button";

export default async function HomePage() {
  const session = await getSession();
  if (session) {
    const viewer = await requireViewer();
    redirect(landingPathFor(viewer.roles));
  }

  return (
    <main className="min-h-dvh">
      <header className="mx-auto flex max-w-5xl items-center justify-between px-6 py-6">
        <span className="font-serif text-xl">Cát Tường AI</span>
        <nav className="flex items-center gap-3">
          <Button variant="ghost" size="sm" asChild>
            <Link href="/login">Sign in</Link>
          </Button>
          <Button size="sm" asChild>
            <Link href="/signup">Get started</Link>
          </Button>
        </nav>
      </header>

      <section className="mx-auto flex max-w-3xl flex-col items-center gap-6 px-6 pt-24 pb-16 text-center">
        <h1 className="font-serif text-5xl leading-tight tracking-tight sm:text-6xl">
          Your AI assistant that knows
          <br />
          <span className="text-[var(--color-primary)]">what you know.</span>
        </h1>
        <p className="max-w-xl text-base text-[var(--color-muted-fg)] sm:text-lg">
          Drop your notes, ideas, references. Ask anything. Cát Tường AI
          remembers what you wrote and answers in your own context.
        </p>
        <div className="flex gap-3">
          <Button size="lg" asChild>
            <Link href="/signup">Create free account</Link>
          </Button>
          <Button size="lg" variant="outline" asChild>
            <Link href="/login">Sign in</Link>
          </Button>
        </div>
      </section>

      <section className="mx-auto grid max-w-5xl gap-6 px-6 pb-24 sm:grid-cols-3">
        {[
          {
            title: "Personal knowledge",
            body: "Notes, snippets, facts. All yours, private to your account.",
          },
          {
            title: "Conversational memory",
            body: "Multi-turn threads. The model remembers what you said earlier.",
          },
          {
            title: "Powered by Claude",
            body: "Anthropic Claude Haiku 4.5 by default — fast, capable, multilingual.",
          },
        ].map((card) => (
          <article
            key={card.title}
            className="rounded-[var(--radius)] border bg-[var(--color-muted)] p-6"
          >
            <h2 className="font-serif text-xl">{card.title}</h2>
            <p className="mt-2 text-sm text-[var(--color-muted-fg)]">{card.body}</p>
          </article>
        ))}
      </section>
    </main>
  );
}
