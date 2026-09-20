import { redirect } from "next/navigation";
// ACL V2 final rule: department-scoped agent assignment lives entirely on
// the owning Department Agent's own page (tab Nhân sự) — there is no
// separate tenant-agent entitlement screen anymore.
export default function Page() { redirect("/department/agents"); }
