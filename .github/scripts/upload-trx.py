#!/usr/bin/env python3
"""Upload a TRX zip through the Actions results service.

The repository policy only allows actions owned by DNTOF, so this job cannot
call actions/upload-artifact. The runner still provides ACTIONS_RUNTIME_TOKEN
and ACTIONS_RESULTS_URL for the job's own artifact storage.
"""

import base64
import datetime
import hashlib
import json
import os
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def redact(text: str) -> str:
    token = os.environ.get("ACTIONS_RUNTIME_TOKEN", "")
    if token:
        text = text.replace(token, "[REDACTED]")
    # Signed blob URLs carry a query signature. Keep only the host.
    redacted = []
    for line in text.splitlines():
        if "sig=" in line or "https://" in line:
            redacted.append("[redacted response line]")
        else:
            redacted.append(line)
    return "\n".join(redacted)[:800]


def backend_ids(token: str) -> tuple[str, str]:
    payload = token.split(".")[1]
    payload += "=" * (-len(payload) % 4)
    claims = json.loads(base64.urlsafe_b64decode(payload))
    for scope in str(claims.get("scp", "")).split():
        parts = scope.split(":")
        if len(parts) == 3 and parts[0] == "Actions.Results":
            return parts[1], parts[2]
    fail("ACTIONS_RUNTIME_TOKEN does not contain an Actions.Results scope")
    return "", ""


def request_json(url: str, token: str, body: dict) -> dict:
    data = json.dumps(body).encode()
    req = urllib.request.Request(
        url,
        data=data,
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "User-Agent": "sldataapi-trx-upload",
        },
    )
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode())
    except urllib.error.HTTPError as exc:
        fail(f"{url.rsplit('/', 1)[-1]} HTTP {exc.code}: {redact(exc.read().decode('utf-8', 'replace'))}")
    return {}


def find_signed_url(value: object) -> str:
    if isinstance(value, dict):
        for key, item in value.items():
            normalized = key.replace("_", "").lower()
            if normalized in {"signeduploadurl", "signedurl"} and isinstance(item, str) and item.startswith("https://"):
                return item
            found = find_signed_url(item)
            if found:
                return found
    elif isinstance(value, list):
        for item in value:
            found = find_signed_url(item)
            if found:
                return found
    return ""


def main() -> None:
    suite = os.environ.get("SUITE", "")
    if not suite:
        fail("SUITE is empty")
    source = Path("TestResults") / suite
    files = sorted(source.glob("*.trx")) if source.is_dir() else []
    if not files:
        print(f"warning: no TRX files in {source}")
        return

    token = os.environ.get("ACTIONS_RUNTIME_TOKEN", "")
    results = os.environ.get("ACTIONS_RESULTS_URL", "")
    if not token or not results:
        names = sorted(name for name in os.environ if name.startswith("ACTIONS_"))
        fail(
            "ACTIONS_RUNTIME_TOKEN or ACTIONS_RESULTS_URL is missing. "
            f"ACTIONS_* names present: {', '.join(names) if names else '(none)'}"
        )
    run_id, job_id = backend_ids(token)
    origin = results.split("/twirp/")[0].rstrip("/")
    if "://" not in origin:
        fail("ACTIONS_RESULTS_URL is not a URL")

    archive = Path(os.environ.get("RUNNER_TEMP", "/tmp")) / f"{suite}.trx.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for path in files:
            zf.write(path, arcname=path.name)
    payload = archive.read_bytes()
    digest = hashlib.sha256(payload).hexdigest()
    expires = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=14)).strftime("%Y-%m-%dT%H:%M:%SZ")
    name = f"test-results-{suite}"
    service = f"{origin}/twirp/github.actions.results.api.v1.ArtifactService"
    # protobuf-ts sends proto field names. Wrapper fields are plain JSON strings.
    created = request_json(
        f"{service}/CreateArtifact",
        token,
        {
            "workflow_run_backend_id": run_id,
            "workflow_job_run_backend_id": job_id,
            "name": name,
            "version": 7,
            "mime_type": "application/zip",
            "expires_at": expires,
        },
    )
    if created.get("ok") is False:
        fail(f"CreateArtifact was not accepted: {redact(json.dumps(created))}")
    upload_url = find_signed_url(created)
    if not upload_url:
        fail(f"CreateArtifact response did not include an upload URL. Keys: {list(created)}")

    put = urllib.request.Request(
        upload_url,
        data=payload,
        method="PUT",
        headers={
            "Content-Type": "application/zip",
            "x-ms-blob-type": "BlockBlob",
        },
    )
    try:
        with urllib.request.urlopen(put) as resp:
            status = resp.status
    except urllib.error.HTTPError as exc:
        fail(f"blob upload HTTP {exc.code}: {redact(exc.read().decode('utf-8', 'replace'))}")
    if status not in {200, 201}:
        fail(f"blob upload HTTP {status}")

    finalized = request_json(
        f"{service}/FinalizeArtifact",
        token,
        {
            "workflow_run_backend_id": run_id,
            "workflow_job_run_backend_id": job_id,
            "name": name,
            "size": str(len(payload)),
            "hash": f"sha256:{digest}",
        },
    )
    if finalized.get("ok") is False:
        fail(f"FinalizeArtifact was not accepted: {redact(json.dumps({k: v for k, v in finalized.items() if 'url' not in k.lower()}))}")
    print(f"uploaded {name} ({len(files)} trx, {len(payload)} bytes)")


if __name__ == "__main__":
    main()
