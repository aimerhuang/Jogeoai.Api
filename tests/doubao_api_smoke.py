"""豆包 API Provider 本机验收脚本（只读为主，仅写入一次站点 Target 和一次 Run）。

环境变量：
  SMOKE_API_BASE   例如 http://localhost:5260
  SMOKE_TENANT_ID  品牌所属租户
  SMOKE_SITE_ID    品牌站点
  SMOKE_TOKEN      该租户成员（非 viewer）的会话令牌；系统管理员账号亦可
  SMOKE_PROMPTS    可选，采集的 Prompt 数量，默认 5
  SMOKE_SKIP_RUN   可选，为 1 时只核对目录与 Target，不创建 Run

脚本不打印回答正文、凭据或原始响应；只输出计数、状态码、质量字段与标识。
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

base = os.environ["SMOKE_API_BASE"].rstrip("/") + "/api/v1"
tenant = os.environ["SMOKE_TENANT_ID"]
site = os.environ["SMOKE_SITE_ID"]
token = os.environ["SMOKE_TOKEN"]
prompt_count = int(os.environ.get("SMOKE_PROMPTS", "5"))


def call(method, path, body=None, key=None, expected=(200,)):
    headers = {"X-Tenant-Id": tenant, "Content-Type": "application/json", "Authorization": "Bearer " + token}
    if key:
        headers["Idempotency-Key"] = key
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(base + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            status, payload = response.status, json.load(response)
    except urllib.error.HTTPError as response:
        raw = response.read()
        try:
            payload = json.loads(raw)
        except ValueError:
            payload = {"raw_length": len(raw)}
        status = response.code
    if status not in expected:
        print("FAIL", method, path, status, json.dumps(payload, ensure_ascii=False)[:400])
        sys.exit(1)
    return payload


def step(title):
    print("\n==", title)


step("目录：豆包行")
catalog = call("GET", "/collection-targets")
items = catalog if isinstance(catalog, list) else catalog.get("items", [])
doubao = [t for t in items if t.get("provider") == "doubao_api"]
print("doubao_api rows:", json.dumps(doubao, ensure_ascii=False))
if not doubao or doubao[0]["availability"] != "available":
    print("豆包目录不可用：请先由系统管理员配置 PUT /api/v1/providers/doubao/settings 并启用。")
    sys.exit(2)

step("站点 Target：确保 doubao_api/doubao 已启用")
targets = call("GET", f"/sites/{site}/targets")
version = targets["configVersion"]
current = targets["items"]
print("configVersion:", version, "targets:", [(t["provider"], t["engine"], t["enabled"]) for t in current])
has_doubao = any(t["provider"] == "doubao_api" and t["enabled"] for t in current)
if not has_doubao:
    write = [
        {"provider": t["provider"], "engine": t["engine"], "captureMode": t["captureMode"],
         "country": t["country"], "locale": t["locale"], "enabled": t["enabled"]}
        for t in current
    ]
    write.append({"provider": "doubao_api", "engine": "doubao", "captureMode": "api",
                  "country": "CN", "locale": "zh-CN", "enabled": True})
    targets = call("PUT", f"/sites/{site}/targets", {"expectedVersion": version, "targets": write})
    print("after PUT configVersion:", targets["configVersion"])
doubao_target = next(t for t in targets["items"] if t["provider"] == "doubao_api" and t["enabled"])
print("doubao targetId:", doubao_target["targetId"])

if os.environ.get("SMOKE_SKIP_RUN") == "1":
    print("SMOKE_SKIP_RUN=1，结束。")
    sys.exit(0)

step("Prompt：取前 N 条启用的 Prompt")
prompts = call("GET", f"/sites/{site}/prompts?limit=100")
enabled = [p for p in prompts["items"] if p["enabled"] and not p.get("archivedAt")][:prompt_count]
print("prompt ids:", [p["promptId"] for p in enabled])
if not enabled:
    print("没有可用 Prompt。")
    sys.exit(2)

step("创建手动 Run（仅豆包 Target，单样本）")
key = "doubao-smoke:" + uuid.uuid4().hex
run = call("POST", "/visibility-runs", {
    "siteId": site,
    "promptIds": [p["promptId"] for p in enabled],
    "targetIds": [doubao_target["targetId"]],
    "sampleCount": 1,
    "requestedBy": "doubao-smoke",
}, key=key, expected=(200, 201, 202))
run_id = run["runId"]
print("runId:", run_id, "totalChecks:", run["totalChecks"], "status:", run["status"])

step("等待 Worker 完成（最长 20 分钟）")
deadline = time.time() + 20 * 60
while time.time() < deadline:
    summary = call("GET", f"/visibility-runs/{run_id}")
    print(time.strftime("%H:%M:%S"), summary["status"], "succeeded", summary["succeededChecks"],
          "failed", summary["failedChecks"], "/", summary["totalChecks"])
    if summary["status"] in ("succeeded", "failed", "partial_failed", "cancelled"):
        break
    time.sleep(15)

step("逐条核对 Check（不打印正文）")
checks = call("GET", f"/visibility-runs/{run_id}/checks?limit=50")
rows = []
for c in checks["items"]:
    detail = call("GET", f"/visibility-checks/{c['checkId']}")
    answer = (detail.get("answer") or {}).get("text") or ""
    citations = detail.get("citations") or []
    rows.append({
        "checkId": detail["checkId"],
        "provider": detail.get("provider"), "engine": detail.get("engine"),
        "captureMode": detail.get("captureMode"), "evidenceClass": detail.get("evidenceClass"),
        "status": detail.get("status"), "answerPresent": detail.get("answerPresent"),
        "answerChars": len(answer), "webQueries": len(detail.get("webQueries") or []),
        "queryAvailability": detail.get("queryAvailability"),
        "citations": len(citations),
        "citationsWithTitle": sum(1 for x in citations if x.get("title")),
        "citationsWithDomain": sum(1 for x in citations if x.get("domain")),
        "modelVersion": detail.get("modelVersion"), "parserVersion": detail.get("parserVersion"),
        "durationMs": detail.get("durationMs"), "errorCode": detail.get("errorCode"),
        "resultQuality": detail.get("resultQuality"),
    })
for r in rows:
    print(json.dumps(r, ensure_ascii=False))

ok = [r for r in rows if r["status"] == "succeeded"]
print(f"\n成功 {len(ok)} / {len(rows)}；runId={run_id}")
