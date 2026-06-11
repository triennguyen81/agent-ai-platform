// Clerk middleware — wraps Clerk's runtime so its <SignIn />, <SignUp />,
// and useUser() hooks work, but does NOT gate access. Our (app) routes
// authenticate via the aiq.session httpOnly cookie which is set after EITHER
// the Clerk flow (via /api/auth/sync) OR the legacy /login email/password
// flow. Layout components enforce the cookie check directly.
//
// When NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY is missing, fall back to a no-op so
// dev environments without Clerk dashboard setup keep working.

import type { NextRequest } from "next/server";
import { NextResponse } from "next/server";

const isClerkConfigured = Boolean(process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY);

async function noopMiddleware(_req: NextRequest): Promise<NextResponse> {
  return NextResponse.next();
}

function buildClerkMiddleware() {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { clerkMiddleware } =
    require("@clerk/nextjs/server") as typeof import("@clerk/nextjs/server");

  // No auth.protect() — Clerk wraps every request to populate currentUser()
  // and useUser() correctly, but route protection lives in our (app) layout.
  return clerkMiddleware();
}

export default isClerkConfigured ? buildClerkMiddleware() : noopMiddleware;

export const config = {
  matcher: [
    // Skip Next internals and static files
    "/((?!_next|[^?]*\\.(?:html?|css|js(?!on)|jpe?g|webp|png|gif|svg|ttf|woff2?|ico)).*)",
    "/(api|trpc)(.*)",
  ],
};
