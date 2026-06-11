import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway, type AuthResponse } from "@/lib/api";
import { setSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

async function signupAction(formData: FormData) {
  "use server";
  const email = String(formData.get("email") ?? "");
  const password = String(formData.get("password") ?? "");
  const displayName = String(formData.get("displayName") ?? "");

  const res = await gateway.register({ email, password, displayName });
  if (!res.ok) {
    const reason = res.status === 409 ? "duplicate" : "invalid";
    redirect(`/signup?error=${reason}`);
  }

  const body = (await res.json()) as AuthResponse;
  await setSession(body.token, new Date(body.expiresAt));
  redirect("/chat");
}

export default async function SignupPage({
  searchParams,
}: {
  searchParams: Promise<{ error?: string }>;
}) {
  const { error } = await searchParams;

  return (
    <main className="grid min-h-dvh place-items-center px-6">
      <div className="w-full max-w-sm space-y-8">
        <header className="space-y-2 text-center">
          <h1 className="font-serif text-3xl">Create your account</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Free. No card. Start writing your knowledge in 30 seconds.
          </p>
        </header>

        <form action={signupAction} className="space-y-5">
          <div className="space-y-2">
            <Label htmlFor="displayName">Name</Label>
            <Input id="displayName" name="displayName" required minLength={1} maxLength={64} />
          </div>
          <div className="space-y-2">
            <Label htmlFor="email">Email</Label>
            <Input id="email" name="email" type="email" required autoComplete="email" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="password">Password</Label>
            <Input id="password" name="password" type="password" required minLength={8} autoComplete="new-password" />
            <p className="text-xs text-[var(--color-muted-fg)]">Min 8 characters.</p>
          </div>

          {error === "duplicate" && (
            <p className="text-sm text-red-600">Email already registered.</p>
          )}
          {error === "invalid" && (
            <p className="text-sm text-red-600">Sign up failed. Check your input.</p>
          )}

          <Button type="submit" className="w-full" size="lg">
            Create account
          </Button>
        </form>

        <p className="text-center text-sm text-[var(--color-muted-fg)]">
          Already have one?{" "}
          <Link href="/login" className="text-[var(--color-primary)] underline">
            Sign in
          </Link>
        </p>
      </div>
    </main>
  );
}
