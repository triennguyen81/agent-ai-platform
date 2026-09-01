import Link from "next/link";
import { notFound } from "next/navigation";
import { ArrowLeft } from "lucide-react";
import { ARTIFACTS, getArtifact } from "@/data/artifacts-manifest";
import { requireSession } from "@/lib/auth";
import { ArtifactWalkthrough } from "@/components/artifact-walkthrough";

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function ArtifactDetailPage({ params }: PageProps) {
  const { id } = await params;
  await requireSession();
  const artifact = getArtifact(id);
  if (!artifact) notFound();

  // Compute next artifact id for "Học mẫu tiếp" navigation.
  const currentIdx = ARTIFACTS.findIndex((a) => a.id === id);
  const nextArtifactId =
    currentIdx >= 0 && currentIdx < ARTIFACTS.length - 1
      ? ARTIFACTS[currentIdx + 1].id
      : null;

  return (
    <div className="space-y-4">
      <div>
        <Link
          href="/mau-chung-tu"
          className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        >
          <ArrowLeft className="size-3.5" /> Về Chứng từ mẫu
        </Link>
      </div>

      <header className="space-y-1">
        <h1 className="font-serif text-2xl">{artifact.title}</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">{artifact.description}</p>
      </header>

      {/* Layout: image left (sticky), walkthrough right */}
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)]">
        <aside className="space-y-2 lg:sticky lg:top-4 lg:self-start">
          <div className="overflow-hidden rounded-[var(--radius)] border bg-white shadow-sm">
            <img
              src={`/demo-templates/${artifact.filename}`}
              alt={artifact.title}
              className="w-full"
            />
          </div>
          <a
            href={`/demo-templates/${artifact.filename}`}
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-1 text-xs text-[var(--color-primary)] hover:underline"
          >
            Mở file gốc trong tab mới ↗
          </a>
        </aside>

        <div>
          <ArtifactWalkthrough artifact={artifact} nextArtifactId={nextArtifactId} />
        </div>
      </div>
    </div>
  );
}
