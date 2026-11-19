"""Stage 1 API smoke against an explicitly prepared isolated MySQL test database.

Requires STAGE1_API_BASE and STAGE1_TENANT_ID. The API must point to the test DB,
which needs 001-022, one active test tenant, a test-only Cloro settings row and at
least one enabled test key in cloro_api_keys.
STAGE1_ALLOW_SETTINGS_MUTATION=1 additionally requires STAGE1_ADMIN_TOKEN: a session
token of a test system administrator, because the Cloro settings API is admin-only.
This script creates test rows and intentionally does not delete them.
"""

import json
import os
import uuid
import urllib.error
import urllib.parse
import urllib.request


base = os.environ["STAGE1_API_BASE"].rstrip("/") + "/api/v1"
tenant = os.environ["STAGE1_TENANT_ID"]
suffix = uuid.uuid4().hex[:12]
settings_mutation = os.environ.get("STAGE1_ALLOW_SETTINGS_MUTATION") == "1"
admin_token = os.environ["STAGE1_ADMIN_TOKEN"] if settings_mutation else None


def call(method, path, body=None, key=None, expected=200, tenant_id=tenant, token=None):
    headers = {"X-Tenant-Id": tenant_id, "Content-Type": "application/json"}
    if key:
        headers["Idempotency-Key"] = key
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(base + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request) as response:
            status, payload = response.status, json.load(response)
    except urllib.error.HTTPError as response:
        status, payload = response.code, json.loads(response.read())
    assert status == expected, (method, path, status, payload)
    return payload


brand = call("POST", "/sites", {
    "name": "Smoke Brand", "primaryDomain": f"{suffix}.example.test",
    "aliases": ["测试品牌"], "additionalDomains": [f"{suffix}.cn.test"],
}, expected=201)
site = brand["siteId"]
assert call("GET", f"/sites/{site}")["siteId"] == site
second_brand = call("POST", "/sites", {
    "name": "Smoke Brand 2", "primaryDomain": f"second-{suffix}.example.test",
}, expected=201)
sites = call("GET", "/sites?limit=1")
assert len(sites["items"]) == 1 and sites["nextCursor"]
assert call("GET", "/sites?" + urllib.parse.urlencode({"limit": 1, "cursor": sites["nextCursor"]}))["items"]
other_tenant = str(uuid.uuid4())
assert call("GET", f"/sites/{site}", expected=404, tenant_id=other_tenant)["error"]["code"] == "RESOURCE_NOT_FOUND"
assert call("POST", f"/sites/{site}/competitors", {
    "name": "Overlap", "domains": [f"sub.{suffix}.example.test"],
}, expected=409)["error"]["code"] == "SUBJECT_TERM_CONFLICT"
call("POST", f"/sites/{site}/competitors", {
    "name": "Rival", "aliases": ["竞争品牌"], "domains": [f"{suffix}.example.test.evil.test"],
}, expected=201)
call("POST", f"/sites/{site}/competitors", {"name": "Rival Two"}, expected=201)
competitors = call("GET", f"/sites/{site}/competitors?limit=1")
assert len(competitors["items"]) == 1 and competitors["nextCursor"]
assert call("GET", f"/sites/{site}/competitors?" + urllib.parse.urlencode({"limit": 1, "cursor": competitors["nextCursor"]}))["items"]
tag = call("POST", f"/sites/{site}/tags", {"name": "Research"}, expected=201)
call("POST", f"/sites/{site}/tags", {"name": "Comparison"}, expected=201)
tags = call("GET", f"/sites/{site}/tags?limit=1")
assert len(tags["items"]) == 1 and tags["nextCursor"]
assert call("GET", f"/sites/{site}/tags?" + urllib.parse.urlencode({"limit": 1, "cursor": tags["nextCursor"]}))["items"]
assert call("GET", f"/sites/{site}/tags?" + urllib.parse.urlencode({"limit": 1, "cursor": competitors["nextCursor"]}),
            expected=400)["error"]["code"] == "INVALID_CURSOR"
prompt1 = call("POST", f"/sites/{site}/prompts", {
    "content": "Which Smoke Brand tools are best?", "tagIds": [tag["tagId"]],
}, expected=201)
prompt2 = call("POST", f"/sites/{site}/prompts", {
    "content": "Compare pricing", "tagIds": [tag["tagId"]],
}, expected=201)
assert call("PATCH", f"/sites/{site}/prompts/{prompt1['promptId']}", {
    "enabled": False, "expectedVersion": 99,
}, expected=409)["error"]["code"] == "CONFIG_VERSION_CONFLICT"

rows = [{"content": prompt1["content"]}, {"content": "New imported question"}]
import_preview = call("POST", f"/sites/{site}/prompts/import-preview", rows)
assert (import_preview["newCount"], import_preview["duplicateCount"]) == (1, 1)
import_key = "stage1-import-" + suffix
imported = call("POST", f"/sites/{site}/prompts/import", rows, key=import_key)
assert (imported["createdCount"], imported["duplicateCount"]) == (1, 1)
assert call("POST", f"/sites/{site}/prompts/import", rows, key=import_key) == imported
assert call("POST", f"/sites/{site}/prompts/import", [{"content": "different"}],
            key=import_key, expected=409)["error"]["code"] == "IMPORT_IDEMPOTENCY_CONFLICT"

targets = call("PUT", f"/sites/{site}/targets", {
    "expectedVersion": brand["targetConfigVersion"],
    "targets": [
        {"provider": "cloro", "engine": "chatgpt", "country": "US", "locale": "en-US"},
        {"provider": "cloro", "engine": "chatgpt", "country": "GB", "locale": "en-GB"},
        {"provider": "cloro", "engine": "gemini", "country": "US", "locale": "en-US"},
    ],
})
assert len(targets["items"]) == 3 and targets["configVersion"] == 2
assert call("PUT", f"/sites/{site}/targets", {
    "expectedVersion": 1, "targets": [],
}, expected=409)["error"]["code"] == "CONFIG_VERSION_CONFLICT"
selection = {"promptIds": [prompt1["promptId"], prompt2["promptId"]],
             "targetIds": [item["targetId"] for item in targets["items"]], "sampleCount": 2}
preview = call("POST", f"/sites/{site}/collection-previews", selection)
assert preview["expectedChecks"] == 12 and preview["estimatedAmount"] is None
run_body = {"siteId": site, "previewId": preview["previewId"]}
run_key = "stage1-run-" + suffix
run = call("POST", "/visibility-runs", run_body, key=run_key, expected=202)
assert run["totalChecks"] == 12
assert call("POST", "/visibility-runs", run_body, key=run_key, expected=202)["runId"] == run["runId"]
checks = call("GET", f"/visibility-runs/{run['runId']}/checks")["items"]
assert len(checks) == 12
assert len({item["runTargetId"] for item in checks}) == 3
assert {item["sampleIndex"] for item in checks} == {1, 2}
assert all(item["promptVersionId"] and item["promptTagsSnapshot"] for item in checks)
assert call("GET", f"/visibility-runs/{run['runId']}")["expectedChecks"] == 12

updated = call("PATCH", f"/sites/{site}/prompts/{prompt1['promptId']}", {
    "content": "Which new Smoke Brand tools are best?", "expectedVersion": prompt1["configVersion"],
})
assert updated["version"] == 2
assert sum(item["promptSnapshot"] == prompt1["content"] for item in checks) == 6
assert call("POST", "/visibility-runs", run_body, key=run_key, expected=202)["runId"] == run["runId"]
call("PUT", f"/sites/{site}/targets", {"expectedVersion": targets["configVersion"], "targets": []})
assert call("POST", "/visibility-runs", run_body, key=run_key, expected=202)["runId"] == run["runId"]
assert call("POST", "/visibility-runs", {"siteId": site, "previewId": str(uuid.uuid4())},
            key=run_key, expected=409)["error"]["code"] == "RUN_IDEMPOTENCY_CONFLICT"
assert call("POST", "/visibility-runs", run_body, key="stale-preview-" + suffix,
            expected=409)["error"]["code"] == "PREVIEW_EXPIRED"

old_cloro = call("POST", "/visibility-runs", {
    "siteId": site, "promptIds": [prompt2["promptId"]],
    "targets": [{"provider": "cloro", "engine": "copilot"}],
}, key="old-cloro-" + suffix, expected=202)
assert old_cloro["totalChecks"] == 1
old_cloro_body = {
    "siteId": site, "promptIds": [prompt2["promptId"]],
    "targets": [{"provider": "cloro", "engine": "copilot"}],
}
assert call("POST", "/visibility-runs", old_cloro_body, key="old-cloro-" + suffix, expected=202)["runId"] == old_cloro["runId"]
assert call("POST", "/visibility-runs", {
    "siteId": site, "promptIds": [prompt2["promptId"]],
    "targets": [{"provider": "cloro", "engine": "copilot", "options": {}}],
}, key="old-cloro-" + suffix, expected=202)["runId"] == old_cloro["runId"]
normalized_locale_body = {
    "siteId": site, "promptIds": [prompt2["promptId"]],
    "targets": [{"provider": "cloro", "engine": "chatgpt", "locale": "en-us"}],
}
normalized_locale_run = call("POST", "/visibility-runs", normalized_locale_body,
                             key="locale-" + suffix, expected=202)
normalized_locale_body["targets"][0]["locale"] = "en-US"
assert call("POST", "/visibility-runs", normalized_locale_body,
            key="locale-" + suffix, expected=202)["runId"] == normalized_locale_run["runId"]
old_domestic_body = {
    "siteId": site, "promptIds": [prompt2["promptId"]],
    "platforms": [{"platformCode": "doubao", "entryCode": "doubao.consumer_web"}],
}
old_domestic = call("POST", "/visibility-runs", old_domestic_body, key="old-domestic-" + suffix, expected=202)
assert old_domestic["totalChecks"] == 1
domestic_check = call("GET", f"/visibility-runs/{old_domestic['runId']}/checks")["items"][0]
assert domestic_check["promptVersionId"]
assert domestic_check["promptTagsSnapshot"] == ["Research"]
assert domestic_check["brandedStatusSnapshot"] in ("branded", "unbranded")
assert domestic_check["brandedSourceSnapshot"] in ("system", "manual")
assert domestic_check["subjectConfigVersion"] and not domestic_check["snapshotMissing"]
assert call("POST", "/visibility-runs", old_domestic_body, key="old-domestic-" + suffix, expected=202)["runId"] == old_domestic["runId"]

runs = call("GET", f"/sites/{site}/visibility-runs?limit=1")
assert len(runs["items"]) == 1 and runs["nextCursor"]
assert call("GET", f"/sites/{site}/visibility-runs?" + urllib.parse.urlencode({"limit": 1, "cursor": runs["nextCursor"]}))["items"]
assert call("GET", f"/sites/{site}/visibility-runs?" + urllib.parse.urlencode({"provider": "cloro"}))["items"]
assert call("GET", f"/sites/{site}/visibility-runs?" + urllib.parse.urlencode({"provider": "domestic_collector"}))["items"]
assert call("GET", f"/sites/{site}/visibility-runs?" + urllib.parse.urlencode({"provider": "cloro", "cursor": runs["nextCursor"]}),
            expected=400)["error"]["code"] == "INVALID_CURSOR"
assert call("GET", f"/sites/{uuid.uuid4()}/visibility-runs", expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"
assert call("GET", f"/sites/{site}/visibility-runs", tenant_id=other_tenant, expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"

if settings_mutation:
    # The URL/switch is all that changes; the test key stays in the pool and no apiKey is sent.
    settings = {"apiUrl": "http://127.0.0.1:9999", "enabled": False}
    call("PUT", "/providers/cloro/settings", settings, expected=401)
    call("PUT", "/providers/cloro/settings", settings, token=admin_token)
    try:
        assert call("POST", "/visibility-runs", run_body, key=run_key, expected=202)["runId"] == run["runId"]
        assert call("POST", "/visibility-runs", old_cloro_body, key="old-cloro-" + suffix, expected=202)["runId"] == old_cloro["runId"]
        assert call("POST", "/visibility-runs", old_cloro_body, key="disabled-cloro-" + suffix,
                    expected=503)["error"]["code"] == "CLORO_NOT_CONFIGURED"
    finally:
        settings["enabled"] = True
        call("PUT", "/providers/cloro/settings", settings, token=admin_token)

latest_brand = call("GET", f"/sites/{site}")
call("PATCH", f"/sites/{site}", {"status": "archived", "expectedVersion": latest_brand["subjectConfigVersion"]})
assert call("GET", f"/sites/{site}")["status"] == "archived"
assert call("GET", f"/sites/{site}/competitors")["items"]
assert call("GET", f"/sites/{site}/tags")["items"]
assert call("GET", f"/sites/{site}/prompts")["items"]
assert call("GET", f"/sites/{site}/visibility-runs")["items"]
assert call("GET", f"/visibility-runs/{run['runId']}")["runId"] == run["runId"]
assert call("GET", f"/visibility-runs/{run['runId']}/checks")["items"]

print("stage1 API smoke passed", {"siteId": site, "runId": run["runId"], "checks": len(checks),
                                  "oldCloroRunId": old_cloro["runId"], "oldDomesticRunId": old_domestic["runId"],
                                  "legacyCloroKey": "old-cloro-" + suffix, "legacyDomesticKey": "old-domestic-" + suffix,
                                  "prompt2Id": prompt2["promptId"]})
