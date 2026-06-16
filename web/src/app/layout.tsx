import { ClerkProvider } from "@clerk/nextjs";
import { Inter, Lora } from "next/font/google";
import type { Metadata } from "next";
import type { ReactNode } from "react";
import { isClerkConfigured } from "@/lib/env";
import "./globals.css";

export const metadata: Metadata = {
  title: "Cát Tường AI",
  description: "Your personal AI assistant that knows what you know.",
};

// Inter (sans) and Lora (serif) both ship a `vietnamese` subset so combining
// tone marks like ấ / ế / ờ render as single composed glyphs instead of
// falling back to Georgia/Times where decomposed marks land beside the
// base letter ("Bă´t" instead of "Bắt").
const inter = Inter({
  subsets: ["latin", "latin-ext", "vietnamese"],
  display: "swap",
  variable: "--font-inter",
});

const lora = Lora({
  subsets: ["latin", "latin-ext", "vietnamese"],
  display: "swap",
  variable: "--font-lora",
});

export default function RootLayout({ children }: { children: ReactNode }) {
  // Static import so Next.js standalone tracing bundles @clerk/nextjs into
  // the runtime image. ClerkProvider mounts only when a publishable key
  // is configured — without it, ClerkProvider throws at render time.
  const body = isClerkConfigured ? <ClerkProvider>{children}</ClerkProvider> : children;
  return (
    <html lang="vi" className={`${inter.variable} ${lora.variable}`} suppressHydrationWarning>
      <body className={inter.className}>{body}</body>
    </html>
  );
}
