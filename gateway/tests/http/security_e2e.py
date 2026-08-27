"""Live HTTP authorization smoke tests.

Required environment: AIQ_BASE_URL plus ADMIN_A_EMAIL/PASSWORD, USER_A_EMAIL/
PASSWORD, TENANT_A_ID, TENANT_B_ID, ASSIGNED_AGENT_ID, UNASSIGNED_AGENT_ID,
DISABLED_AGENT_ID, UNPUBLISHED_AGENT_ID, and TENANT_B_AGENT_ID.
"""
import json
import os
import unittest
import urllib.error
import urllib.request


BASE = os.getenv("AIQ_BASE_URL", "http://localhost:8080").rstrip("/")


def call(path, token=None, method="GET", body=None):
    data = None if body is None else json.dumps(body).encode()
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request)
        return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, None


def login(prefix):
    status, result = call("/auth/login", method="POST", body={
        "email": os.environ[f"{prefix}_EMAIL"],
        "password": os.environ[f"{prefix}_PASSWORD"],
    })
    assert status == 200
    return result["token"]


@unittest.skipUnless(os.getenv("RUN_AIQ_HTTP_E2E") == "1", "live fixture test")
class SecurityE2E(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.admin = login("ADMIN_A")
        cls.user = login("USER_A")

    def test_admin_cannot_switch_or_cross_tenant(self):
        status, _ = call("/me/tenants/switch", self.admin, "POST",
                         {"tenantId": os.environ["TENANT_B_ID"]})
        self.assertEqual(404, status)
        for suffix in ("", "/acl", "/tools", "/knowledge", "/hitl/gates"):
            status, _ = call(f"/agents/{os.environ['TENANT_B_AGENT_ID']}{suffix}", self.admin)
            self.assertIn(status, (403, 404))

    def test_user_agent_entitlements(self):
        status, result = call("/me/agents", self.user)
        self.assertEqual(200, status)
        ids = {item["agentId"] for item in result["items"]}
        self.assertIn(os.environ["ASSIGNED_AGENT_ID"], ids)
        for name in ("UNASSIGNED_AGENT_ID", "DISABLED_AGENT_ID",
                     "UNPUBLISHED_AGENT_ID", "TENANT_B_AGENT_ID"):
            status, _ = call(f"/agents/{os.environ[name]}/execution-authorization", self.user)
            self.assertIn(status, (403, 404))

    def test_tenant_forgery_is_ignored(self):
        status, result = call(f"/me/agents?tenant_id={os.environ['TENANT_B_ID']}", self.user)
        self.assertEqual(200, status)
        ids = {item["agentId"] for item in result["items"]}
        self.assertIn(os.environ["ASSIGNED_AGENT_ID"], ids)
        self.assertNotIn(os.environ["TENANT_B_AGENT_ID"], ids)


if __name__ == "__main__":
    unittest.main()
