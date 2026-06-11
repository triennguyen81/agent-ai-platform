import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Cát Tường AI",
  description: "Your personal AI assistant that knows what you know.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body>{children}</body>
    </html>
  );
}
