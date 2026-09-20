"use client";
import { useState } from "react";
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CustomerForm } from "@/components/customer-form";

// "+ Thêm khách hàng" — shown to any authenticated Department member who can
// reach this page at all; the Gateway (customer.management capability) is
// the actual authorization boundary, same "show the button, let the
// backend decide" pattern /contracts and /vouchers already use.
export function CustomerAddPanel() {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <Button onClick={() => setOpen((v) => !v)}>
        {open ? <X className="size-4" /> : <Plus className="size-4" />}
        {open ? "Đóng" : "Thêm khách hàng"}
      </Button>
      {open && (
        <div className="mt-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-4">
          <CustomerForm onDone={() => setOpen(false)} />
        </div>
      )}
    </div>
  );
}
