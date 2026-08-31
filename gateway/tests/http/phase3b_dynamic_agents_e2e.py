"""Live Phase 3B Gateway integration tests.

Requires the verified fixture users plus JWT_SIGNING_KEY. No passwords are
needed: tokens are signed with the same deploy-time key as the Gateway.
"""
import base64
import hashlib
import hmac
import json
import os
import time
import unittest
import urllib.error
import urllib.request
import uuid


BASE = os.getenv("AIQ_BASE_URL", "http://localhost:8080").rstrip("/")


def b64(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def token(user_id, role, tenant_id=None):
    now = int(time.time())
    payload = {"sub": user_id, "email": f"phase3b-{role}@aiq.local", "role": role,
               "iss": os.getenv("JWT_ISSUER", "aiq.local"),
               "aud": os.getenv("JWT_AUDIENCE", "aiq.client"), "iat": now, "exp": now + 3600}
    if tenant_id:
        payload["tenant_id"] = tenant_id
    head = b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    body = b64(json.dumps(payload, separators=(",", ":")).encode())
    signature = hmac.new(os.environ["JWT_SIGNING_KEY"].encode(), f"{head}.{body}".encode(), hashlib.sha256).digest()
    return f"{head}.{body}.{b64(signature)}"


def call(path, bearer, method="GET", body=None):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(BASE + path, data=data,
        headers={"Authorization": f"Bearer {bearer}", "Content-Type": "application/json"}, method=method)
    try:
        with urllib.request.urlopen(request) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw else None


@unittest.skipUnless(os.getenv("RUN_AIQ_PHASE3B_E2E") == "1", "live Phase 3B fixture test")
class DynamicAgentsE2E(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tenant_a = os.environ["TENANT_A_ID"]
        cls.tenant_b = os.environ["TENANT_B_ID"]
        cls.user_a_id = os.environ["USER_A_ID"]
        cls.user_b_id = os.environ["USER_B_ID"]
        cls.super = token(os.environ["SUPER_ADMIN_ID"], "super_admin")
        cls.admin_a = token(os.environ["ADMIN_A_ID"], "admin", cls.tenant_a)
        cls.user_a = token(cls.user_a_id, "user", cls.tenant_a)
        cls.user_b = token(cls.user_b_id, "user", cls.tenant_b)
        status, catalog = call("/tools?activeOnly=true", cls.super)
        assert status == 200
        cls.tool = next(item for item in catalog["items"] if item["minActionTier"] in ("read_only", "suggest"))

    def create_agent(self, suffix):
        slug = f"phase3b-{suffix}-{uuid.uuid4().hex[:8]}"
        status, value = call("/system/agents", self.super, "POST", {
            "slug": slug, "name": f"Phase 3B {suffix}", "description": "integration fixture",
            "iconMetadata": {"kind": "test"}, "type": "domain", "actionTier": "suggest"})
        self.assertEqual(201, status, value)
        return value

    def configure_draft(self, agent, model="phase3b-model-v1"):
        status, versions = call(f"/system/agents/{agent['id']}/versions", self.super)
        self.assertEqual(200, status, versions)
        draft = next(v for v in versions["items"] if v["status"] == "draft")
        status, edited = call(f"/system/agents/{agent['id']}/versions/{draft['id']}", self.super, "PATCH", {
            "provider": "ollama", "model": model, "systemPrompt": f"prompt:{model}",
            "temperature": 0.35, "maxTurns": 5, "timeoutSeconds": 90, "maxTokens": 1500,
            "memoryConfig": {"mode": "session"}, "toolIds": [], "skills": ["phase3b-test-skill"]})
        self.assertEqual(200, status, edited)
        return edited

    def publish(self, agent_id, version_id):
        status, validation = call(f"/system/agents/{agent_id}/versions/{version_id}/validate", self.super, "POST")
        self.assertEqual(200, status, validation)
        self.assertTrue(validation["valid"], validation)
        status, published = call(f"/system/agents/{agent_id}/versions/{version_id}/publish", self.super, "POST")
        self.assertEqual(200, status, published)
        return published

    def test_dynamic_agent_lifecycle_assignments_and_manifest(self):
        # Only SUPER_ADMIN can manage system definitions.
        probe = {"slug": f"denied-{uuid.uuid4().hex[:8]}", "name": "Denied"}
        self.assertEqual(403, call("/system/agents", self.admin_a, "POST", probe)[0])
        self.assertEqual(403, call("/system/agents", self.user_a, "POST", probe)[0])

        agent = self.create_agent("main")
        draft1 = self.configure_draft(agent)
        published1 = self.publish(agent["id"], draft1["id"])

        # Published snapshots are immutable.
        status, _ = call(f"/system/agents/{agent['id']}/versions/{published1['id']}", self.super, "PATCH", {"model": "tampered"})
        self.assertEqual(409, status)

        # A new v2 is independent and publishing it preserves v1.
        status, draft2 = call(f"/system/agents/{agent['id']}/versions", self.super, "POST", {
            "provider": "ollama", "model": "phase3b-model-v2", "systemPrompt": "prompt:v2",
            "temperature": 0.45, "maxTurns": 6, "timeoutSeconds": 100, "maxTokens": 1600,
            "memoryConfig": {"mode": "session-v2"}, "toolIds": [self.tool["id"]], "skills": ["phase3b-v2-skill"]})
        self.assertEqual(201, status, draft2)
        published2 = self.publish(agent["id"], draft2["id"])
        status, versions = call(f"/system/agents/{agent['id']}/versions", self.super)
        self.assertEqual(200, status)
        self.assertEqual({published1["id"], published2["id"]}, {v["id"] for v in versions["items"] if v["status"] == "published"})

        # Tenant assignment is system-controlled; user entitlement is separate and tenant-bound.
        self.assertEqual(204, call(f"/system/agents/{agent['id']}/tenants/{self.tenant_a}", self.super, "PUT")[0])
        self.assertEqual(204, call(f"/tenant/agents/{agent['id']}/users/{self.user_a_id}", self.admin_a, "PUT")[0])
        self.assertEqual(400, call(f"/tenant/agents/{agent['id']}/users/{self.user_b_id}", self.admin_a, "PUT")[0])

        # The manifest is authorization-bound and takes no tenant/provider/model/version input.
        status, manifest = call(f"/runtime/agents/{agent['id']}/manifest?tenant_id={self.tenant_b}&provider=claude_sdk&model=forged&version=1", self.user_a)
        self.assertEqual(200, status, manifest)
        self.assertEqual(self.tenant_a, manifest["tenantId"])
        self.assertEqual(self.user_a_id, manifest["userId"])
        self.assertEqual(published2["id"], manifest["publishedVersionId"])
        self.assertEqual(2, manifest["version"])
        self.assertEqual("ollama", manifest["provider"])
        self.assertEqual("phase3b-model-v2", manifest["model"])
        self.assertEqual(["phase3b-v2-skill"], manifest["skills"])
        self.assertEqual([self.tool["id"]], [tool["id"] for tool in manifest["tools"]])
        self.assertEqual(403, call(f"/runtime/agents/{agent['id']}/manifest", self.user_b)[0])

        # Disable and revocation gates fail closed.
        self.assertEqual(200, call(f"/system/agents/{agent['id']}/disable", self.super, "POST")[0])
        self.assertEqual(403, call(f"/runtime/agents/{agent['id']}/manifest", self.user_a)[0])
        self.assertEqual(200, call(f"/system/agents/{agent['id']}/enable", self.super, "POST")[0])
        self.assertEqual(204, call(f"/tenant/agents/{agent['id']}/users/{self.user_a_id}", self.admin_a, "DELETE")[0])
        self.assertEqual(403, call(f"/runtime/agents/{agent['id']}/manifest", self.user_a)[0])

        # A tenant-assigned draft without a published pointer remains non-executable.
        unpublished = self.create_agent("unpublished")
        self.configure_draft(unpublished)
        self.assertEqual(204, call(f"/system/agents/{unpublished['id']}/tenants/{self.tenant_a}", self.super, "PUT")[0])
        self.assertEqual(204, call(f"/tenant/agents/{unpublished['id']}/users/{self.user_a_id}", self.admin_a, "PUT")[0])
        self.assertEqual(403, call(f"/runtime/agents/{unpublished['id']}/manifest", self.user_a)[0])

        # Revoking tenant access also revokes all tenant user assignments.
        self.assertEqual(204, call(f"/system/agents/{unpublished['id']}/tenants/{self.tenant_a}", self.super, "DELETE")[0])
        self.assertEqual(403, call(f"/runtime/agents/{unpublished['id']}/manifest", self.user_a)[0])


if __name__ == "__main__":
    unittest.main()
