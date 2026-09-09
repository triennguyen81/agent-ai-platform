import { requireViewer } from "@/lib/authorization";
export default async function Layout({ children }: { children: React.ReactNode }) {
  // D7: nav shows "Chứng từ" to every authenticated role, and the Gateway's
  // voucher endpoints already authorize literal `user` via the
  // `resource_or_tool` policy — this layout was the one place still
  // excluding `user`, silently 403'ing kế toán viên who clicked the link
  // the nav itself offered them. Web-only fix; no backend policy changed.
  await requireViewer(["admin", "user"]); return children;
}
