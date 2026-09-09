import type { ReactNode } from "react";
import { requireViewer } from "@/lib/authorization";

export default async function DepartmentToolsLayout({ children }: { children: ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
