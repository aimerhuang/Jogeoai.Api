"""Read-only smoke test for the dashboard endpoints.

The script never creates or modifies business data.  It requires a site that
already exists in an isolated or staging environment:

    DASHBOARD_API_BASE=http://localhost:5260 \
    DASHBOARD_TENANT_ID=<tenant> DASHBOARD_SITE_ID=<site> \
    python tests/dashboard_api_smoke.py
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid


base = os.environ["DASHBOARD_API_BASE"].rstrip("/")
tenant = os.environ["DASHBOARD_TENANT_ID"]
site = os.environ["DASHBOARD_SITE_ID"]
prefix = f"/api/v1/sites/{site}"


def call(path, *, expected=200, tenant_id=tenant):
    request = urllib.request.Request(
        base + path,
        method="GET",
        headers={"X-Tenant-Id": tenant_id, "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(request) as response:
            status, payload = response.status, json.load(response)
    except urllib.error.HTTPError as response:
        status, payload = response.code, json.loads(response.read())
    assert status == expected, (path, status, payload)
    return payload


summary = call(prefix + "/monitoring/summary")
models = call(prefix + "/monitoring/models")
snapshot = summary["meta"]["snapshotToken"]
snapshot_query = urllib.parse.urlencode({"snapshotToken": snapshot})
records = call(prefix + "/monitoring/records?limit=2&" + snapshot_query)
dashboard = call(prefix + "/analytics/brand-dashboard?" + snapshot_query)
breakdowns = call(prefix + "/analytics/brand-dashboard/breakdowns?limit=2&" + snapshot_query)

assert snapshot == dashboard["meta"]["snapshotToken"]
assert snapshot == records["meta"]["snapshotToken"]
assert snapshot == breakdowns["meta"]["snapshotToken"]
assert isinstance(summary["totalChecks"], int)
assert isinstance(models, list)
assert "metrics" in dashboard and "quality" in dashboard
assert isinstance(records["items"], list)
for item in records["items"]:
    assert item["detailsPath"].startswith("/api/v1/visibility-checks/")
    assert "apiKey" not in json.dumps(item, sort_keys=True).lower()

invalid = call(prefix + "/monitoring/summary?aggregationMode=unsupported", expected=400)
assert invalid["error"]["code"] == "INVALID_FILTER"
expired = call(prefix + "/monitoring/summary?snapshotToken=expired-token", expected=409)
assert expired["error"]["code"] == "SNAPSHOT_EXPIRED"
cross_tenant = call(prefix + "/analytics/brand-dashboard", expected=404, tenant_id=str(uuid.uuid4()))
assert cross_tenant["error"]["code"] == "RESOURCE_NOT_FOUND"

print("dashboard_api_smoke: passed")
