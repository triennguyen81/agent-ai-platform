import { Building2 } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { CompanyProfileForm } from "@/components/company-profile-form";

export default async function CompanyProfilePage() {
  const viewer = await requireViewer(["tenant_owner"]);
  const response = await gateway.getCompanyProfile(viewer.token);
  const profile: { companyTaxId: string | null } | null = response.ok
    ? ((await response.json()) as { companyTaxId: string | null })
    : null;

  return (
    <div className="space-y-6">
      <header>
        <h1 className="font-serif text-3xl">
          <Building2 className="mr-2 inline size-6" /> Thông tin công ty
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Mã số thuế của công ty (tenant) hiện tại — luôn xác định phía server,
          không nhận từ trình duyệt hay AI.
        </p>
      </header>

      <section className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <CompanyProfileForm initialCompanyTaxId={profile?.companyTaxId ?? null} />
      </section>
    </div>
  );
}
