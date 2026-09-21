import { Building2 } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { BusinessProfileForm, type BusinessProfileValues } from "@/components/business-profile-form";

interface CompanyProfileRecord extends BusinessProfileValues {
  tenantId: string;
  createdAt: string;
  updatedAt: string;
}

// TENANT_OWNER-only ("Hồ sơ doanh nghiệp"). Only ONLY-TENANT_OWNER may
// create/update it — enforced server-side by the "tenant_governance" policy
// on GET/PUT /tenant/business-profile, not just by this page's own gate.
export default async function BusinessProfilePage() {
  const viewer = await requireViewer(["tenant_owner"]);
  const response = await gateway.getBusinessProfile(viewer.token);
  const profile: CompanyProfileRecord | null = response.ok
    ? ((await response.json()) as { profile: CompanyProfileRecord | null }).profile
    : null;

  return (
    <div className="space-y-6">
      <header>
        <h1 className="font-serif text-3xl">
          <Building2 className="mr-2 inline size-6" /> Hồ sơ doanh nghiệp
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Danh tính pháp lý của công ty chủ thể (tenant hiện tại) — AIQ dùng thông tin này làm
          ngữ cảnh công ty đáng tin cậy khi trò chuyện. Chỉ Tenant Owner được cập nhật.
        </p>
      </header>

      <section className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <BusinessProfileForm initial={profile} />
      </section>
    </div>
  );
}
