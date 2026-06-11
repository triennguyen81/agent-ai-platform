import { SignIn } from "@clerk/nextjs";
import { AuthShell } from "@/components/auth-shell";

export const metadata = {
  title: "Đăng nhập — Cát Tường AI",
};

export default function SignInPage() {
  return (
    <AuthShell title="Đăng nhập" subtitle="Tiếp tục với tài khoản của bạn.">
      <SignIn
        path="/sign-in"
        routing="path"
        signUpUrl="/sign-up"
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
