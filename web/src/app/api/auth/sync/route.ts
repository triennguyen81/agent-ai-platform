import { NextResponse, type NextRequest } from "next/server";
import { setSession } from "@/lib/auth";
import {
  gatewayBaseUrl,
  gatewaySharedSecret,
  initialAdminEmails,
  isClerkConfigured,
} from "@/lib/env";

// GET /api/auth/sync?to=/chat
// Called after a successful Clerk sign-in. Reads the Clerk user, exchanges
// their identity for a Gateway JWT via /auth/external-link, sets the
// aiq.session cookie, and redirects to ?to (default /chat).
export async function GET(request: NextRequest) {
  const to = request.nextUrl.searchParams.get("to") ?? "/chat";

  if (!isClerkConfigured) {
    return NextResponse.redirect(new URL(to, request.url));
  }

  if (!gatewaySharedSecret) {
    return NextResponse.json(
      { error: "GATEWAY_SHARED_SECRET is not configured" },
      { status: 500 },
    );
  }

  // Dynamic import — keeps the build clean on environments without Clerk.
  const { currentUser } = (await import("@clerk/nextjs/server")) as typeof import("@clerk/nextjs/server");
  const user = await currentUser();
  if (!user) {
    return NextResponse.redirect(new URL("/sign-in", request.url));
  }

  const email =
    user.emailAddresses.find((e) => e.id === user.primaryEmailAddressId)?.emailAddress
    ?? user.emailAddresses[0]?.emailAddress;
  if (!email) {
    return NextResponse.json(
      { error: "Clerk user has no email — refusing to sync" },
      { status: 400 },
    );
  }

  const displayName =
    [user.firstName, user.lastName].filter(Boolean).join(" ") || user.username || null;

  // Auto-grant admin to bootstrap emails.
  const additionalRoles = initialAdminEmails.includes(email.toLowerCase())
    ? ["admin"]
    : undefined;

  const res = await fetch(`${gatewayBaseUrl}/auth/external-link`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Gateway-Secret": gatewaySharedSecret,
    },
    body: JSON.stringify({
      provider: "clerk",
      externalId: user.id,
      email,
      displayName,
      additionalRoles,
    }),
    cache: "no-store",
  });

  if (!res.ok) {
    const text = await res.text();
    return NextResponse.json(
      { error: "external-link failed", status: res.status, body: text },
      { status: 502 },
    );
  }

  const body = (await res.json()) as { token: string; expiresAt: string };
  await setSession(body.token, new Date(body.expiresAt));

  return NextResponse.redirect(new URL(to, request.url));
}
