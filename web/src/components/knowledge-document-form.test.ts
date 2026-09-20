import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { expect, test, vi } from "vitest";
vi.mock("@/lib/authorization", () => ({ requireViewer: vi.fn().mockResolvedValue({}) }));
import DepartmentKnowledgePage from "@/app/(app)/department/knowledge/page";
import { KnowledgeDocumentForm, documentRequest } from "./knowledge-document-form";

// React's SSR renderer checks real form markup without a browser-only test dependency.
test("add document is unavailable until a KB is selected; legacy manual area is absent", async () => {
  const html = renderToStaticMarkup(await DepartmentKnowledgePage());
  expect(html).toMatch(/<button[^>]*disabled=""[^>]*aria-controls="kb-add-document"/);
  expect(html).not.toContain('name="content"');
  expect(html).not.toContain("Thêm tài liệu mới");
});

test.each(["upload", "manual"] as const)("one document form offers the %s mode", (mode) => {
  const html = renderToStaticMarkup(createElement(KnowledgeDocumentForm, {
    mode, onModeChange: vi.fn(), documents: [{ id: "existing", title: "Policy" }], busy: false, onSubmit: vi.fn(),
  }));
  expect(html.match(/<form\b/g)).toHaveLength(1);
  expect(html).toContain("Tải tệp lên");
  expect(html).toContain("Nhập trực tiếp");
  expect(html).toContain('value="existing"');
  if (mode === "upload") {
    expect(html).toContain('.pdf,.docx,.txt,.md,.png,.jpg,.jpeg');
    expect(html).not.toContain('name="content"');
  } else {
    expect(html).toContain('name="title"');
    expect(html).toContain('name="tags"');
    expect(html).toContain('name="content"');
    expect(html).not.toContain('type="file"');
  }
});

test("direct input keeps text and chosen version, normalizes tags, and supplies no tenant authority", () => {
  const form = new FormData();
  form.set("title", " Policy "); form.set("tags", "ar, policy, ar, ");
  form.set("content", "# Policy\n  Original formatting"); form.set("documentId", "existing");
  form.set("tenantId", "forged"); form.set("knowledgeBaseId", "forged");
  expect(JSON.parse(documentRequest("manual", form).body as string)).toEqual({
    title: "Policy", tags: ["ar", "policy"], content: "# Policy\n  Original formatting", documentId: "existing",
  });
  expect(documentRequest("upload", form).body).toBe(form);
});
