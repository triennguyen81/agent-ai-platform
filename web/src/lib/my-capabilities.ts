import "server-only";
import { gateway } from "@/lib/api";
import type { LoginRole } from "@/lib/authorization";

// D7 §7/§17: shared by every business page that needs to show/hide an
// AI-backed action based on the viewer's actual authorized capabilities
// (accounting/layout.tsx's sub-nav, accounting/page.tsx's tool cards) —
// one fetch-and-derive instead of two copies drifting apart. Returns
// `null` for TENANT_OWNER/SUPER_ADMIN, who never get implicit production
// capability execution (D4 §8) and so have no `/me/capabilities` of their
// own; callers should treat `null` as "not capability-gated, show
// everything" rather than "authorized for nothing." This is presentation
// only — the Gateway resolver and each page's own executeCapability call
// remain the sole authorization boundary regardless of what this returns.
export async function fetchMyCapabilityCodes(
  token: string,
  roles: LoginRole[],
): Promise<Set<string> | null> {
  if (!roles.includes("admin") && !roles.includes("user")) return null;
  const res = await gateway.listMyCapabilities(token);
  const codes = res.ok
    ? ((await res.json()) as { items: { code: string }[] }).items.map((c) => c.code)
    : [];
  return new Set(codes);
}
