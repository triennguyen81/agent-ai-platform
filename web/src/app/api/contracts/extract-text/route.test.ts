import { beforeEach, expect, test, vi } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/auth", () => ({ getSession: vi.fn() }));
vi.mock("@/lib/api", () => ({ gateway: { extractContractText: vi.fn() } }));
import { getSession } from "@/lib/auth";
import { gateway } from "@/lib/api";
import { POST } from "./route";

beforeEach(() => {
  vi.resetAllMocks();
});

function requestWithFile(file: File | null): NextRequest {
  const formData = new FormData();
  if (file) formData.set("file", file);
  return new NextRequest("http://web/api/contracts/extract-text", {
    method: "POST",
    body: formData,
  });
}

test("requires a session before relaying to the gateway", async () => {
  vi.mocked(getSession).mockResolvedValue(null);
  const res = await POST(requestWithFile(new File(["x"], "a.pdf")));
  expect(res.status).toBe(401);
  expect(gateway.extractContractText).not.toHaveBeenCalled();
});

test("rejects a missing/empty file before ever calling the gateway", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  const res = await POST(requestWithFile(null));
  expect(res.status).toBe(400);
  expect(gateway.extractContractText).not.toHaveBeenCalled();

  const emptyFile = new File([], "empty.pdf");
  const res2 = await POST(requestWithFile(emptyFile));
  expect(res2.status).toBe(400);
  expect(gateway.extractContractText).not.toHaveBeenCalled();
});

test("relays the gateway's extraction result unchanged on success", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  const payload = {
    text: "Nội dung hợp đồng",
    charCount: 18,
    mimeType: "application/pdf",
    extractionMethod: "pdf_text_layer",
    originalFilename: "contract.pdf",
    sourceObjectKey: "tenant/x/contracts/y/staging/abc.pdf",
  };
  vi.mocked(gateway.extractContractText).mockResolvedValue(
    new Response(JSON.stringify(payload), { status: 200, headers: { "content-type": "application/json" } }),
  );

  const res = await POST(requestWithFile(new File(["%PDF-1.4 ..."], "contract.pdf")));

  expect(res.status).toBe(200);
  expect(await res.json()).toEqual(payload);
  expect(gateway.extractContractText).toHaveBeenCalledWith("token", expect.any(FormData));
});

test("relays a gateway extraction failure (e.g. malformed document) unchanged", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  vi.mocked(gateway.extractContractText).mockResolvedValue(
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
