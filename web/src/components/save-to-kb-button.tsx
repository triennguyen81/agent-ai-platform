"use client";

import { useState } from "react";
import { BookmarkPlus, Check, Loader2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label, Textarea } from "@/components/ui/input";
import { MicButton } from "@/components/mic-button";

interface SaveToKbButtonProps {
  /** Pre-filled title for the modal — user can still edit before saving. */
  defaultTitle: string;
  /** Pre-filled content body (Markdown ok). */
  defaultContent: string;
  /** Comma-separated tag suggestions pre-filled into the tags input. */
  defaultTags?: string;
  /** Override the button label. */
  label?: string;
  /** "chip" = small pill (used in chat bubbles) · "button" = standard Button (pages). */
  variant?: "chip" | "button";
}

export function SaveToKbButton({
  defaultTitle,
  defaultContent,
  defaultTags,
  label = "Lưu vào kiến thức",
  variant = "button",
}: SaveToKbButtonProps) {
  const [open, setOpen] = useState(false);
  const [savedJustNow, setSavedJustNow] = useState(false);

  function handleSaved() {
    setOpen(false);
    setSavedJustNow(true);
    setTimeout(() => setSavedJustNow(false), 3000);
  }

  return (
    <>
      {variant === "chip" ? (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="flex items-center gap-1 rounded-full border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)] hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]"
        >
          {savedJustNow ? <Check className="size-3" /> : <BookmarkPlus className="size-3" />}
          {savedJustNow ? "Đã lưu" : label}
        </button>
      ) : (
        <Button
          type="button"
          size="sm"
          variant="outline"
          onClick={() => setOpen(true)}
          disabled={!defaultContent.trim()}
        >
          {savedJustNow ? <Check className="size-4" /> : <BookmarkPlus className="size-4" />}
          {savedJustNow ? "Đã lưu" : label}
        </Button>
      )}
      {open && (
        <SaveModal
          defaultTitle={defaultTitle}
          defaultContent={defaultContent}
          defaultTags={defaultTags ?? ""}
          onClose={() => setOpen(false)}
          onSaved={handleSaved}
        />
      )}
    </>
  );
}

interface SaveModalProps {
  defaultTitle: string;
  defaultContent: string;
  defaultTags: string;
  onClose: () => void;
  onSaved: () => void;
}

function SaveModal({
  defaultTitle,
  defaultContent,
  defaultTags,
  onClose,
  onSaved,
}: SaveModalProps) {
  const [title, setTitle] = useState(defaultTitle.slice(0, 300));
  const [content, setContent] = useState(defaultContent);
  const [tagsInput, setTagsInput] = useState(defaultTags);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [interim, setInterim] = useState("");

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (saving) return;
    setSaving(true);
    setError(null);

    const tags = tagsInput
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    try {
      const res = await fetch("/api/notes", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: title.trim(), content, tags }),
      });
      if (!res.ok) {
        const body = await res.text();
        setError(body.slice(0, 300) || `HTTP ${res.status}`);
        return;
      }
      onSaved();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <form
        onSubmit={submit}
        className="w-full max-w-lg space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 shadow-xl"
      >
        <div className="flex items-start justify-between">
          <div>
            <h3 className="font-serif text-lg">Lưu vào kiến thức</h3>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Note này sẽ được dùng cho các chat sau (RAG).
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
            aria-label="Close"
          >
            <X className="size-4" />
          </button>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="kb-title">Tiêu đề</Label>
          <Input
            id="kb-title"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            maxLength={300}
            required
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="kb-content">Nội dung (markdown ok)</Label>
          {interim && (
            <div className="rounded-[var(--radius)] border border-dashed border-[var(--color-primary)]/40 bg-[var(--color-primary)]/5 px-3 py-1.5 text-xs italic text-[var(--color-muted-fg)]">
              <span className="mr-2 text-[10px] uppercase tracking-wide text-[var(--color-primary)]">
                Đang nghe
              </span>
              {interim}…
            </div>
          )}
          <div className="relative">
            <Textarea
              id="kb-content"
              value={content}
              onChange={(e) => setContent(e.target.value)}
              rows={10}
            />
            <div className="absolute bottom-2 right-2">
              <MicButton
                onTranscript={(text) =>
                  setContent((prev) => (prev ? `${prev.trimEnd()} ${text}` : text))
                }
                onInterim={setInterim}
                size="sm"
              />
            </div>
          </div>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="kb-tags">Tags (cách nhau bằng dấu phẩy)</Label>
          <Input
            id="kb-tags"
            value={tagsInput}
            onChange={(e) => setTagsInput(e.target.value)}
            placeholder="kế toán, định khoản, 2026"
          />
        </div>
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="ghost" size="sm" onClick={onClose}>
            Huỷ
          </Button>
          <Button type="submit" size="sm" disabled={saving || !title.trim()}>
            {saving ? (
              <>
                <Loader2 className="mr-1 size-3.5 animate-spin" />
                Đang lưu…
              </>
            ) : (
              "Lưu note"
            )}
          </Button>
        </div>
      </form>
    </div>
  );
}
