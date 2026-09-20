"use client";

import { type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Input, Label, Textarea } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

export type DocumentMode = "upload" | "manual";
export type DocumentChoice = { id: string; title: string };

export function KnowledgeDocumentForm({ mode, onModeChange, documents, busy, onSubmit }: {
  mode: DocumentMode;
  onModeChange: (mode: DocumentMode) => void;
  documents: DocumentChoice[];
  busy: boolean;
  onSubmit: (event: FormEvent<HTMLFormElement>) => void;
}) {
  return (
    <form id="kb-add-document" className="space-y-4 rounded-[var(--radius)] border p-4" onSubmit={onSubmit}>
      <fieldset disabled={busy} className="space-y-4">
        <Tabs value={mode} onValueChange={(value) => onModeChange(value as DocumentMode)}>
          <TabsList aria-label="Cách thêm tài liệu">
            <TabsTrigger value="upload" disabled={busy}>Tải tệp lên</TabsTrigger>
            <TabsTrigger value="manual" disabled={busy}>Nhập trực tiếp</TabsTrigger>
          </TabsList>
          <TabsContent value="upload" className="space-y-2">
            <Label htmlFor="kb-file">Tệp tài liệu</Label>
            <Input id="kb-file" type="file" name="file" accept=".pdf,.docx,.txt,.md,.png,.jpg,.jpeg" required />
            <p className="text-xs text-[var(--color-muted-fg)]">PDF, DOCX, TXT, MD, PNG/JPG · tối đa 10 MB</p>
          </TabsContent>
          <TabsContent value="manual" className="space-y-3">
            <div className="space-y-1.5">
              <Label htmlFor="kb-title">Tiêu đề</Label>
              <Input id="kb-title" name="title" required maxLength={300} placeholder="Quy trình xử lý công nợ" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="kb-tags">Thẻ (phân cách bằng dấu phẩy)</Label>
              <Input id="kb-tags" name="tags" placeholder="cong-no, quy-trinh" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="kb-content">Nội dung</Label>
              <Textarea id="kb-content" name="content" required maxLength={2_000_000} rows={8} />
            </div>
          </TabsContent>
        </Tabs>
        <div className="space-y-1.5">
          <Label htmlFor="kb-document-version">Lưu tài liệu</Label>
          <select id="kb-document-version" name="documentId" className="w-full rounded border bg-[var(--color-bg)] p-2">
            <option value="">Tài liệu mới</option>
            {documents.map((doc) => <option key={doc.id} value={doc.id}>Phiên bản mới: {doc.title}</option>)}
          </select>
        </div>
        <Button type="submit" disabled={busy}>{busy ? "Đang lưu…" : "Lưu và lập chỉ mục"}</Button>
      </fieldset>
    </form>
  );
}

export function documentRequest(mode: DocumentMode, form: FormData): RequestInit {
  if (mode === "upload") return { method: "POST", body: form };
  return {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      title: String(form.get("title") ?? "").trim(),
      tags: [...new Set(String(form.get("tags") ?? "").split(",").map((tag) => tag.trim()).filter(Boolean))],
      content: String(form.get("content") ?? ""),
      documentId: String(form.get("documentId") ?? "") || null,
    }),
  };
}
