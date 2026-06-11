import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway, type AuthResponse } from "@/lib/api";
import { setSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

async function loginAction(formData: FormData) {
  "use server";
  const email = String(formData.get("email") ?? "");
  const password = String(formData.get("password") ?? "");

  const res = await gateway.login({ email, password });
  if (!res.ok) {
    redirect("/login?error=invalid");
  }

  const body = (await res.json()) as AuthResponse;
  await setSession(body.token, new Date(body.expiresAt));
  redirect("/chat");
}

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ error?: string }>;
}) {
  const { error } = await searchParams;

  return (
    <main className="grid min-h-dvh place-items-center px-6">
      <div className="w-full max-w-sm space-y-8">
        <header className="space-y-2 text-center">
          <h1 className="font-serif text-3xl">Cát Tường AI</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Welcome back.
          </p>
        </header>

        <form action={loginAction} className="space-y-5">
          <div className="space-y-2">
            <Label htmlFor="email">Email</Label>
            <Input id="email" name="email" type="email" required autoComplete="email" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="password">Password</Label>
            <Input id="password" name="password" type="password" required autoComplete="current-password" />
          </div>

          {error === "invalid" && (
            <p className="text-sm text-red-600">Invalid email or password.</p>
          )}

          <Button type="submit" className="w-full" size="lg">
            Sign in
          </Button>
        </form>

        <p className="text-center text-sm text-[var(--color-muted-fg)]">
          New here?{" "}
          <Link href="/signup" className="text-[var(--color-primary)] underline">
            Create an account
          </Link>
        </p>
      </div>
    </main>
  );
}
