"use client";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

export function ResetPasswordForm({
  userId,
  targetRole,
  email,
  action,
}: {
  userId: string;
  targetRole: string;
  email: string;
  action: (formData: FormData) => Promise<void>;
}) {
  return (
    <form
      action={action}
      className="mt-2 flex flex-col gap-2"
      onSubmit={(e) => {
        if (
          !confirm(
            `Đặt lại mật khẩu cho ${email}?\nNgười dùng sẽ phải đổi sang mật khẩu mới ở lần đăng nhập tiếp theo.`,
          )
        ) {
          e.preventDefault();
        }
      }}
    >
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="targetRole" value={targetRole} />
      <Input name="newPassword" type="password" required minLength={8} placeholder="Mật khẩu mới (tối thiểu 8 ký tự)" />
      <Input name="confirmPassword" type="password" required minLength={8} placeholder="Xác nhận mật khẩu mới" />
      <Button type="submit" size="sm" variant="outline">Đặt lại mật khẩu</Button>
    </form>
  );
}
