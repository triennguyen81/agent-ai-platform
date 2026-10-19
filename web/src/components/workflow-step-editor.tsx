"use client";

import { useId, useState } from "react";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

// Shared structured Workflow Builder editor — used by BOTH SUPER_ADMIN's
// platform workflow pages and ADMIN's Department Workflow page. Same editor
// UX everywhere; the surrounding form/page decides where the serialized
// `definition` JSON gets sent (platform create/update vs. department
// draft-version PATCH) and what "publish" means for that scope. This
// component never talks to the network itself — it only keeps a hidden
// input's value in sync with the interactive step list so any parent
// <form> (Server Action or client fetch) can read it on submit.
//
// Only the step *shape* the runtime actually understands is exposed here —
// "prompt" (free-text instruction), "require_knowledge" (no extra config,
// gated on the caller's authorized knowledge), and "require_tool" (an
// installed tool code) — kept in sync with
// runtime/executors.py's _WORKFLOW_STEP_TYPES and
// Governance/IWorkflowRepository.cs's WorkflowStepTypes.Known. There is no
// way to enter arbitrary Python/shell/eval/SQL through this editor — the
// step "value" is always plain text bound to one of these three fixed
// shapes, never executed client- or server-side, just persisted as data.

// "require_review" is a document-processing gate: the Gateway document
// pipeline forces human (ADMIN) confirmation for that document type. It can
// only make processing stricter — never post, verify or skip a gate.
export type WorkflowStepType = "prompt" | "require_knowledge" | "require_tool" | "require_review";

export interface WorkflowStepValue {
  id: string;
  type: WorkflowStepType;
  value: string;
}

export interface ParsedWorkflowDefinition {
  steps: WorkflowStepValue[];
  requiresApproval: boolean;
}

let stepIdCounter = 0;
function nextStepId(): string {
  stepIdCounter += 1;
  return `step-${Date.now()}-${stepIdCounter}`;
}

/** Parses a workflow's definitionJson (or undefined, for a brand-new workflow) into editor-friendly steps. Never invents a placeholder step — an empty/missing definition yields an empty steps array. */
export function parseWorkflowDefinition(definitionJson: string | undefined | null): ParsedWorkflowDefinition {
  if (!definitionJson) return { steps: [], requiresApproval: false };
  try {
    const parsed = JSON.parse(definitionJson) as {
      steps?: Array<{ type: string; prompt?: string; text?: string; tool?: string }>;
      requiresApproval?: boolean;
    };
    const steps = (parsed.steps ?? []).map((step) => ({
      id: nextStepId(),
      type: (step.type as WorkflowStepType) ?? "prompt",
      // Canonical key is "prompt"; "text" is the legacy key older API rows
      // still carry — read it so re-saving never silently erases a step.
      value: step.prompt ?? step.text ?? step.tool ?? "",
    }));
    return { steps, requiresApproval: Boolean(parsed.requiresApproval) };
  } catch {
    return { steps: [], requiresApproval: false };
  }
}

/** Serializes editor state back into the exact JSON shape the Gateway/runtime expect. */
export function serializeWorkflowDefinition(steps: WorkflowStepValue[], requiresApproval: boolean): string {
  const normalized = steps.map((step) =>
    step.type === "prompt"
      ? { type: step.type, prompt: step.value }
      : step.type === "require_tool"
        ? { type: step.type, tool: step.value }
        : { type: step.type },
  );
  return JSON.stringify({ steps: normalized, requiresApproval });
}

const STEP_TYPE_LABELS: Record<WorkflowStepType, string> = {
  prompt: "Prompt",
  require_knowledge: "Yêu cầu knowledge",
  require_tool: "Yêu cầu tool",
  require_review: "Yêu cầu duyệt thủ công",
};

function stepPlaceholder(type: WorkflowStepType): string {
  if (type === "prompt") return "Nội dung hướng dẫn cho bước này";
  if (type === "require_tool") return "Mã tool đã cài đặt (tool code)";
  if (type === "require_review") return "Không cần cấu hình — chứng từ phải được ADMIN xác nhận trước khi xử lý tiếp";
  return "Không cần cấu hình — dùng knowledge được uỷ quyền của Agent gọi workflow";
}

export function WorkflowStepEditor({
  fieldName = "definition",
  initialDefinitionJson,
}: {
  fieldName?: string;
  initialDefinitionJson?: string | null;
}) {
  const [state, setState] = useState<ParsedWorkflowDefinition>(() => parseWorkflowDefinition(initialDefinitionJson));
  const approvalId = useId();

  function updateStep(id: string, patch: Partial<WorkflowStepValue>) {
    setState((current) => ({
      ...current,
      steps: current.steps.map((step) => (step.id === id ? { ...step, ...patch } : step)),
    }));
  }
  function addStep() {
    setState((current) => ({ ...current, steps: [...current.steps, { id: nextStepId(), type: "prompt", value: "" }] }));
  }
  function removeStep(id: string) {
    setState((current) => ({ ...current, steps: current.steps.filter((step) => step.id !== id) }));
  }
  function moveStep(id: string, direction: -1 | 1) {
    setState((current) => {
      const index = current.steps.findIndex((step) => step.id === id);
      const target = index + direction;
      if (index < 0 || target < 0 || target >= current.steps.length) return current;
      const steps = [...current.steps];
      [steps[index], steps[target]] = [steps[target], steps[index]];
      return { ...current, steps };
    });
  }

  const definitionJson = serializeWorkflowDefinition(state.steps, state.requiresApproval);

  return (
    <div className="space-y-3">
      <input type="hidden" name={fieldName} value={definitionJson} />
      <Label>Các bước (ordered steps)</Label>
      {state.steps.length === 0 ? (
        <p className="rounded border border-dashed p-3 text-xs text-[var(--color-muted-fg)]">
          Chưa có bước nào — nhấn &ldquo;Thêm bước&rdquo; để bắt đầu.
        </p>
      ) : (
        <div className="space-y-2">
          {state.steps.map((step, index) => (
            <div key={step.id} className="grid gap-2 rounded border p-3 sm:grid-cols-[180px_1fr_auto]">
              <select
                aria-label={`Loại bước ${index + 1}`}
                value={step.type}
                onChange={(event) => updateStep(step.id, { type: event.target.value as WorkflowStepType, value: "" })}
                className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 text-sm"
              >
                <option value="prompt">{STEP_TYPE_LABELS.prompt}</option>
                <option value="require_knowledge">{STEP_TYPE_LABELS.require_knowledge}</option>
                <option value="require_tool">{STEP_TYPE_LABELS.require_tool}</option>
                <option value="require_review">{STEP_TYPE_LABELS.require_review}</option>
              </select>
              <Input
                aria-label={`Nội dung bước ${index + 1}`}
                disabled={step.type === "require_knowledge" || step.type === "require_review"}
                value={step.value}
                placeholder={stepPlaceholder(step.type)}
                onChange={(event) => updateStep(step.id, { value: event.target.value })}
              />
              <div className="flex items-center gap-1">
                <Button type="button" variant="ghost" size="sm" onClick={() => moveStep(step.id, -1)} disabled={index === 0} title="Di chuyển lên">
                  <ArrowUp className="size-4" />
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => moveStep(step.id, 1)} disabled={index === state.steps.length - 1} title="Di chuyển xuống">
                  <ArrowDown className="size-4" />
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => removeStep(step.id)} title="Xoá bước">
                  <Trash2 className="size-4" />
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}
      <div className="flex flex-wrap items-center gap-4">
        <Button type="button" variant="outline" size="sm" onClick={addStep}>
          <Plus className="size-4" /> Thêm bước
        </Button>
        <label htmlFor={approvalId} className="flex items-center gap-2 text-sm">
          <input
            id={approvalId}
            type="checkbox"
            checked={state.requiresApproval}
            onChange={(event) => setState((current) => ({ ...current, requiresApproval: event.target.checked }))}
          />
          Yêu cầu phê duyệt (requires approval)
        </label>
      </div>
    </div>
  );
}

/** Read-only rendering for a published version or a Global/platform workflow ADMIN may only view+use. Never renders Add/Remove/reorder controls. */
export function ReadOnlyWorkflowSteps({ definitionJson }: { definitionJson: string }) {
  const { steps, requiresApproval } = parseWorkflowDefinition(definitionJson);
  if (steps.length === 0) {
    return <p className="text-xs text-[var(--color-muted-fg)]">Chưa có bước nào.</p>;
  }
  return (
    <div className="space-y-1">
      <ol className="list-decimal space-y-1 pl-5 text-sm">
        {steps.map((step) => (
          <li key={step.id}>
            <span className="font-mono text-xs text-[var(--color-muted-fg)]">{STEP_TYPE_LABELS[step.type]}</span>
            {step.value ? ` — ${step.value}` : ""}
          </li>
        ))}
      </ol>
      {requiresApproval && <p className="text-xs text-amber-700">Yêu cầu phê duyệt trước khi thực thi.</p>}
    </div>
  );
}
