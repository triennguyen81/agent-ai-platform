"use client";

import { useEffect } from "react";
import { Mic, MicOff } from "lucide-react";
import { useSpeechRecognition } from "@/hooks/use-speech-recognition";

interface MicButtonProps {
  onTranscript: (text: string) => void;
  /** When provided, the parent renders interim text itself instead of the
   *  tiny floating tooltip — useful when there's room for a full-width
   *  preview right above the input field. */
  onInterim?: (text: string) => void;
  onListeningChange?: (listening: boolean) => void;
  disabled?: boolean;
  size?: "sm" | "md";
  title?: string;
}

export function MicButton({
  onTranscript,
  onInterim,
  onListeningChange,
  disabled = false,
  size = "md",
  title,
}: MicButtonProps) {
  const { supported, listening, interimText, error, start, stop } =
    useSpeechRecognition({
      onFinalTranscript: (text) => {
        if (text) onTranscript(text);
      },
    });

  // Bubble interim + listening state up so parents can render their own UI.
  useEffect(() => {
    onInterim?.(listening ? interimText : "");
  }, [interimText, listening, onInterim]);

  useEffect(() => {
    onListeningChange?.(listening);
  }, [listening, onListeningChange]);

  if (!supported) {
    // Firefox or older browsers — render nothing rather than a dead button.
    return null;
  }

  const sizeClass = size === "sm" ? "size-7" : "size-9";
  const iconSize = size === "sm" ? "size-3.5" : "size-4";

  const tooltip =
    title ?? (listening ? "Nhấn để dừng" : "Nhập bằng giọng nói (vi-VN)");

  const hasParentPreview = Boolean(onInterim);

  return (
    <div className="relative inline-flex">
      <button
        type="button"
        onClick={listening ? stop : start}
        disabled={disabled}
        title={tooltip}
        aria-label={tooltip}
        aria-pressed={listening}
        className={`${sizeClass} inline-flex items-center justify-center rounded-full border transition disabled:cursor-not-allowed disabled:opacity-50 ${
          listening
            ? "animate-pulse border-red-500 bg-red-500 text-white"
            : "border-[var(--color-border)] bg-[var(--color-bg)] text-[var(--color-muted-fg)] hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]"
        }`}
      >
        {listening ? <MicOff className={iconSize} /> : <Mic className={iconSize} />}
      </button>
      {/* Only show the floating fallback tooltip when the parent isn't
          rendering its own preview. */}
      {!hasParentPreview && listening && interimText && (
        <div className="pointer-events-none absolute bottom-full right-0 mb-2 w-max max-w-md whitespace-normal rounded-[var(--radius)] border bg-[var(--color-bg)] px-3 py-1.5 text-xs italic text-[var(--color-muted-fg)] shadow-md">
          {interimText}…
        </div>
      )}
      {error && (
        <div className="pointer-events-none absolute bottom-full right-0 mb-2 w-max max-w-md whitespace-normal rounded-[var(--radius)] border border-red-300 bg-red-50 px-3 py-1.5 text-xs text-red-700 shadow-md">
          {error}
        </div>
      )}
    </div>
  );
}
