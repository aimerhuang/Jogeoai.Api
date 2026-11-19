"""Measure the PRD 9.3 HTTP read baseline against an isolated 108k-Check site.

Required environment: PERF_API_BASE, PERF_SITE_ID, PERF_TENANT_ID, PERF_CHECK_ID.
The script only issues GET requests and prints JSON; it does not clean up data.
"""

from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta, timezone
import json
import math
import os
import time
from urllib.parse import urlencode
from urllib.request import Request, urlopen


base = os.environ["PERF_API_BASE"].rstrip("/")
site = os.environ["PERF_SITE_ID"]
tenant = os.environ["PERF_TENANT_ID"]
check = os.environ["PERF_CHECK_ID"]
concurrency = int(os.getenv("PERF_CONCURRENCY", "10"))
samples = int(os.getenv("PERF_SAMPLES", "40"))
warmups = int(os.getenv("PERF_WARMUPS", str(min(concurrency, 10))))
now = datetime.now(timezone.utc)
window_days = int(os.getenv("PERF_WINDOW_DAYS", "90"))
filter_query = {
    "from": (now - timedelta(days=window_days)).isoformat().replace("+00:00", "Z"),
    "to": (now + timedelta(hours=1)).isoformat().replace("+00:00", "Z"),
    "timeZone": "UTC",
}


def get(path: str) -> tuple[float, dict]:
    request = Request(base + path, headers={"X-Tenant-Id": tenant})
    start = time.perf_counter()
    with urlopen(request, timeout=180) as response:
        payload = json.load(response)
        if response.status != 200:
            raise RuntimeError(f"{path}: HTTP {response.status}")
    return (time.perf_counter() - start) * 1000, payload


root = f"/api/v1/sites/{site}/analytics"
query = urlencode(filter_query)
_, first = get(f"{root}/overview?{query}")
token = first["meta"]["snapshotToken"]
fixed_query = urlencode({**filter_query, "snapshotToken": token})
paths = {
    "overview": f"{root}/overview?{fixed_query}",
    "visibility": f"{root}/visibility?{fixed_query}",
    "shareOfVoice": f"{root}/share-of-voice?{fixed_query}",
    "prompts": f"{root}/prompts?{fixed_query}",
    "queryFanout": f"{root}/query-fanout?{fixed_query}",
    "citations": f"{root}/citations?{fixed_query}",
    "checkDetail": f"/api/v1/visibility-checks/{check}?snapshotToken={token}",
}
optional_paths = {
    "citationUrls": f"{root}/citations/urls?" + urlencode({
        **filter_query,
        "snapshotToken": token,
        "limit": int(os.getenv("PERF_URL_LIMIT", "50")),
        **({"domain": os.environ["PERF_URL_DOMAIN"]} if os.getenv("PERF_URL_DOMAIN") else {}),
    }),
    "citationChanges": f"{root}/citations/changes?" + urlencode({
        **filter_query,
        "snapshotToken": token,
        "minimumEvents": int(os.getenv("PERF_CHANGE_MINIMUM_EVENTS", "1")),
        "limit": int(os.getenv("PERF_CHANGE_LIMIT", "50")),
    }),
}
selected = os.getenv("PERF_ENDPOINTS")
if selected:
    names = selected.split(",")
    available = {**paths, **optional_paths}
    paths = {name: available[name] for name in names}


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    return round(ordered[max(0, math.ceil(len(ordered) * fraction) - 1)], 2)


results = {}
for name, path in paths.items():
    for _ in range(warmups):
        get(path)
    with ThreadPoolExecutor(max_workers=concurrency) as executor:
        futures = [executor.submit(get, path) for _ in range(samples)]
        timings = [future.result()[0] for future in as_completed(futures)]
    results[name] = {
        "count": len(timings),
        "concurrency": concurrency,
        "p50Ms": percentile(timings, 0.5),
        "p95Ms": percentile(timings, 0.95),
        "maxMs": round(max(timings), 2),
    }
    print(f"{name}: p95={results[name]['p95Ms']} ms", flush=True)

print(json.dumps({
    "measuredAtUtc": datetime.now(timezone.utc).isoformat(),
    "siteId": site,
    "checksExpected": 108000,
    "citationsExpected": 540000,
    "window": filter_query,
    "results": results,
}, indent=2))
