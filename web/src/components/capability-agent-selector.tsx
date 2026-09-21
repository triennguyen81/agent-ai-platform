import { Bot } from "lucide-react";
import type { EligibleCapabilityAgent } from "@/lib/capability";

// "Xử lý bằng trợ lý" — reusable selector shown only when more than one
// Agent is eligible for a capability (Contract first; Voucher/Invoice/
// Customer/Supplier/Expense reuse this unchanged). Purely presentational:
// the parent page owns selection state and passes the chosen id through to
// executeCapability/runTask as preferredAgentId, which the Gateway always
// re-validates server-side before honoring.
export function CapabilityAgentSelector({
  agents,
  selectedAgentId,
  onSelect,
  disabled,
}: {
  agents: EligibleCapabilityAgent[];
  selectedAgentId: string | null;
  onSelect: (agentId: string) => void;
  disabled?: boolean;
}) {
  return (
    <div className="space-y-1.5">
      <label htmlFor="capability-agent-selector" className="text-sm font-medium">
        Xử lý bằng trợ lý
      </label>
      <select
        id="capability-agent-selector"
        value={selectedAgentId ?? ""}
        onChange={(e) => onSelect(e.target.value)}
        disabled={disabled}
        className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
      >
        <option value="" disabled>
          — Chọn trợ lý —
        </option>
        {agents.map((a) => (
          <option key={a.agentId} value={a.agentId}>
            {a.name}
          </option>
        ))}
      </select>
      <p className="flex items-center gap-1 text-[10px] text-[var(--color-muted-fg)]">
        <Bot className="size-3" /> Nghiệp vụ này đang được nhiều trợ lý hỗ trợ — chọn 1 để xử lý.
      </p>
    </div>
  );
}
