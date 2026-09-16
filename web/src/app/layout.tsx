import { ClerkProvider } from "@clerk/nextjs";
import type { Metadata } from "next";
import type { ReactNode } from "react";
import { isClerkConfigured } from "@/lib/env";
import "./globals.css";

export const metadata: Metadata = {
  title: "Cát Tường AI",
  description: "Your personal AI assistant that knows what you know.",
};

// Font stack is a plain system-font fallback declared in globals.css
// (--font-sans / --font-serif) — no next/font/google, so the production
// build never needs network access to fonts.googleapis.com/fonts.gstatic.com.
export default function RootLayout({ children }: { children: ReactNode }) {
  // Static import so Next.js standalone tracing bundles @clerk/nextjs into
  // the runtime image. ClerkProvider mounts only when a publishable key
  // is configured — without it, ClerkProvider throws at render time.
  const body = isClerkConfigured ? <ClerkProvider>{children}</ClerkProvider> : children;
  return (
    <html lang="vi" suppressHydrationWarning>
      <body>{body}</body>
    </html>
  );
}
