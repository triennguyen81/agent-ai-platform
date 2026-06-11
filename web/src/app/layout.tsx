import { ClerkProvider } from "@clerk/nextjs";
import { Inter } from "next/font/google";
import type { Metadata } from "next";
import type { ReactNode } from "react";
import { isClerkConfigured } from "@/lib/env";
import "./globals.css";

export const metadata: Metadata = {
  title: "Cát Tường AI",
  description: "Your personal AI assistant that knows what you know.",
};

// Inter has full Vietnamese diacritic support and is self-hosted by Next.
const inter = Inter({
  subsets: ["latin", "latin-ext", "vietnamese"],
  display: "swap",
});

export default function RootLayout({ children }: { children: ReactNode }) {
  // Static import so Next.js standalone tracing bundles @clerk/nextjs into
  // the runtime image. ClerkProvider mounts only when a publishable key
  // is configured — without it, ClerkProvider throws at render time.
  const body = isClerkConfigured ? <ClerkProvider>{children}</ClerkProvider> : children;
  return (
    <html lang="vi" suppressHydrationWarning>
      <body className={inter.className}>{body}</body>
    </html>
  );
}
