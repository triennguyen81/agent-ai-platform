import { beforeEach, expect, test, vi } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/auth", () => ({ getSession: vi.fn() }));
vi.mock("@/lib/api", () => ({ GATEWAY_BASE: "http://gateway" }));
import { getSession } from "@/lib/auth";
import { GET, POST, DELETE } from "./route";

beforeEach(() => { vi.resetAllMocks(); vi.stubGlobal("fetch", vi.fn()); });
test("requires a session before proxying knowledge", async () => {
  vi.mocked(getSession).mockResolvedValue(null);
  expect((await GET(new NextRequest("http://web/api/knowledge-bases"), { params: Promise.resolve({}) })).status).toBe(401);
  expect(fetch).not.toHaveBeenCalled();
});
test("forwards upload bytes and authenticated token to the fixed department endpoint", async () => {
  vi.mocked(getSession).mockResolvedValue("server-token");
  vi.mocked(fetch).mockResolvedValue(new Response('{"status":"pending"}', { status: 202 }));
  const result = await POST(new NextRequest("http://web/api/knowledge-bases/kb/documents", { method: "POST", body: "document-bytes", headers: { "content-type": "multipart/form-data; boundary=test", Authorization: "Bearer forged" } }), { params: Promise.resolve({ path: ["kb", "documents"] }) });
  expect(result.status).toBe(202);
  expect(fetch).toHaveBeenCalledWith("http://gateway/department/knowledge-bases/kb/documents", expect.objectContaining({ method: "POST", headers: expect.objectContaining({ Authorization: "Bearer server-token" }) }));
});
test("rejects path traversal and preserves no-content detach responses", async () => {
  vi.mocked(getSession).mockResolvedValue("token");
  expect((await GET(new NextRequest("http://web/api/knowledge-bases"), { params: Promise.resolve({ path: [".."] }) })).status).toBe(400);
  vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 204 }));
  expect((await DELETE(new NextRequest("http://web/api/knowledge-bases/kb/agents/agent", { method: "DELETE" }), { params: Promise.resolve({ path: ["kb", "agents", "agent"] }) })).status).toBe(204);
});

test("direct entry uses the same selected-KB document endpoint as upload", async () => {
  vi.mocked(getSession).mockResolvedValue("server-token");
  vi.mocked(fetch).mockResolvedValue(new Response('{"status":"accepted"}', { status: 202 }));
  const body = { title: "Policy", tags: ["ar"], content: "Review overdue accounts.", documentId: null };
  const result = await POST(new NextRequest("http://web/api/knowledge-bases/selected-kb/documents", {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body),
  }), { params: Promise.resolve({ path: ["selected-kb", "documents"] }) });
  expect(result.status).toBe(202);
  const [url, options] = vi.mocked(fetch).mock.calls[0];
  expect(url).toBe("http://gateway/department/knowledge-bases/selected-kb/documents");
  expect(options?.headers).toEqual({ Authorization: "Bearer server-token", "Content-Type": "application/json" });
  expect(JSON.parse(new TextDecoder().decode(options?.body as ArrayBuffer))).toEqual(body);
});
