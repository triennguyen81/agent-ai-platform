// Clerk auth middleware — only runs when NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY
// is set. When Clerk is not configured (dev mode), this middleware is a
// no-op so the legacy /login + /signup flow keeps working.

import type { NextRequest } from "next/server";
import { NextResponse } from "next/server";

const isClerkConfigured = Boolean(process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY);

async function noopMiddleware(_req: NextRequest): Promise<NextResponse> {
  return NextResponse.next();
}

function buildClerkMiddleware() {
  // Lazy-load so the import does not break dev builds without a Clerk key.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { clerkMiddleware, createRouteMatcher } =
    require("@clerk/nextjs/server") as typeof import("@clerk/nextjs/server");

  const isPublicRoute = createRouteMatcher([
    "/",
    "/sign-in(.*)",
    "/sign-up(.*)",
    "/login(.*)",          // legacy email login stays available
    "/signup(.*)",
    "/api/auth/sync",      // post-Clerk sync to Gateway
  ]);

  return clerkMiddleware(async (auth, req) => {
    if (!isPublicRoute(req)) {
      await auth.protect();
    }
  });
}

export default isClerkConfigured ? buildClerkMiddleware() : noopMiddleware;

export const config = {
  matcher: [
    // Skip Next internals and static files
    "/((?!_next|[^?]*\\.(?:html?|css|js(?!on)|jpe?g|webp|png|gif|svg|ttf|woff2?|ico)).*)",
    "/(api|trpc)(.*)",
  ],
};
