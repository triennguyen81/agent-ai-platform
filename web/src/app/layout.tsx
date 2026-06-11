import type { Metadata } from "next";
import type { ReactNode } from "react";
import { isClerkConfigured } from "@/lib/env";
import "./globals.css";

export const metadata: Metadata = {
  title: "Cát Tường AI",
  description: "Your personal AI assistant that knows what you know.",
};

export default function RootLayout({ children }: { children: ReactNode }) {
  // ClerkProvider goes INSIDE <body> per Clerk v6+ guidance — keeps the
  // server-rendered <html> stable across auth state transitions.
  if (isClerkConfigured) {
    // Dynamic require so dev builds with no Clerk key skip the import.
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { ClerkProvider } = require("@clerk/nextjs") as typeof import("@clerk/nextjs");
    return (
      <html lang="vi" suppressHydrationWarning>
        <body>
          <ClerkProvider>{children}</ClerkProvider>
        </body>
      </html>
    );
  }

  return (
    <html lang="vi" suppressHydrationWarning>
      <body>{children}</body>
    </html>
  );
}
