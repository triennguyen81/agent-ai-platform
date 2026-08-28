import { Activity, Building2, Users, Bot } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";

interface TenantQuotaStatus {
  tenantId: string;
  code: string;
  name: string;
  status: string;
  maxUsers: number | null;
  currentUsers: number;
  maxAgents: number | null;
  currentAgents: number;
}

function limitLabel(current: number, max: number | null): string {
  return max === null ? `${current} (không giới hạn)` : `${current} / ${max}`;
}

export default async function TenantStatusPage() {
  const viewer = await requireViewer(["tenant_owner"]);
  const response = await gateway.getMyTenantQuota(viewer.token);
  const quota: TenantQuotaStatus | null = response.ok ? ((await response.json()) as TenantQuotaStatus) : null;

  return (
    <div className="space-y-6">
      <header>
        <h1 className="font-serif text-3xl">Tình trạng tenant</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Quota và trạng thái thực tế của tenant gắn với phiên đăng nhập này — luôn xác định phía server, không nhận từ client.
        </p>
      </header>

      {!quota ? (
        <p className="rounded border border-dashed p-4 text-sm text-[var(--color-muted-fg)]">
          Không tải được tình trạng tenant.
        </p>
      ) : (
        <section className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <div className="rounded-[var(--radius)] border p-5">
            <Building2 className="mb-2 size-5" />
            <div className="font-medium">{quota.name}</div>
            <div className="font-mono text-xs text-[var(--color-muted-fg)]">{quota.code}</div>
          </div>
          <div className="rounded-[var(--radius)] border p-5">
            <Activity className="mb-2 size-5 text-emerald-600" />
            <div className="font-medium">Trạng thái</div>
            <div className="text-xs text-[var(--color-muted-fg)]">{quota.status}</div>
          </div>
          <div className="rounded-[var(--radius)] border p-5">
            <Users className="mb-2 size-5" />
            <div className="font-medium">Người dùng</div>
            <div className="text-xs text-[var(--color-muted-fg)]">{limitLabel(quota.currentUsers, quota.maxUsers)}</div>
          </div>
          <div className="rounded-[var(--radius)] border p-5">
            <Bot className="mb-2 size-5" />
            <div className="font-medium">Agent đang được gán</div>
            <div className="text-xs text-[var(--color-muted-fg)]">{limitLabel(quota.currentAgents, quota.maxAgents)}</div>
          </div>
        </section>
      )}

      <p className="rounded border border-dashed p-4 text-sm text-[var(--color-muted-fg)]">
        Hiện chỉ số lượng người dùng và agent đang gán được áp quota. Giới hạn token/chi phí/billing chưa được
        triển khai — dữ liệu sử dụng nằm trong audit log per-execution của agent runtime, chưa ở dạng Gateway có
        thể enforce cứng, nên trang này không tự bịa ra số liệu.
      </p>
    </div>
  );
}
