import Link from "next/link";
import { ShieldX } from "lucide-react";

export default function ForbiddenPage() {
  return (
    <div className="mx-auto max-w-lg space-y-4 rounded-[var(--radius)] border p-8 text-center">
      <ShieldX className="mx-auto size-10 text-red-600" />
      <h1 className="font-serif text-3xl">Không có quyền truy cập</h1>
      <p className="text-sm text-[var(--color-muted-fg)]">
        Trang này không thuộc phạm vi vai trò của tài khoản hiện tại.
      </p>
      <Link href="/chat" className="text-sm text-[var(--color-primary)] hover:underline">Về Chat</Link>
    </div>
  );
}
