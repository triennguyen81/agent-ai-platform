import { SignUp } from "@clerk/nextjs";
import { AuthShell } from "@/components/auth-shell";

export const metadata = {
  title: "Tạo tài khoản — Cát Tường AI",
};

export default function SignUpPage() {
  return (
    <AuthShell title="Tạo tài khoản" subtitle="Miễn phí. Bắt đầu trong 30 giây.">
      <SignUp
        path="/sign-up"
        routing="path"
        signInUrl="/sign-in"
        forceRedirectUrl="/api/auth/sync?to=/chat"
        appearance={{
          elements: {
            rootBox: "w-full",
            card: "bg-transparent shadow-none border-0 p-0",
            headerTitle: "hidden",
            headerSubtitle: "hidden",
            socialButtonsBlockButton:
              "border border-[var(--color-border)] hover:bg-[var(--color-muted)]",
            formButtonPrimary:
              "bg-[var(--color-primary)] text-[var(--color-primary-fg)] hover:opacity-90",
            footerActionLink: "text-[var(--color-primary)] hover:underline",
          },
        }}
      />
    </AuthShell>
  );
}
