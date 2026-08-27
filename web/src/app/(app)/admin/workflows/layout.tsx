import { requireViewer } from "@/lib/authorization";
export default async function Layout({ children }: { children: React.ReactNode }) { await requireViewer(["super_admin"]); return children; }
