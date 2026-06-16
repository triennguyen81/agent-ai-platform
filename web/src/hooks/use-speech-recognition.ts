"use client";

import { useCallback, useEffect, useRef, useState } from "react";

// The Web Speech API types are not in lib.dom yet. Declare the minimum
// surface we touch so callers stay type-safe without pulling in a polyfill.
interface SpeechRecognitionAlternative {
  transcript: string;
  confidence: number;
}

interface SpeechRecognitionResult {
  isFinal: boolean;
  length: number;
  [index: number]: SpeechRecognitionAlternative;
}

interface SpeechRecognitionResultList {
  length: number;
  [index: number]: SpeechRecognitionResult;
}

interface SpeechRecognitionEvent {
  resultIndex: number;
  results: SpeechRecognitionResultList;
}

interface SpeechRecognitionErrorEvent {
  error: string;
  message?: string;
}

interface SpeechRecognitionInstance {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  maxAlternatives: number;
  onresult: ((event: SpeechRecognitionEvent) => void) | null;
  onerror: ((event: SpeechRecognitionErrorEvent) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
  abort(): void;
}

type SpeechRecognitionCtor = new () => SpeechRecognitionInstance;

interface UseSpeechRecognitionOptions {
  lang?: string;
  onFinalTranscript?: (text: string) => void;
}

interface UseSpeechRecognitionResult {
  supported: boolean;
  listening: boolean;
  interimText: string;
  error: string | null;
  start: () => void;
  stop: () => void;
}

function getCtor(): SpeechRecognitionCtor | null {
  if (typeof window === "undefined") return null;
  const w = window as unknown as {
    SpeechRecognition?: SpeechRecognitionCtor;
    webkitSpeechRecognition?: SpeechRecognitionCtor;
  };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
}

export function useSpeechRecognition(
  options: UseSpeechRecognitionOptions = {},
): UseSpeechRecognitionResult {
  const { lang = "vi-VN", onFinalTranscript } = options;
  const [supported, setSupported] = useState(false);
  const [listening, setListening] = useState(false);
  const [interimText, setInterimText] = useState("");
  const [error, setError] = useState<string | null>(null);
  const recognitionRef = useRef<SpeechRecognitionInstance | null>(null);
  const onFinalRef = useRef(onFinalTranscript);

  useEffect(() => {
    onFinalRef.current = onFinalTranscript;
  }, [onFinalTranscript]);

  useEffect(() => {
    const Ctor = getCtor();
    setSupported(Boolean(Ctor));
    if (!Ctor) return;

    const recognition = new Ctor();
    recognition.lang = lang;
    recognition.continuous = true;
    recognition.interimResults = true;
    recognition.maxAlternatives = 1;

    recognition.onresult = (event) => {
      let interim = "";
      let finalChunk = "";
      for (let i = event.resultIndex; i < event.results.length; i += 1) {
        const result = event.results[i];
        const text = result[0]?.transcript ?? "";
        if (result.isFinal) {
          finalChunk += text;
        } else {
          interim += text;
        }
      }
      setInterimText(interim);
      if (finalChunk) {
        onFinalRef.current?.(finalChunk.trim());
      }
    };

    recognition.onerror = (event) => {
      // "no-speech" và "aborted" là bình thường khi user dừng tay — không
      // hiển thị như lỗi.
      if (event.error === "no-speech" || event.error === "aborted") {
        return;
      }
      setError(event.message ?? event.error ?? "Speech recognition failed");
    };

    recognition.onend = () => {
      setListening(false);
      setInterimText("");
    };

    recognitionRef.current = recognition;

    return () => {
      try {
        recognition.abort();
      } catch {
        // ignore
      }
      recognitionRef.current = null;
    };
  }, [lang]);

  const start = useCallback(() => {
    const recognition = recognitionRef.current;
    if (!recognition || listening) return;
    setError(null);
    setInterimText("");
    try {
      recognition.start();
      setListening(true);
    } catch (err) {
      // Chrome throws InvalidStateError if start() called while already running.
      setError(err instanceof Error ? err.message : "Failed to start mic");
    }
  }, [listening]);

  const stop = useCallback(() => {
    const recognition = recognitionRef.current;
    if (!recognition) return;
    try {
      recognition.stop();
    } catch {
      // ignore
    }
    setListening(false);
  }, []);

  return { supported, listening, interimText, error, start, stop };
}
