import { beforeEach, expect, test, vi } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/auth", () => ({ getSession: vi.fn() }));
vi.mock("@/lib/api", () => ({ gateway: { extractInvoiceText: vi.fn() } }));
import { getSession } from "@/lib/auth";
import { gateway } from "@/lib/api";
import { POST } from "./route";

beforeEach(() => {
  vi.resetAllMocks();
});

function requestWithFile(file: File | null): NextRequest {
  const formData = new FormData();
  if (file) formData.set("file", file);
  return new NextRequest("http://web/api/accounting/invoice/extract-text", {
    method: "POST",
    body: formData,
  });
}

test("requires a session before relaying to the gateway", async () => {
  vi.mocked(getSession).mockResolvedValue(null);
  const res = await POST(requestWithFile(new File(["x"], "a.pdf")));
  expect(res.status).toBe(401);
  expect(gateway.extractInvoiceText).not.toHaveBeenCalled();
});

test("rejects a missing/empty file before ever calling the gateway", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  const res = await POST(requestWithFile(null));
  expect(res.status).toBe(400);
  expect(gateway.extractInvoiceText).not.toHaveBeenCalled();

  const emptyFile = new File([], "empty.pdf");
  const res2 = await POST(requestWithFile(emptyFile));
  expect(res2.status).toBe(400);
  expect(gateway.extractInvoiceText).not.toHaveBeenCalled();
});

test("relays the gateway's extraction result unchanged on success (incl. XML)", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  const payload = {
    text: "SoHDon: 00012345\nMST: 0399999999",
    charCount: 33,
    mimeType: "application/xml",
    extractionMethod: "xml_text",
    originalFilename: "invoice.xml",
    sourceObjectKey: "tenant/x/accounting/invoice-staging/y/abc.xml",
  };
  vi.mocked(gateway.extractInvoiceText).mockResolvedValue(
    new Response(JSON.stringify(payload), { status: 200, headers: { "content-type": "application/json" } }),
  );

  const res = await POST(requestWithFile(new File(["<HDon/>"], "invoice.xml")));

  expect(res.status).toBe(200);
  expect(await res.json()).toEqual(payload);
  expect(gateway.extractInvoiceText).toHaveBeenCalledWith("token", expect.any(FormData));
});

test("relays a gateway extraction failure unchanged", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  vi.mocked(gateway.extractInvoiceText).mockResolvedValue(
    new Response(JSON.stringify({ error: "extraction_failed", detail: "Không trích xuất được nội dung" }), {
      status: 422,
      headers: { "content-type": "application/json" },
    }),
  );

  const res = await POST(requestWithFile(new File(["garbage"], "broken.pdf")));

  expect(res.status).toBe(422);
  const body = await res.json();
  expect(body.error).toBe("extraction_failed");
});
