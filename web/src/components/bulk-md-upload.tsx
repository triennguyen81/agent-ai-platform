"use client";

import { useCallback, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { FileUp, Loader2, X } from "lucide-react";
import { Button } from "@/components/ui/button";

interface ParsedNote {
  fileName: string;
  title: string;
  content: string;
  tags: string[];
}

interface UploadOutcome {
  fileName: string;
  ok: boolean;
  error?: string;
}

const FRONTMATTER_RE = /^---\s*\n([\s\S]*?)\n---\s*\n/;

function parseMarkdown(fileName: string, raw: string): ParsedNote {
  let title = fileName.replace(/\.(md|markdown)$/i, "");
  let tags: string[] = [];
  let content = raw;

  const fm = raw.match(FRONTMATTER_RE);
  if (fm) {
    content = raw.slice(fm[0].length);
    for (const line of fm[1].split("\n")) {
      const [k, ...rest] = line.split(":");
      if (!k || rest.length === 0) continue;
      const key = k.trim().toLowerCase();
      const value = rest.join(":").trim().replace(/^["']|["']$/g, "");
      if (key === "title" && value) title = value;
      if (key === "tags" && value) {
        tags = value
          .replace(/^\[|\]$/g, "")
          .split(/[,;]/)
          .map((t) => t.trim().replace(/^["']|["']$/g, ""))
          .filter(Boolean);
      }
    }
  }

  if (!fm) {
    const headingMatch = content.match(/^#\s+(.+)$/m);
    if (headingMatch) title = headingMatch[1].trim();
  }

  return { fileName, title: title.slice(0, 300), content, tags };
}

export function BulkMarkdownUpload() {
  const router = useRouter();
  const inputRef = useRef<HTMLInputElement>(null);
  const [parsed, setParsed] = useState<ParsedNote[]>([]);
  const [uploading, setUploading] = useState(false);
  const [results, setResults] = useState<UploadOutcome[]>([]);

  const pickFiles = useCallback(async (files: FileList | null) => {
    if (!files || files.length === 0) return;
    const next: ParsedNote[] = [];
    for (const file of Array.from(files)) {
      if (!/\.(md|markdown)$/i.test(file.name)) continue;
      const raw = await file.text();
      next.push(parseMarkdown(file.name, raw));
    }
    setParsed((prev) => [...prev, ...next]);
    setResults([]);
  }, []);

  function removeAt(index: number) {
    setParsed((prev) => prev.filter((_, i) => i !== index));
  }

  async function upload() {
    if (parsed.length === 0 || uploading) return;
    setUploading(true);
    const outcomes: UploadOutcome[] = [];

    for (const note of parsed) {
      try {
        const res = await fetch("/api/notes", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            title: note.title,
            content: note.content,
            tags: note.tags,
          }),
        });
        if (!res.ok) {
          const text = await res.text();
          outcomes.push({ fileName: note.fileName, ok: false, error: text.slice(0, 200) });
        } else {
          outcomes.push({ fileName: note.fileName, ok: true });
        }
      } catch (err) {
        const message = err instanceof Error ? err.message : "Network error";
        outcomes.push({ fileName: note.fileName, ok: false, error: message });
      }
    }

    setResults(outcomes);
    setUploading(false);
    const allOk = outcomes.every((o) => o.ok);
    if (allOk) {
      setParsed([]);
      router.refresh();
    } else {
      // keep failed entries so admin can retry
      const failedNames = new Set(outcomes.filter((o) => !o.ok).map((o) => o.fileName));
      setParsed((prev) => prev.filter((p) => failedNames.has(p.fileName)));
      router.refresh();
    }
  }

  return (
    <div className="space-y-3 rounded-[var(--radius)] border border-dashed bg-[var(--color-muted)] p-4">
      <div>
        <h3 className="font-serif text-base">Bulk upload Markdown</h3>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Chọn nhiều file <code>.md</code> cùng lúc. Hệ thống đọc frontmatter
          <code> title</code>/<code>tags</code> nếu có, không thì lấy tên file
          làm tiêu đề.
        </p>
      </div>

      <input
        ref={inputRef}
        type="file"
        accept=".md,.markdown,text/markdown"
        multiple
        className="hidden"
        onChange={(e) => {
          void pickFiles(e.target.files);
          if (inputRef.current) inputRef.current.value = "";
        }}
      />

      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => inputRef.current?.click()}
        disabled={uploading}
      >
        <FileUp className="mr-1 size-4" />
        Chọn file…
      </Button>

      {parsed.length > 0 && (
        <ul className="space-y-1 rounded-[var(--radius)] border bg-[var(--color-bg)] p-2 text-xs">
          {parsed.map((n, i) => (
            <li key={`${n.fileName}-${i}`} className="flex items-center justify-between gap-2">
              <div className="min-w-0 flex-1">
                <div className="truncate font-medium">{n.title}</div>
                <div className="truncate text-[10px] text-[var(--color-muted-fg)]">
                  {n.fileName} · {n.content.length} chars
                  {n.tags.length > 0 && ` · ${n.tags.join(", ")}`}
                </div>
              </div>
              <button
                type="button"
                onClick={() => removeAt(i)}
                className="text-[var(--color-muted-fg)] hover:text-red-600"
                aria-label="Remove"
              >
                <X className="size-3.5" />
              </button>
            </li>
          ))}
        </ul>
      )}

      {results.length > 0 && (
        <ul className="space-y-1 text-xs">
          {results.map((r) => (
            <li
              key={r.fileName}
              className={r.ok ? "text-emerald-700" : "text-red-700"}
            >
              {r.ok ? "✓" : "✗"} {r.fileName}
              {r.error && ` — ${r.error}`}
            </li>
          ))}
        </ul>
      )}

      {parsed.length > 0 && (
        <Button
          type="button"
          size="sm"
          onClick={upload}
          disabled={uploading}
          className="w-full"
        >
          {uploading ? (
            <>
              <Loader2 className="mr-1 size-4 animate-spin" />
              Đang upload {parsed.length}…
            </>
          ) : (
            <>Upload {parsed.length} note{parsed.length > 1 ? "s" : ""}</>
          )}
        </Button>
      )}
    </div>
  );
}
