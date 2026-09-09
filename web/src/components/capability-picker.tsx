"use client";

import { useState } from "react";

export interface CapabilityOption {
  id: string;
  code: string;
  name: string;
  category: string | null;
  isActive: boolean;
  // ACL V2-D: "platform" (consume-only, SUPER_ADMIN-managed) or "department"
  // (this department's own, ADMIN-managed) — display only, Gateway is what
  // actually enforces which of the two a draft may attach.
  scope?: "platform" | "department";
}

/**
 * D4 Agent Builder "Nghiệp vụ hỗ trợ" picker. Deliberately client-side:
 * search/filter and a live selected-count are the explicit anti-bloat
 * safeguards from the D4 spec (no implicit select-all, no silently
 * attaching every capability). Renders plain `name="capabilityIds"`
 * checkboxes so it still posts through the surrounding server-action form
 * exactly like the Tools/Skills checkbox lists beside it.
 */
export function CapabilityPicker({
  capabilities,
  selected,
}: {
  capabilities: CapabilityOption[];
  selected: string[];
}) {
  const [query, setQuery] = useState("");
  const [checkedCount, setCheckedCount] = useState(selected.length);

  const active = capabilities.filter((c) => c.isActive);
  const q = query.trim().toLowerCase();
  const filtered = q
    ? active.filter((c) => c.name.toLowerCase().includes(q) || c.code.toLowerCase().includes(q))
    : active;

  const grouped = new Map<string, CapabilityOption[]>();
  for (const c of filtered) {
    const key = c.category ?? "Khác";
    if (!grouped.has(key)) grouped.set(key, []);
    grouped.get(key)!.push(c);
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <input
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Tìm nghiệp vụ theo tên hoặc mã..."
          className="w-full max-w-xs rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
        />
        <span className="shrink-0 text-xs text-[var(--color-muted-fg)]">Đã chọn: {checkedCount}</span>
      </div>
      {active.length === 0 ? (
        <p className="text-xs text-[var(--color-muted-fg)]">Chưa có nghiệp vụ nào trong catalog.</p>
      ) : filtered.length === 0 ? (
        <p className="text-xs text-[var(--color-muted-fg)]">Không tìm thấy nghiệp vụ phù hợp.</p>
      ) : (
        <div className="max-h-64 space-y-3 overflow-auto rounded border p-3">
          {[...grouped.entries()].map(([category, items]) => (
            <div key={category} className="space-y-1">
              <p className="text-[10px] font-medium uppercase tracking-wide text-[var(--color-muted-fg)]">
                {category}
              </p>
              <div className="grid gap-1.5 sm:grid-cols-2">
                {items.map((c) => (
                  <label key={c.id} className="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      name="capabilityIds"
                      value={c.id}
                      defaultChecked={selected.includes(c.id)}
                      onChange={(e) =>
                        setCheckedCount((prev) => prev + (e.target.checked ? 1 : -1))
                      }
                    />
                    <span>{c.name}</span>
                    <span className="font-mono text-[10px] text-[var(--color-muted-fg)]">{c.code}</span>
                    {c.scope && (
                      <span className="rounded-full border border-blue-300 bg-blue-50 px-1.5 py-0 text-[9px] text-blue-700">
                        {c.scope === "department" ? "Phòng ban" : "Hệ thống"}
                      </span>
                    )}
                  </label>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
