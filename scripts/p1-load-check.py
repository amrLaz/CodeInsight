"""Small reproducibility/guard check; does not contact an API or database."""
import argparse
import importlib.util
import json
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("load", Path(__file__).with_name("p1-load.py"))
load = importlib.util.module_from_spec(spec)
spec.loader.exec_module(load)
data = load.report(argparse.Namespace(density=10, report_bytes=4096))
assert len(data) == 4096
assert len(json.loads(data)["runs"][0]["results"]) == 10
assert load.report(argparse.Namespace(density=10, report_bytes=4096)) == data
assert load.percentiles([1, 2, 3])["p50"] == 2
assert load.TIERS["C"] == (500, 500, 5, 50, 50)
with patch.object(load, "sql", return_value="1"), patch.object(load, "call") as call:
    try:
        load.seed(argparse.Namespace(db_container="isolated", seed=42), 2, 2)
        raise AssertionError("Existing database accepted")
    except ValueError as error:
        assert "already contains" in str(error)
    call.assert_not_called()
print("P1 load checks passed: deterministic report, size, density, percentiles, tier, seed refusal")
