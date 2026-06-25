import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { NotificationsList, type NotificationRecord } from "@/components/notifications-list";

export default async function NotificationsPage() {
  const token = await requireSession();
  const res = await gateway.listNotifications(token, { limit: 100 });
  let items: NotificationRecord[] = [];
  if (res.ok) {
    const body = (await res.json()) as { items: NotificationRecord[] };
    items = body.items ?? [];
  }

  return (
    <div className="space-y-6">
      <header className="space-y-1">
        <h1 className="font-serif text-3xl">Thông báo</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Nhắc nhở từ checklist hợp đồng. Cron quét mỗi 7:00 sáng (giờ VN).
        </p>
      </header>

      <NotificationsList initialItems={items} />
    </div>
  );
}
