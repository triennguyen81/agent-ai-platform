"use client";

import { useState } from "react";
import Link from "next/link";
import {
  ArrowLeft,
  ArrowRight,
  CheckCircle2,
  Circle,
  GraduationCap,
  Lightbulb,
  Sparkles,
  XCircle,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import type { Artifact } from "@/data/artifacts-manifest";

// Client component — 3-step interactive walkthrough cho 1 artifact.
// State: currentStep (1/2/3), step1Selected, step3Selected, completed.
// Kết thúc bước 3 → show summary + "Học tiếp mẫu khác".

interface ArtifactWalkthroughProps {
  artifact: Artifact;
  nextArtifactId: string | null;
}

function fmtMoney(n: number): string {
  return n.toLocaleString("vi-VN");
}

const STEP_LABELS = [
  "Nhận diện loại chứng từ",
  "Fields LLM sẽ extract",
  "Bút toán suggest theo TT200",
];

export function ArtifactWalkthrough({ artifact, nextArtifactId }: ArtifactWalkthroughProps) {
  const [step, setStep] = useState<1 | 2 | 3 | 4>(1);
  const [step1Selected, setStep1Selected] = useState<number | null>(null);
  const [step1Revealed, setStep1Revealed] = useState(false);
  const [step3Selected, setStep3Selected] = useState<number | null>(null);
  const [step3Revealed, setStep3Revealed] = useState(false);

  const step1CorrectIdx = artifact.step1Options.findIndex((o) => o.correct);
  const step3CorrectIdx = artifact.step3QuizOptions.findIndex((o) => o.correct);

  const canAdvanceStep1 = step1Revealed;
  const canAdvanceStep3 = step3Revealed;

  return (
    <div className="space-y-6">
      {/* Progress steps header */}
      <div className="flex items-center gap-2 rounded-[var(--radius)] border bg-[var(--color-muted)]/30 p-3">
        {[1, 2, 3].map((s) => {
          const done = step > s || (s === 3 && step === 4);
          const active = step === s;
          return (
            <div key={s} className="flex flex-1 items-center gap-2">
              <div
                className={`flex size-8 items-center justify-center rounded-full text-sm font-bold ${
                  done
                    ? "bg-emerald-600 text-white"
                    : active
                      ? "bg-[var(--color-primary)] text-white"
                      : "bg-[var(--color-muted)] text-[var(--color-muted-fg)]"
                }`}
              >
                {done ? <CheckCircle2 className="size-4" /> : s}
              </div>
              <div className={`text-xs ${active ? "font-medium" : "text-[var(--color-muted-fg)]"}`}>
                {STEP_LABELS[s - 1]}
              </div>
              {s < 3 && <div className="h-px flex-1 bg-[var(--color-border)]" />}
            </div>
          );
        })}
      </div>

      {step === 1 && (
        <section className="space-y-4 rounded-[var(--radius)] border p-5">
          <div>
            <h2 className="font-serif text-lg">
              <GraduationCap className="mr-2 inline size-5 text-[var(--color-primary)]" /> Bước 1
              · Nhận diện loại chứng từ
            </h2>
            <p className="mt-1 text-sm text-[var(--color-muted-fg)]">
              Nhìn vào file bên trái. Đây là loại chứng từ gì?
            </p>
          </div>

          <div className="space-y-2">
            {artifact.step1Options.map((opt, idx) => {
              const isSelected = step1Selected === idx;
              const isCorrect = idx === step1CorrectIdx;
              const showResult = step1Revealed;
              return (
                <button
                  key={idx}
                  type="button"
                  onClick={() => {
                    if (!showResult) {
                      setStep1Selected(idx);
                      setStep1Revealed(true);
                    }
                  }}
                  disabled={showResult}
                  className={`flex w-full items-start gap-3 rounded-[var(--radius)] border p-3 text-left transition ${
                    showResult
                      ? isCorrect
                        ? "border-emerald-400 bg-emerald-50"
                        : isSelected
                          ? "border-red-400 bg-red-50"
                          : "border-[var(--color-border)] opacity-50"
                      : isSelected
                        ? "border-[var(--color-primary)] bg-[var(--color-primary)]/5"
                        : "hover:border-[var(--color-primary)] hover:bg-[var(--color-muted)]/30"
                  }`}
                >
                  {showResult ? (
                    isCorrect ? (
                      <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-emerald-600" />
                    ) : isSelected ? (
                      <XCircle className="mt-0.5 size-5 shrink-0 text-red-600" />
                    ) : (
                      <Circle className="mt-0.5 size-5 shrink-0 text-[var(--color-muted-fg)]" />
                    )
                  ) : (
                    <Circle className="mt-0.5 size-5 shrink-0 text-[var(--color-muted-fg)]" />
                  )}
                  <span className="text-sm">{opt.text}</span>
                </button>
              );
            })}
          </div>

          {step1Revealed && (
            <div className="flex items-start gap-2 rounded-[var(--radius)] border border-blue-200 bg-blue-50 p-3 text-sm text-blue-900">
              <Lightbulb className="mt-0.5 size-4 shrink-0 text-blue-600" />
              <div>
                <p className="font-medium">Giải thích</p>
                <p className="mt-1 text-xs">{artifact.step1Explanation}</p>
              </div>
            </div>
          )}

          <div className="flex justify-between border-t pt-4">
            <Link href="/mau-chung-tu">
              <Button variant="outline">
                <ArrowLeft className="size-4" /> Về danh sách
              </Button>
            </Link>
            <Button onClick={() => setStep(2)} disabled={!canAdvanceStep1}>
              Bước tiếp <ArrowRight className="size-4" />
            </Button>
          </div>
        </section>
      )}

      {step === 2 && (
        <section className="space-y-4 rounded-[var(--radius)] border p-5">
          <div>
            <h2 className="font-serif text-lg">
              <GraduationCap className="mr-2 inline size-5 text-[var(--color-primary)]" /> Bước 2
              · Fields LLM sẽ extract
            </h2>
            <p className="mt-1 text-sm text-[var(--color-muted-fg)]">
              Đây là những field chính mà universal-extractor sẽ đọc từ chứng từ. Hover để xem tại
              sao mỗi field quan trọng.
            </p>
          </div>

          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)]/60 text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-3 py-2 text-left">Field name</th>
                  <th className="px-3 py-2 text-left">Nghĩa</th>
                  <th className="px-3 py-2 text-left">Giá trị extract</th>
                </tr>
              </thead>
              <tbody>
                {artifact.step2Fields.map((f) => (
                  <tr key={f.field} className="border-t align-top">
                    <td className="px-3 py-2 font-mono text-xs text-[var(--color-primary)]">
                      {f.field}
                    </td>
                    <td className="px-3 py-2 text-xs">{f.label}</td>
                    <td className="px-3 py-2 text-xs">
                      <div className="font-mono font-medium">{f.value}</div>
                      <div className="mt-1 text-[11px] italic text-[var(--color-muted-fg)]">
                        💡 {f.hint}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {artifact.riskFlags.length > 0 && (
            <div className="rounded-[var(--radius)] border border-red-200 bg-red-50 p-3 text-sm text-red-900">
              <div className="mb-1 flex items-center gap-2 font-medium">
                <Sparkles className="size-4" /> Risk flags có thể fire
              </div>
              <ul className="ml-6 list-disc text-xs">
                {artifact.riskFlags.map((f) => (
                  <li key={f}>
                    <code className="rounded bg-white/60 px-1">{f}</code>
                  </li>
                ))}
              </ul>
            </div>
          )}

          <div className="flex justify-between border-t pt-4">
            <Button variant="outline" onClick={() => setStep(1)}>
              <ArrowLeft className="size-4" /> Bước trước
            </Button>
            <Button onClick={() => setStep(3)}>
              Bước tiếp <ArrowRight className="size-4" />
            </Button>
          </div>
        </section>
      )}

      {step === 3 && (
        <section className="space-y-4 rounded-[var(--radius)] border p-5">
          <div>
            <h2 className="font-serif text-lg">
              <GraduationCap className="mr-2 inline size-5 text-[var(--color-primary)]" /> Bước 3
              · Bút toán suggest theo TT200
            </h2>
            <p className="mt-1 text-sm text-[var(--color-muted-fg)]">
              Bút toán chuẩn được agent suggest. Tham chiếu: {artifact.tt200Reference}
            </p>
          </div>

          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)]/60 text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-3 py-2 text-left">Nợ</th>
                  <th className="px-3 py-2 text-left">Có</th>
                  <th className="px-3 py-2 text-right">Số tiền</th>
                  <th className="px-3 py-2 text-left">Diễn giải</th>
                </tr>
              </thead>
              <tbody>
                {artifact.step3JournalEntries.map((e, idx) => (
                  <tr key={idx} className="border-t">
                    <td className="px-3 py-2 font-mono font-bold text-red-700">{e.debit}</td>
                    <td className="px-3 py-2 font-mono font-bold text-blue-700">{e.credit}</td>
                    <td className="px-3 py-2 text-right font-mono">{fmtMoney(e.amount)}</td>
                    <td className="px-3 py-2 text-xs text-[var(--color-muted-fg)]">
                      {e.description}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="rounded-[var(--radius)] border-2 border-[var(--color-primary)]/30 bg-[var(--color-primary)]/5 p-4">
            <h3 className="font-medium">🎯 Quiz: {artifact.step3QuizQuestion}</h3>
            <div className="mt-3 space-y-2">
              {artifact.step3QuizOptions.map((opt, idx) => {
                const isSelected = step3Selected === idx;
                const isCorrect = idx === step3CorrectIdx;
                const showResult = step3Revealed;
                return (
                  <button
                    key={idx}
                    type="button"
                    onClick={() => {
                      if (!showResult) {
                        setStep3Selected(idx);
                        setStep3Revealed(true);
                      }
                    }}
                    disabled={showResult}
                    className={`flex w-full items-start gap-3 rounded-[var(--radius)] border p-3 text-left transition ${
                      showResult
                        ? isCorrect
                          ? "border-emerald-400 bg-emerald-50"
                          : isSelected
                            ? "border-red-400 bg-red-50"
                            : "border-[var(--color-border)] opacity-50"
                        : isSelected
                          ? "border-[var(--color-primary)] bg-white"
                          : "border-[var(--color-border)] bg-white hover:border-[var(--color-primary)]"
                    }`}
                  >
                    {showResult ? (
                      isCorrect ? (
                        <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-600" />
                      ) : isSelected ? (
                        <XCircle className="mt-0.5 size-4 shrink-0 text-red-600" />
                      ) : (
                        <Circle className="mt-0.5 size-4 shrink-0 text-[var(--color-muted-fg)]" />
                      )
                    ) : (
                      <Circle className="mt-0.5 size-4 shrink-0 text-[var(--color-muted-fg)]" />
                    )}
                    <span className="text-sm">{opt.text}</span>
                  </button>
                );
              })}
            </div>
            {step3Revealed && (
              <div className="mt-3 flex items-start gap-2 rounded-[var(--radius)] border border-blue-200 bg-blue-50 p-3 text-sm text-blue-900">
                <Lightbulb className="mt-0.5 size-4 shrink-0 text-blue-600" />
                <div>
                  <p className="font-medium">Giải thích</p>
                  <p className="mt-1 text-xs">{artifact.step3Explanation}</p>
                </div>
              </div>
            )}
          </div>

          <div className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
            <div className="flex items-start gap-2">
              <Sparkles className="mt-0.5 size-4 shrink-0 text-amber-600" />
              <div>
                <p className="font-medium">Bài học chốt</p>
                <p className="mt-1 text-xs">{artifact.keyLesson}</p>
              </div>
            </div>
          </div>

          <div className="flex justify-between border-t pt-4">
            <Button variant="outline" onClick={() => setStep(2)}>
              <ArrowLeft className="size-4" /> Bước trước
            </Button>
            <Button onClick={() => setStep(4)} disabled={!canAdvanceStep3}>
              Hoàn thành <CheckCircle2 className="size-4" />
            </Button>
          </div>
        </section>
      )}

      {step === 4 && (
        <section className="space-y-4 rounded-[var(--radius)] border border-emerald-300 bg-emerald-50/50 p-8 text-center">
          <div className="mx-auto flex size-16 items-center justify-center rounded-full bg-emerald-600 text-white">
            <CheckCircle2 className="size-8" />
          </div>
          <h2 className="font-serif text-2xl">Đã hoàn thành!</h2>
          <p className="mx-auto max-w-md text-sm text-[var(--color-muted-fg)]">
            Bạn đã học xong mẫu <b>{artifact.title}</b>. Giờ bạn hiểu voucher_type=
            <code className="rounded bg-white/60 px-1 font-mono">{artifact.voucherType}</code> —
            fields LLM extract — bút toán chuẩn TT200.
          </p>
          <div className="flex flex-wrap justify-center gap-3 border-t pt-4">
            <Link href="/mau-chung-tu">
              <Button variant="outline">
                <ArrowLeft className="size-4" /> Về danh sách
              </Button>
            </Link>
            {nextArtifactId && (
              <Link href={`/mau-chung-tu/${nextArtifactId}`}>
                <Button>
                  Học mẫu tiếp <ArrowRight className="size-4" />
                </Button>
              </Link>
            )}
            <Link href="/vouchers">
              <Button variant="outline">
                Upload chứng từ thực tế →
              </Button>
            </Link>
          </div>
        </section>
      )}
    </div>
  );
}
