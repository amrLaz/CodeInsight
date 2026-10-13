import importlib.util
from pathlib import Path


script = Path(__file__).parents[1] / "scripts" / "codeinsights-ado-transition.py"
source = script.read_text(encoding="utf-8")
spec = importlib.util.spec_from_file_location("codeinsights_ado_transition", script)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


oauth_client = module.AzureDevOpsClient(
    "https://dev.azure.com/up-group", "oauth-token"
)
assert oauth_client.authorization == "Bearer oauth-token"
for invalid_url in (
    "https://user@dev.azure.com/up-group",
    "https://dev.azure.com:444/up-group",
    "https://dev.azure.com/up-group/extra",
    "https://dev.azure.com/up-group?redirect=hostile",
):
    try:
        module.AzureDevOpsClient(invalid_url, "token")
    except module.TransitionError:
        pass
    else:
        raise AssertionError(f"unsafe organization URL accepted: {invalid_url}")

sync_source = source.split("def sync_service_connections", 1)[1].split("def _system_access_token", 1)[0]
assert "_system_access_token()" in sync_source
assert "cleanup-comments" not in source
assert "CODEINSIGHTS_LEGACY_ADO_PAT" not in source

print("CodeInsights Azure DevOps transition checks passed")

pipeline = (Path(__file__).parents[1] / ".azuredevops" / "provision-sast-projects.yml").read_text(
    encoding="utf-8"
)
for obsolete in ("CodeInsightsAdoPat", "CODEINSIGHTS_ADO_PAT", "personal_access_token"):
    assert obsolete not in pipeline, f"obsolete PAT dependency remains: {obsolete}"
for required in (
    "SYSTEM_ACCESSTOKEN: $(System.AccessToken)",
    "sync-service-connections",
    "ttl_days\": 3650",
):
    assert required in pipeline, f"transition pipeline contract is missing: {required}"
for obsolete in ("cleanupLegacyCommentsMode", "cleanup-comments", "CODEINSIGHTS_LEGACY_ADO_PAT"):
    assert obsolete not in pipeline, f"legacy cleanup remains: {obsolete}"

print("CodeInsights provisioning transition contract passed")
