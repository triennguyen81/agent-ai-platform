"use client";
import { useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

type StepType = "prompt" | "require_knowledge" | "require_tool";
interface Step { type: StepType; value: string }

export function WorkflowBuilder() {
  const [steps, setSteps] = useState<Step[]>([{ type: "prompt", value: "" }]);
  const [message, setMessage] = useState("");
  async function submit(form: FormData) {
    setMessage("Saving…");
    const normalized = steps.map((step) => step.type === "prompt" ? { type: step.type, prompt: step.value } : step.type === "require_tool" ? { type: step.type, tool: step.value } : { type: step.type });
    const response = await fetch("/api/workflows", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: form.get("name"), description: form.get("description"), definition: { steps: normalized, requiresApproval: form.get("requiresApproval") === "on" } }) });
    setMessage(response.ok ? "Workflow saved. Refresh to view it." : `Save failed (${response.status})`);
  }
  return <form action={submit} className="space-y-4 rounded-[var(--radius)] border p-5"><h2 className="font-serif text-xl">Create structured workflow</h2><div><Label htmlFor="wf-name">Name</Label><Input id="wf-name" name="name" required /></div><div><Label htmlFor="wf-description">Description</Label><Input id="wf-description" name="description" /></div>
    <div className="space-y-2"><Label>Ordered steps</Label>{steps.map((step, index) => <div key={index} className="grid gap-2 rounded border p-3 sm:grid-cols-[180px_1fr_auto]"><select value={step.type} onChange={(event) => setSteps((current) => current.map((item, i) => i === index ? { type: event.target.value as StepType, value: "" } : item))} className="rounded border bg-[var(--color-bg)] px-2 text-sm"><option value="prompt">Prompt</option><option value="require_knowledge">Require knowledge</option><option value="require_tool">Require tool</option></select><Input aria-label={`Step ${index + 1} value`} disabled={step.type === "require_knowledge"} value={step.value} placeholder={step.type === "prompt" ? "Instruction" : step.type === "require_tool" ? "Installed tool code" : "Tenant-authorized knowledge required"} onChange={(event) => setSteps((current) => current.map((item, i) => i === index ? { ...item, value: event.target.value } : item))}/><Button type="button" variant="ghost" size="sm" onClick={() => setSteps((current) => current.filter((_, i) => i !== index))} disabled={steps.length === 1}><Trash2 className="size-4"/></Button></div>)}</div>
    <div className="flex flex-wrap gap-2"><Button type="button" variant="outline" onClick={() => setSteps((current) => [...current, { type: "prompt", value: "" }])}><Plus className="size-4"/>Add step</Button><label className="flex items-center gap-2 text-sm"><input type="checkbox" name="requiresApproval"/>Requires approval</label></div><Button type="submit">Save workflow</Button>{message && <p className="text-sm text-[var(--color-muted-fg)]">{message}</p>}</form>;
}
