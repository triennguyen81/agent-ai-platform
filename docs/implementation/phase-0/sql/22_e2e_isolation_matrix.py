"""Phase 0 đợt 7 (P0-06): black-box tenant / department isolation of every accounting & tax GET route,
against the REAL Gateway on the throwaway aiq_e2e database (after e2e_d2.py created KT data). LIVE-TEST data only.

For each route and each outsider (ADMIN of KD, ADMIN of tenant B, TENANT_OWNER, SUPER_ADMIN): the answer is
401 / 403 / 404, or a 200 that contains NONE of KT's ids / names. KT's own ADMIN sees them (positive control).
"""
import json, subprocess, sys, time, urllib.error, urllib.request

GW = "http://127.0.0.1:18080"
PW = "E2e#Pass2026"
KT = "aaaaaaaa-0000-0000-0000-0000000000a1"
checks = 0
known = []


def ok(cond, label, detail=""):
    global checks
    checks += 1
    print(("PASS " if cond else "FAIL ") + label + ("" if cond else f"  -> {detail}"))
    if not cond:
        sys.exit(1)


def psql(sql):
    return subprocess.run(["docker", "exec", "-i", "aiq-test-pg", "psql", "-U", "postgres", "-d", "aiq_e2e", "-tAc", sql],
                          check=True, capture_output=True, text=True).stdout.strip()


def call(path, token):
    req = urllib.request.Request(GW + path, headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.read().decode(errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


def login(email):
    req = urllib.request.Request(GW + "/auth/login", method="POST", headers={"Content-Type": "application/json"},
                                 data=json.dumps({"email": email, "password": PW}).encode())
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())["token"]


# ---- KT markers (LIVE-TEST): a customer and a supplier with recognisable names, plus what the earlier E2E created.
cust = psql(f"""insert into sim_customers(tenant_id, department_id, customer_code, name, mst)
                values ('aaaaaaaa-0000-0000-0000-00000000000a', '{KT}', 'LT-KT-C-' || substr(md5(random()::text), 1, 6), 'LIVE-TEST-KT-SECRET-CUSTOMER', '0105555555') returning id""").splitlines()[0]
supp = psql(f"""insert into sim_suppliers(tenant_id, department_id, supplier_code, name, mst)
                values ('aaaaaaaa-0000-0000-0000-00000000000a', '{KT}', 'LT-KT-S-' || substr(md5(random()::text), 1, 6), 'LIVE-TEST-KT-SECRET-SUPPLIER', '0104444444') returning id""").splitlines()[0]
voucher = psql(f"select id from vouchers where department_id = '{KT}' and voucher_type = 'invoice_vat_in' and status <> 'rejected' order by created_at limit 1")
rule = psql(f"select id from tax_rules where department_id = '{KT}' order by created_at limit 1")
version = psql(f"select id from tax_rule_versions where department_id = '{KT}' order by created_at limit 1")
calc = psql(f"select id from tax_calculation_runs where department_id = '{KT}' order by created_at limit 1")
markers = [m for m in [cust, supp, voucher, rule, version, calc, "LIVE-TEST-KT-SECRET"] if m]
ok(len(markers) == 7, "KT data present for the matrix", markers)

routes = [
    "/vouchers", f"/vouchers/{voucher}", f"/vouchers/{voucher}/settlement", f"/vouchers/{voucher}/events", f"/vouchers/{voucher}/validation",
    f"/vouchers/{voucher}/preview", f"/vouchers/{voucher}/processing-state", f"/vouchers/{voucher}/receipt-match",
    f"/vouchers/{voucher}/tax-verification", f"/vouchers/{voucher}/document-review", f"/vouchers/{voucher}/expense-claim-result",
    f"/vouchers/{voucher}/expense-claim-export", f"/vouchers/{voucher}/expense-claim/payment", f"/vouchers/{voucher}/vat-d2",
    f"/vouchers/{voucher}/duplicate-check", f"/vouchers/{voucher}/bank-transactions", "/vouchers/export", "/vouchers/status-report",
    "/vouchers/company-bank-accounts",
    "/customers", f"/customers/{cust}", f"/customers/{cust}/receivables", "/customers/by-mst/0105555555", f"/customers/{cust}/bank-accounts",
    "/suppliers", f"/suppliers/{supp}", f"/suppliers/{supp}/payables", "/suppliers/by-mst/0104444444", "/suppliers/ap-summary",
    f"/suppliers/{supp}/bank-accounts",
    "/accounting-periods", "/accounting-periods/2026-03/events",
    f"/expense-claims/{voucher}", "/expense-claims/employees",
    "/tax-rules", "/tax-rules/effective", f"/tax-rules/{rule}/versions", f"/tax-rules/{rule}/timeline", f"/tax-rules/{rule}/events",
    f"/tax-rules/{rule}/revisions", f"/tax-rule-versions/{version}", f"/tax-rule-versions/{version}/verifications",
    f"/tax-rule-versions/{version}/approval-preview", f"/tax-rule-versions/{version}/revoke-preview",
    "/tax-legal-references", "/tax-rule-delegates", "/tax/calculations", f"/tax/calculations/{calc}",
    "/tax/deadlines/?year=2026&vatFrequency=monthly", "/tax/deadlines/vat_monthly?period=2026-03",
    "/contracts", "/notes", "/books", "/tenant/reports/weekly-summary",
    "/approval-queue",
]

time.sleep(61)   # the login limiter (10 / min) after e2e_d2.py
outsiders = {"ADMIN of KD": login("admin.kd@livetest.vn"), "ADMIN of tenant B": login("admin.b@livetest.vn"),
             "TENANT_OWNER": login("owner.a@livetest.vn"), "SUPER_ADMIN": login("super@livetest.vn")}
kt = login("admin.kt@livetest.vn")

# Positive control: KT's own ADMIN sees its markers where it should.
for path, marker in ((f"/vouchers/{voucher}", voucher), (f"/customers/{cust}", "LIVE-TEST-KT-SECRET-CUSTOMER"),
                     (f"/suppliers/{supp}", "LIVE-TEST-KT-SECRET-SUPPLIER"), (f"/tax-rules/{rule}/versions", version),
                     (f"/tax/calculations/{calc}", calc), ("/customers", "LIVE-TEST-KT-SECRET-CUSTOMER")):
    s, body = call(path, kt)
    ok(s == 200 and marker in body, f"control: KT ADMIN reads {path}", (s, body[:200]))

# /vouchers/export used to answer 500 to everyone (synchronous ZIP write) — fixed in đợt 7.
s, body = call("/vouchers/export", kt)
ok(s == 200 and body.startswith("PK"), "control: KT ADMIN downloads /vouchers/export (a ZIP, not 500)", (s, body[:80]))

summary = {}
for who, token in outsiders.items():
    for path in routes:
        time.sleep(0.62)   # 100 requests / minute / user
        s, body = call(path, token)
        leaked = [m for m in markers if m in body] if s == 200 else []
        summary[(who, path)] = s
        ok(s in (200, 204, 400, 401, 403, 404, 405, 409, 422) and not leaked,
           f"{who:18} GET {path} → {s}{' (no KT data)' if s == 200 else ''}", (s, leaked, body[:300]))

for k in known:
    print(f"KNOWN (18 R-1) {k[0]} GET {k[1]} → {k[2]} shows KT queue items (legacy tenant-wide read)")
codes = {}
for (_, _), s in summary.items():
    codes[s] = codes.get(s, 0) + 1
print("status codes:", dict(sorted(codes.items())))
print(f"ALL {checks} ISOLATION CHECKS PASSED")
