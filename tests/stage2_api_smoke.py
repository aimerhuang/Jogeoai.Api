"""Stage 2 HTTP smoke against an explicitly migrated isolated MySQL database.

Requires STAGE2_API_BASE and STAGE2_TENANT_ID. Creates records and never deletes them.
The API must target a test database with migrations 001-006; no Provider is called.
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid


base = os.environ["STAGE2_API_BASE"].rstrip("/")
tenant = os.environ["STAGE2_TENANT_ID"]
suffix = uuid.uuid4().hex[:12]


def call(method, path, body=None, *, expected=200, tenant_id=tenant, key=None):
    headers = {"X-Tenant-Id": tenant_id, "Content-Type": "application/json"}
    if key:
        headers["Idempotency-Key"] = key
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(base + "/api/v1" + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request) as response:
            status, payload = response.status, json.load(response)
    except urllib.error.HTTPError as response:
        status, payload = response.code, json.loads(response.read())
    assert status == expected, (method, path, status, payload)
    return payload


brand = call("POST", "/sites", {"name": "Stage2 Acme", "primaryDomain": f"{suffix}.example.test"}, expected=201)
site = brand["siteId"]
competitor = call("POST", f"/sites/{site}/competitors", {"name": "Stage2 Beta"}, expected=201)
prompt = call("POST", f"/sites/{site}/prompts", {"content": "Which tools are best?"}, expected=201)

overview = call("GET", f"/sites/{site}/analytics/overview")
assert overview["metrics"]["eligibleChecks"] == 0
assert overview["metrics"]["visibility"]["value"] is None
token = overview["meta"]["snapshotToken"]
assert token and overview["meta"]["subjectConfigVersion"] == 2
prompt_detail = call("GET", f"/sites/{site}/analytics/prompts/{prompt['promptId']}?snapshotToken={token}")
assert prompt_detail["promptId"] == prompt["promptId"]
assert len(prompt_detail["versions"]) == 1
assert prompt_detail["versions"][0]["content"] == "Which tools are best?"
assert prompt_detail["metrics"]["eligibleChecks"] == 0
assert prompt_detail["metrics"]["visibility"]["value"] is None
assert token in prompt_detail["checkHistoryPath"]
assert call("GET", f"/sites/{site}/analytics/prompts/{uuid.uuid4()}", expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"
assert call("GET", f"/sites/{site}/analytics/prompts/{prompt['promptId']}",
            tenant_id=str(uuid.uuid4()), expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"
call("PATCH", f"/sites/{site}/prompts/{prompt['promptId']}",
     {"content": "Which revised tools are best?", "expectedVersion": prompt["configVersion"]})
assert len(call("GET", f"/sites/{site}/analytics/prompts/{prompt['promptId']}?snapshotToken={token}")["versions"]) == 1
assert [v["content"] for v in call("GET", f"/sites/{site}/analytics/prompts/{prompt['promptId']}")["versions"]] == [
    "Which tools are best?", "Which revised tools are best?"
]
assert call("GET", f"/sites/{site}/analytics/overview?" + urllib.parse.urlencode({
    "snapshotToken": token, "provider": "cloro"
}))["meta"]["snapshotToken"] == token
visibility = call("GET", f"/sites/{site}/analytics/visibility?aggregationMode=latest_observation")
assert visibility["metrics"]["eligibleChecks"] == 0
assert visibility["trend"][0]["visibility"]["value"] is None
sov = call("GET", f"/sites/{site}/analytics/share-of-voice?" + urllib.parse.urlencode({"snapshotToken": token, "topN": 1}))
assert len(sov["entries"]) == 2 and {entry["subjectId"] for entry in sov["entries"]} == {
    site, competitor["competitorId"]
}
assert all(entry["mentions"] == 0 for entry in sov["entries"])
page1 = call("GET", f"/sites/{site}/analytics/share-of-voice/leaderboard?" + urllib.parse.urlencode({
    "snapshotToken": token, "limit": 1
}))
assert len(page1["items"]) == 1 and page1["nextCursor"]
page2 = call("GET", f"/sites/{site}/analytics/share-of-voice/leaderboard?" + urllib.parse.urlencode({
    "snapshotToken": token, "limit": 1, "cursor": page1["nextCursor"]
}))
assert len(page2["items"]) == 1 and not page2["nextCursor"]
assert page1["items"][0]["subjectId"] != page2["items"][0]["subjectId"]
assert call("GET", f"/sites/{site}/analytics/share-of-voice/leaderboard?" + urllib.parse.urlencode({
    "snapshotToken": token, "limit": 1, "cursor": "bad"
}), expected=400)["error"]["code"] == "INVALID_CURSOR"
assert call("GET", f"/sites/{site}/analytics/overview?unsupported=1", expected=400)["error"]["code"] == "INVALID_FILTER"
assert call("GET", f"/sites/{site}/analytics/overview?from=not-a-date", expected=400)["error"]["code"] == "INVALID_TIME_RANGE"
assert call("GET", f"/sites/{site}/analytics/overview", tenant_id=str(uuid.uuid4()), expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"
assert call("GET", f"/sites/{site}/prompts/{prompt['promptId']}/checks")["items"] == []

job = call("POST", f"/sites/{site}/analysis-jobs", {}, key=f"stage2-{suffix}", expected=202)
assert job["status"] == "published" and job["totalChecks"] == 0 and job["effectiveScope"] == "site"
assert call("GET", f"/sites/{site}/analysis-jobs/{job['jobId']}")["jobId"] == job["jobId"]
assert call("POST", f"/sites/{site}/analysis-jobs", {}, key=f"stage2-{suffix}", expected=202)["jobId"] == job["jobId"]
direct_brand = call("POST", "/sites", {"name": "Stage2 Direct", "primaryDomain": f"direct-{suffix}.example.test"}, expected=201)
direct_job = call("POST", f"/sites/{direct_brand['siteId']}/analysis-jobs", {},
                  key=f"stage2-direct-{suffix}", expected=202)
assert direct_job["status"] == "published" and direct_job["totalChecks"] == 0

with urllib.request.urlopen(base + "/swagger/v1/swagger.json") as response:
    swagger = json.load(response)
assert "/api/v1/sites/{siteId}/analytics/overview" in swagger["paths"]
assert "概览" in swagger["paths"]["/api/v1/sites/{siteId}/analytics/overview"]["get"]["summary"]
assert "Prompt 的历史版本" in swagger["paths"]["/api/v1/sites/{siteId}/analytics/prompts/{promptId}"]["get"]["summary"]
print("stage2_api_smoke: passed")
