import { redirect } from "next/navigation";
import { KeyRound } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

async function changePasswordAction(formData: FormData): Promise<void> {
  "use server";
  const currentPassword = String(formData.get("currentPassword") ?? "");
  const newPassword = String(formData.get("newPassword") ?? "");
  const confirmPassword = String(formData.get("confirmPassword") ?? "");

  if (!currentPassword || newPassword.length < 8) {
    redirect("/profile?error=mat-khau-moi-toi-thieu-8-ky-tu");
  }
  if (newPassword !== confirmPassword) {
    redirect("/profile?error=xac-nhan-mat-khau-khong-khop");
  }

  const token = await requireSession();
  const res = await gateway.changePassword(token, { currentPassword, newPassword, confirmPassword });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    const message = body.error === "current_password_incorrect"
      ? "mat-khau-hien-tai-khong-dung"
      : body.error === "new_password_must_differ"
        ? "mat-khau-moi-phai-khac-mat-khau-cu"
        : body.error === "password_confirmation_mismatch"
          ? "xac-nhan-mat-khau-khong-khop"
          : `doi-mat-khau-that-bai-${res.status}`;
    redirect(`/profile?error=${encodeURIComponent(message)}`);
  }
  redirect("/profile?ok=password-changed");
}

const ERROR_MESSAGES: Record<string, string> = {
  "mat-khau-hien-tai-khong-dung": "Mật khẩu hiện tại không đúng.",
  "mat-khau-moi-phai-khac-mat-khau-cu": "Mật khẩu mới phải khác mật khẩu hiện tại.",
  "xac-nhan-mat-khau-khong-khop": "Xác nhận mật khẩu không khớp.",
  "mat-khau-moi-toi-thieu-8-ky-tu": "Mật khẩu mới phải có ít nhất 8 ký tự.",
};

export default async function ProfilePage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const viewer = await requireViewer();
  const { ok, error } = await searchParams;

  return (
    <div className="max-w-xl space-y-8">
      <header className="space-y-1">
        <h1 className="font-serif text-3xl">Hồ sơ</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">{viewer.email}</p>
      </header>

      <section className="space-y-4 rounded-[var(--radius)] border p-5">
        <div className="flex items-center gap-2">
          <KeyRound className="size-5 text-[var(--color-primary)]" />
          <h2 className="font-serif text-xl">Bảo mật — Đổi mật khẩu</h2>
        </div>

        {viewer.mustChangePassword && (
          <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
            Mật khẩu của bạn vừa được quản trị viên đặt lại. Vui lòng đổi sang mật khẩu mới của riêng bạn.
          </p>
        )}
        {ok === "password-changed" && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            Đã đổi mật khẩu thành công.
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {ERROR_MESSAGES[error] ?? decodeURIComponent(error)}
          </p>
        )}

        <form action={changePasswordAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="currentPassword">Mật khẩu hiện tại</Label>
            <Input id="currentPassword" name="currentPassword" type="password" required autoComplete="current-password" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="newPassword">Mật khẩu mới</Label>
            <Input id="newPassword" name="newPassword" type="password" required minLength={8} autoComplete="new-password" placeholder="Tối thiểu 8 ký tự" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="confirmPassword">Xác nhận mật khẩu mới</Label>
            <Input id="confirmPassword" name="confirmPassword" type="password" required minLength={8} autoComplete="new-password" />
          </div>
          <Button type="submit" className="w-full">Đổi mật khẩu</Button>
        </form>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Đổi mật khẩu không đăng xuất các phiên đăng nhập khác đang hoạt động trên thiết bị khác.
        </p>
      </section>
    </div>
  );
}
