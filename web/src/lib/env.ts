// Auth toggle — Clerk is the primary auth UI when configured. When the
// publishable key is empty we fall back to the legacy email/password
// /login + /signup pages so dev environments keep working without any
// Clerk dashboard setup.
export const isClerkConfigured = Boolean(
  process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY,
);

export const gatewayBaseUrl = process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080";
export const agentBaseUrl = process.env.AGENT_BASE_URL ?? "http://aiq-agent:8000";

// Shared secret used to authenticate the web tier when calling
// /auth/external-link on the gateway. MUST be set in production whenever
// NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY is set.
export const gatewaySharedSecret = process.env.GATEWAY_SHARED_SECRET ?? "";

// Comma-separated emails that auto-receive the `admin` role on first
// Clerk sync. Used to bootstrap the first admin before anyone else can
// invite them.
export const initialAdminEmails: string[] = (process.env.INITIAL_ADMIN_EMAIL ?? "")
  .split(",")
  .map((e) => e.trim().toLowerCase())
  .filter(Boolean);
