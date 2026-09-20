"use client";
import { useState } from "react";
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { SupplierForm } from "@/components/supplier-form";

// "+ Thêm nhà cung cấp" — shown to any authenticated Department member who
// can reach this page at all; the Gateway (supplier.management capability)
// is the actual authorization boundary, same pattern as CustomerAddPanel.
export function SupplierAddPanel() {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <Button onClick={() => setOpen((v) => !v)}>
        {open ? <X className="size-4" /> : <Plus className="size-4" />}
        {open ? "Đóng" : "Thêm nhà cung cấp"}
      </Button>
      {open && (
        <div className="mt-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-4">
          <SupplierForm onDone={() => setOpen(false)} />
        </div>
      )}
    </div>
  );
}
