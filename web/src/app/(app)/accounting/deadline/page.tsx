"use client";

import { useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, Clock } from "lucide-react";

interface Deadline {
  formCode: string;
  name: string;
  period: string;
  date: Date;
}

function buildDeadlines(year: number): Deadline[] {
  const items: Deadline[] = [];
  // Quarterly 01/GTGT (Q1-Q4)
  for (let q = 1; q <= 4; q += 1) {
    const month = (q * 3) % 12; // Q4 → month 0 = Jan next year (handled below)
    const targetYear = q * 3 + 1 > 12 ? year + 1 : year;
    const targetMonth = q * 3 === 12 ? 1 : month + 1; // simplified
    items.push({
      formCode: "01/GTGT",
      name: "Tờ khai GTGT khấu trừ",
      period: `Q${q}/${year}`,
      date: new Date(targetYear, targetMonth - 1, 30),
    });
    items.push({
      formCode: "02/TNDN",
      name: "Tạm nộp TNDN",
      period: `Q${q}/${year}`,
      date: new Date(targetYear, targetMonth - 1, 30),
    });
  }
  // Annual: 31/03 năm sau
  items.push({
    formCode: "03/TNDN",
    name: "Quyết toán TNDN",
    period: `Năm ${year}`,
    date: new Date(year + 1, 2, 31),
  });
  items.push({
    formCode: "05/QTT-TNCN",
    name: "Quyết toán TNCN (DN)",
    period: `Năm ${year}`,
    date: new Date(year + 1, 2, 31),
  });
  items.push({
    formCode: "BCTC",
    name: "Báo cáo tài chính năm",
    period: `Năm ${year}`,
    date: new Date(year + 1, 2, 31),
  });
  // Monthly 01/GTGT for next few months
  for (let m = 0; m < 12; m += 1) {
    const targetMonth = (m + 1) % 12;
    const targetYear = m + 1 === 12 ? year + 1 : year;
    items.push({
      formCode: "01/GTGT-T",
      name: `Tờ khai GTGT khấu trừ (khai tháng)`,
      period: `T${m + 1}/${year}`,
      date: new Date(targetYear, targetMonth, 20),
    });
  }
  return items.sort((a, b) => a.date.getTime() - b.date.getTime());
}

function daysUntil(d: Date): number {
  const now = new Date();
  const diff = d.getTime() - now.getTime();
  return Math.ceil(diff / (1000 * 60 * 60 * 24));
}

function statusOf(d: number): "overdue" | "urgent" | "soon" | "ok" {
  if (d < 0) return "overdue";
  if (d <= 7) return "urgent";
  if (d <= 30) return "soon";
  return "ok";
}

function statusColor(s: ReturnType<typeof statusOf>): string {
  switch (s) {
    case "overdue":
      return "border-red-600 bg-red-50 text-red-700";
    case "urgent":
      return "border-orange-500 bg-orange-50 text-orange-700";
    case "soon":
      return "border-yellow-500 bg-yellow-50 text-yellow-700";
    case "ok":
      return "border-emerald-500 bg-emerald-50 text-emerald-700";
  }
}

function statusIcon(s: ReturnType<typeof statusOf>) {
  switch (s) {
    case "overdue":
      return <AlertTriangle className="size-4" />;
    case "urgent":
      return <AlertTriangle className="size-4" />;
    case "soon":
      return <Clock className="size-4" />;
    case "ok":
      return <CheckCircle2 className="size-4" />;
  }
}

export default function DeadlinePage() {
  const [year, setYear] = useState(new Date().getFullYear());
  const [filter, setFilter] = useState<"all" | "upcoming" | "urgent">("upcoming");

  const items = useMemo(() => {
    const all = buildDeadlines(year);
    if (filter === "all") return all;
    if (filter === "upcoming") return all.filter((d) => daysUntil(d.date) >= 0);
    if (filter === "urgent") return all.filter((d) => {
      const days = daysUntil(d.date);
      return days >= 0 && days <= 30;
    });
    return all;
  }, [year, filter]);

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="font-serif text-3xl">Deadline tờ khai</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Lịch deadline theo Luật Quản lý thuế 38/2019/QH14 + NĐ126/2020. Tính
            cho DN năm tài chính = năm dương lịch.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <label className="text-xs text-[var(--color-muted-fg)]">Năm:</label>
          <select
            value={year}
            onChange={(e) => setYear(Number(e.target.value))}
            className="rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1 text-sm"
          >
            {[2024, 2025, 2026, 2027].map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </select>
        </div>
      </header>

      <div className="flex gap-1 rounded-full border border-[var(--color-border)] bg-[var(--color-bg)] p-1 text-sm">
        {(["upcoming", "urgent", "all"] as const).map((f) => (
          <button
            key={f}
            type="button"
            onClick={() => setFilter(f)}
            className={`flex-1 rounded-full px-4 py-1.5 transition ${
              filter === f
                ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
            }`}
          >
            {f === "upcoming" ? "Sắp tới" : f === "urgent" ? "Trong 30 ngày" : "Tất cả"}
          </button>
        ))}
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {items.length === 0 ? (
          <p className="col-span-full text-center text-sm text-[var(--color-muted-fg)]">
            Không có deadline phù hợp.
          </p>
        ) : (
          items.map((item, idx) => {
            const days = daysUntil(item.date);
            const status = statusOf(days);
            return (
              <div
                key={`${item.formCode}-${item.period}-${idx}`}
                className={`flex flex-col gap-2 rounded-[var(--radius)] border p-4 ${statusColor(status)}`}
              >
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <p className="font-mono text-xs font-semibold opacity-70">
                      {item.formCode}
                    </p>
                    <p className="font-semibold">{item.name}</p>
                  </div>
                  {statusIcon(status)}
                </div>
                <p className="text-xs">Kỳ: {item.period}</p>
                <p className="font-mono text-lg">
                  {item.date.toLocaleDateString("vi-VN")}
                </p>
                <p className="text-xs">
                  {days < 0
                    ? `Đã quá hạn ${-days} ngày`
                    : days === 0
                    ? "Đến hạn hôm nay"
                    : `Còn ${days} ngày`}
                </p>
              </div>
            );
          })
        )}
      </div>

      <section className="rounded-[var(--radius)] border border-dashed bg-[var(--color-muted)] p-4 text-sm">
        <p className="font-medium">Phạt chậm nộp</p>
        <ul className="mt-1 space-y-1 text-[var(--color-muted-fg)]">
          <li>• Tờ khai: 2-25 triệu ₫ tuỳ thời gian chậm (NĐ125/2020/NĐ-CP)</li>
          <li>
            • Tiền thuế: 0,03%/ngày trên số tiền chậm nộp (Điều 59 Luật QLT 38/2019)
          </li>
          <li>
            • Khuyến nghị: chuẩn bị <strong>trước 7 ngày</strong> để có thời gian
            xử lý sai sót
          </li>
        </ul>
      </section>
    </div>
  );
}
