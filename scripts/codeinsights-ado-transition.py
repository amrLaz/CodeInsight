#!/usr/bin/env python3
"""Azure DevOps service-connection provisioning for CodeInsights.

The pipeline OAuth token is read only from SYSTEM_ACCESSTOKEN. CodeInsights tokens
are read from a permission-restricted file. No token is logged.
"""

from __future__ import annotations

import argparse
import json
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

API_VERSION = "7.1"


class TransitionError(RuntimeError):
    pass


class AzureDevOpsClient:
    def __init__(self, organization_url: str, token: str):
        parsed = urllib.parse.urlparse(organization_url.rstrip("/"))
        if (
            parsed.scheme != "https"
            or parsed.hostname != "dev.azure.com"
            or parsed.port not in (None, 443)
            or parsed.username
            or parsed.password
            or parsed.query
            or parsed.fragment
            or len([segment for segment in parsed.path.split("/") if segment]) != 1
        ):
            raise TransitionError(
                "organization URL must use https://dev.azure.com/<organization>"
            )
        if not token or token.startswith("$("):
            raise TransitionError("SYSTEM_ACCESSTOKEN is missing or unresolved")
        self.base_url = organization_url.rstrip("/")
        self.token = token
        self.authorization = f"Bearer {token}"
        self.context = ssl.create_default_context()

    def json(self, path: str, *, method: str = "GET", payload: Any = None) -> Any:
        separator = "&" if "?" in path else "?"
        url = f"{self.base_url}{path}{separator}api-version={API_VERSION}"
        body = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(
            url,
            data=body,
            method=method,
            headers={
                "Accept": "application/json",
                "Authorization": self.authorization,
                "Content-Type": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(
                request, timeout=30, context=self.context
            ) as response:
                content = response.read()
                return json.loads(content) if content else None
        except urllib.error.HTTPError as error:
            # The response may echo endpoint authorization data, so never print its body.
            raise TransitionError(
                f"Azure DevOps returned HTTP {error.code} for {method} {path}"
            ) from None


def _required_text(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise TransitionError(f"{name} must be a non-empty string")
    return value.strip()


def _project(client: AzureDevOpsClient, project_name: str) -> dict[str, Any]:
    return client.json(f"/_apis/projects/{urllib.parse.quote(project_name, safe='')}")


def sync_service_connections(args: argparse.Namespace) -> None:
    codeinsights_url = _required_text(args.codeinsights_url, "CodeInsights URL").rstrip(
        "/"
    )
    if urllib.parse.urlparse(codeinsights_url).scheme != "https":
        raise TransitionError("CodeInsights URL must use HTTPS")
    token = Path(args.token_file).read_text(encoding="utf-8").strip()
    if not token:
        raise TransitionError("CodeInsights token file is empty")
    client = AzureDevOpsClient(args.organization_url, _system_access_token())

    for project_name in dict.fromkeys(args.projects):
        project = _project(client, project_name)
        project_path = urllib.parse.quote(project_name, safe="")
        endpoint_name = urllib.parse.quote(args.endpoint_name, safe="")
        result = client.json(
            f"/{project_path}/_apis/serviceendpoint/endpoints"
            f"?endpointNames={endpoint_name}&includeDetails=true"
        )
        endpoints = result.get("value", [])
        if len(endpoints) > 1:
            raise TransitionError(
                f"multiple service connections named {args.endpoint_name!r} exist in {project_name!r}"
            )
        if endpoints and endpoints[0].get("type") != args.endpoint_type:
            raise TransitionError(
                f"service connection {args.endpoint_name!r} in {project_name!r} has type "
                f"{endpoints[0].get('type')!r}, expected {args.endpoint_type!r}"
            )

        payload = {
            "authorization": {"scheme": "Token", "parameters": {"apitoken": token}},
            "data": {},
            "description": "CodeInsights report publication (managed by provision-sast-projects)",
            "isReady": True,
            "isShared": False,
            "name": args.endpoint_name,
            "owner": "library",
            "serviceEndpointProjectReferences": [
                {
                    "name": args.endpoint_name,
                    "description": "CodeInsights report publication",
                    "projectReference": {"id": project["id"], "name": project["name"]},
                }
            ],
            "type": args.endpoint_type,
            "url": codeinsights_url,
        }
        if endpoints:
            endpoint_id = endpoints[0]["id"]
            payload["id"] = endpoint_id
            client.json(
                f"/_apis/serviceendpoint/endpoints/{urllib.parse.quote(endpoint_id, safe='')}",
                method="PUT",
                payload=payload,
            )
            print(f"Service connection updated: {project_name}/{args.endpoint_name}")
        else:
            client.json(
                "/_apis/serviceendpoint/endpoints", method="POST", payload=payload
            )
            print(f"Service connection created: {project_name}/{args.endpoint_name}")


def _system_access_token() -> str:
    return os.environ.get("SYSTEM_ACCESSTOKEN", "")


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser()
    commands = result.add_subparsers(dest="command", required=True)

    sync = commands.add_parser("sync-service-connections")
    sync.add_argument("--organization-url", required=True)
    sync.add_argument("--projects", nargs="+", required=True)
    sync.add_argument("--endpoint-name", default="CodeInsights-Coop")
    sync.add_argument("--endpoint-type", default="upcoop-codeinsights")
    sync.add_argument("--codeinsights-url", required=True)
    sync.add_argument("--token-file", required=True)
    sync.set_defaults(run=sync_service_connections)

    return result


def main() -> int:
    try:
        args = parser().parse_args()
        args.run(args)
        return 0
    except (OSError, TransitionError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
