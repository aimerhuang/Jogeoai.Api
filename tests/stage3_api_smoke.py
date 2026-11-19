"""Stage 3 HTTP smoke against an explicitly migrated isolated MySQL test database.

Requires STAGE3_API_BASE and STAGE3_TENANT_ID. Creates only synthetic site data.
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid


base = os.environ["STAGE3_API_BASE"].rstrip("/")
tenant = os.environ["STAGE3_TENANT_ID"]


def call(path, *, method="GET", body=None, expected=200, tenant_id=tenant):
    headers = {"X-Tenant-Id": tenant_id, "Content-Type": "application/json"}
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(base + "/api/v1" + path, method=method, data=data, headers=headers)
    try:
        with urllib.request.urlopen(request) as response:
            status, payload = response.status, json.load(response)
    except urllib.error.HTTPError as response:
        status, payload = response.code, json.loads(response.read())
    assert status == expected, (path, status, payload)
    return payload


brand = call("/sites", method="POST", expected=201,
             body={"name": "Stage3 Smoke", "primaryDomain": f"{uuid.uuid4().hex}.smoke.test"})
site = brand["siteId"]
prefix = f"/sites/{site}/analytics"
fan = call(prefix + "/query-fanout")
token = fan["meta"]["snapshotToken"]
assert fan["metrics"]["queryEvents"] == 0
assert fan["metrics"]["queriesPerSuccessfulCheck"]["value"] is None
assert fan["queryVersion"] == "query-v1"
assert call(prefix + "/query-fanout/queries?" + urllib.parse.urlencode({"snapshotToken": token}))["items"] == []
assert call(prefix + "/query-fanout/terms?" + urllib.parse.urlencode({"snapshotToken": token}))["items"] == []
assert call(prefix + "/evidence?" + urllib.parse.urlencode({"snapshotToken": token, "queryHash": "a" * 64}))["items"] == []
assert call(prefix + "/query-fanout/queries/" + "a" * 64, expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"
assert call(prefix + "/query-fanout?aggregationMode=latest_observation", expected=400)["error"]["code"] == "INVALID_FILTER"
assert call(prefix + "/query-fanout/queries?cursor=bad", expected=400)["error"]["code"] == "INVALID_CURSOR"
assert call(prefix + "/query-fanout", expected=404,
            tenant_id=str(uuid.uuid4()))["error"]["code"] == "RESOURCE_NOT_FOUND"

citations = call(prefix + "/citations?" + urllib.parse.urlencode({"snapshotToken": token}))
assert citations["externalCitationEvents"] == 0
assert citations["ownCitationRate"]["value"] is None
assert citations["ownEventShare"]["value"] is None
assert citations["successfulChecks"] == 0 and citations["eligibleChecks"] == 0
assert citations["googleInternalEvents"] == 0
for route in ["urls", "domains", "gaps", "google-internal"]:
    assert call(prefix + "/citations/" + route + "?" + urllib.parse.urlencode({"snapshotToken": token}))["items"] == []
assert call(prefix + "/citations/urls?" + urllib.parse.urlencode({
    "snapshotToken": token, "domain": "example.test"
}))["items"] == []
assert call(prefix + "/citations/urls?domain=bad%20domain", expected=400)["error"]["code"] == "INVALID_FILTER"
changes = call(prefix + "/citations/changes?" + urllib.parse.urlencode({
    "snapshotToken": token, "minimumEvents": 2, "declineThreshold": 0.5
}))
assert changes["changes"] == [] and changes["minimumEvents"] == 2
assert changes["nextCursor"] is None
assert call(prefix + "/citations/changes?limit=101", expected=400)["error"]["code"] == "INVALID_LIMIT"
assert call(prefix + "/citations/changes?cursor=bad", expected=400)["error"]["code"] == "INVALID_CURSOR"
assert call(prefix + "/citations/changes?declineThreshold=2", expected=400)["error"]["code"] == "INVALID_FILTER"
assert call(prefix + "/citations?aggregationMode=latest_observation", expected=400)["error"]["code"] == "INVALID_FILTER"
assert call(prefix + "/citations/urls/" + "a" * 64, expected=404)["error"]["code"] == "RESOURCE_NOT_FOUND"
assert call(prefix + "/evidence?" + urllib.parse.urlencode({"snapshotToken": token, "urlHash": "a" * 64}))["items"] == []

with urllib.request.urlopen(base + "/swagger/v1/swagger.json") as response:
    swagger = json.load(response)
paths = swagger["paths"]
assert "实际搜索词" in paths["/api/v1/sites/{siteId}/analytics/query-fanout"]["get"]["summary"]
assert "外部引用" in paths["/api/v1/sites/{siteId}/analytics/citations"]["get"]["summary"]
assert "Google 内部" in paths["/api/v1/sites/{siteId}/analytics/citations/google-internal"]["get"]["summary"]
fan_params = {
    item["name"]: item for item in
    paths["/api/v1/sites/{siteId}/analytics/query-fanout"]["get"]["parameters"]
}
assert "快照" in fan_params["snapshotToken"]["description"]
assert "时区" in fan_params["timeZone"]["description"]
changes_params = {
    item["name"]: item for item in
    paths["/api/v1/sites/{siteId}/analytics/citations/changes"]["get"]["parameters"]
}
url_params = {
    item["name"]: item for item in
    paths["/api/v1/sites/{siteId}/analytics/citations/urls"]["get"]["parameters"]
}
assert "域名" in url_params["domain"]["description"]
assert "完整 URL" in swagger["components"]["schemas"]["CitationDomainDetail"]["properties"]["urlListPath"]["description"]
assert "阈值" in changes_params["declineThreshold"]["description"]
assert "引用事件" in changes_params["minimumEvents"]["description"]
assert "游标" in changes_params["cursor"]["description"]
assert changes_params["limit"]["example"] == 50
assert "下一页游标" in swagger["components"]["schemas"]["CitationChangesView"]["properties"]["nextCursor"]["description"]
assert "扩展搜索词事件" in swagger["components"]["schemas"]["FanOutMetrics"]["properties"]["queryEvents"]["description"]
assert "分类命中依据" in swagger["components"]["schemas"]["CitationUrlRow"]["properties"]["ruleBasis"]["description"]
fan_example = paths["/api/v1/sites/{siteId}/analytics/query-fanout"]["get"]["responses"]["200"]["content"]["application/json"]["examples"]["fixedFixture"]
assert "示例" in fan_example["summary"]
assert fan_example["value"]["metrics"]["queryEvents"] == 3
assert fan_example["value"]["metrics"]["queriesPerSuccessfulCheck"]["value"] == 0.75
assert isinstance(fan_example["value"]["engines"], list)
citation_example = paths["/api/v1/sites/{siteId}/analytics/citations"]["get"]["responses"]["200"]["content"]["application/json"]["examples"]["fixedFixture"]
assert "示例" in citation_example["summary"]
assert citation_example["value"]["externalCitationEvents"] == 4
assert citation_example["value"]["ownCitationRate"]["value"] == 0.5
assert citation_example["value"]["ownEventShare"]["value"] == 0.5
assert isinstance(citation_example["value"]["sourceDistribution"], list)
print("stage3_api_smoke: passed")
