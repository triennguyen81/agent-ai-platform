"use client";

import * as RadixDropdown from "@radix-ui/react-dropdown-menu";
import { ChevronDown } from "lucide-react";
import { type ComponentPropsWithoutRef, type ElementRef, forwardRef } from "react";
import { cn } from "@/lib/cn";

export const DropdownMenu = RadixDropdown.Root;
export const DropdownMenuTrigger = RadixDropdown.Trigger;

export const DropdownMenuContent = forwardRef<
  ElementRef<typeof RadixDropdown.Content>,
  ComponentPropsWithoutRef<typeof RadixDropdown.Content>
>(({ className, sideOffset = 8, align = "start", ...props }, ref) => (
  <RadixDropdown.Portal>
    <RadixDropdown.Content
      ref={ref}
      sideOffset={sideOffset}
      align={align}
      className={cn(
        "z-50 min-w-48 rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] p-1 text-sm shadow-lg",
        "data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95",
        className,
      )}
      {...props}
    />
  </RadixDropdown.Portal>
));
DropdownMenuContent.displayName = "DropdownMenuContent";

export const DropdownMenuItem = forwardRef<
  ElementRef<typeof RadixDropdown.Item>,
  ComponentPropsWithoutRef<typeof RadixDropdown.Item>
>(({ className, ...props }, ref) => (
  <RadixDropdown.Item
    ref={ref}
    className={cn(
      "flex cursor-pointer items-center gap-2 rounded px-2.5 py-1.5 text-[var(--color-fg)] outline-none transition",
      "hover:bg-[var(--color-muted)] focus:bg-[var(--color-muted)] data-[disabled]:pointer-events-none data-[disabled]:opacity-40",
      className,
    )}
    {...props}
  />
));
DropdownMenuItem.displayName = "DropdownMenuItem";

export const DropdownMenuLabel = forwardRef<
  ElementRef<typeof RadixDropdown.Label>,
  ComponentPropsWithoutRef<typeof RadixDropdown.Label>
>(({ className, ...props }, ref) => (
  <RadixDropdown.Label
    ref={ref}
    className={cn("px-2.5 py-1.5 text-xs font-medium uppercase tracking-wider text-[var(--color-muted-fg)]", className)}
    {...props}
  />
));
DropdownMenuLabel.displayName = "DropdownMenuLabel";

export const DropdownMenuSeparator = forwardRef<
  ElementRef<typeof RadixDropdown.Separator>,
  ComponentPropsWithoutRef<typeof RadixDropdown.Separator>
>(({ className, ...props }, ref) => (
  <RadixDropdown.Separator ref={ref} className={cn("my-1 h-px bg-[var(--color-border)]", className)} {...props} />
));
DropdownMenuSeparator.displayName = "DropdownMenuSeparator";

/** Compact "Label ⌄" nav trigger — the visual language for top-level module dropdowns. */
export function NavTrigger({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <DropdownMenuTrigger
      className={cn(
        "inline-flex items-center gap-1 rounded-[var(--radius)] px-3 py-1.5 text-sm text-[var(--color-fg)] outline-none transition",
        "hover:bg-[var(--color-muted)] data-[state=open]:bg-[var(--color-muted)]",
        className,
      )}
    >
      {children}
      <ChevronDown className="size-3.5 text-[var(--color-muted-fg)]" />
    </DropdownMenuTrigger>
  );
}
