"use client";

import { Button } from "@/components/ui/button";

export function TenantRoleChangeButton({
  userId,
  nextRole,
  label,
  confirmMessage,
  action,
}: {
  userId: string;
  nextRole: "user" | "admin";
  label: string;
  confirmMessage: string;
  action: (userId: string, nextRole: "user" | "admin") => Promise<void>;
}) {
  return (
    <Button
      type="button"
      size="sm"
      variant="outline"
      onClick={() => {
        if (confirm(confirmMessage)) void action(userId, nextRole);
      }}
    >
      {label}
    </Button>
  );
}
