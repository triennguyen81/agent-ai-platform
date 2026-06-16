"use client";

import { useState } from "react";
import { MicButton } from "@/components/mic-button";
import { Textarea } from "@/components/ui/input";

interface TextareaWithMicProps {
  id: string;
  name: string;
  rows?: number;
  placeholder?: string;
  defaultValue?: string;
}

export function TextareaWithMic({
  id,
  name,
  rows = 8,
  placeholder,
  defaultValue = "",
}: TextareaWithMicProps) {
  // Controlled so we can append voice transcript without losing what the
  // user typed by hand.
  const [value, setValue] = useState(defaultValue);
  const [interim, setInterim] = useState("");

  function appendTranscript(text: string) {
    setValue((prev) => (prev ? `${prev.trimEnd()} ${text}` : text));
  }

  return (
    <div className="space-y-2">
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
          id={id}
          name={name}
          rows={rows}
          placeholder={placeholder}
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
        <div className="absolute bottom-2 right-2">
          <MicButton
            onTranscript={appendTranscript}
            onInterim={setInterim}
            size="sm"
          />
        </div>
      </div>
    </div>
  );
}
