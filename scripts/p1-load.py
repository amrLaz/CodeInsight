"""Local, synthetic CodeInsights load campaign; Python standard library only."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import random
import re
import subprocess
import sys
import threading
import time
import urllib.request
import urllib.parse
import uuid

TIERS = {"A": (100, 100, 3, 5, 10), "B": (300, 300, 3, 20, 30), "C": (500, 500, 5, 50, 50)}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise RuntimeError("Load traffic cannot follow redirects outside the local endpoint")


def call(args, method, path, payload=None, raw=None):
    data = raw if raw is not None else json.dumps(payload).encode() if payload is not None else None
    request = urllib.request.Request(args.url.rstrip("/") + "/api/v1" + path, data=data, method=method,
        headers={"Authorization": "Bearer " + os.environ["CODEINSIGHTS_LOAD_TOKEN"].strip(),
                 "Content-Type": "application/octet-stream" if raw is not None else "application/json"})
    with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
        body = response.read()
        return json.loads(body) if body else {}


def sql(args, command):
    return subprocess.check_output(["docker", "exec", "-i", args.db_container, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-h", "/var/run/postgresql", "-U", args.db_user, "-d", args.db_name, "-At"], input=command.encode(), timeout=120).decode().strip()


def seed(args, projects, histories):
    if not args.db_container or not re.fullmatch(r"[a-zA-Z0-9_.-]+", args.db_container):
        raise ValueError("Seeding requires an explicitly named isolated local PostgreSQL container")
    # Refuse all pre-existing project/history data; this runner never deletes or truncates.
    count = sql(args, "SELECT (SELECT count(*) FROM projects)+(SELECT count(*) FROM analysis_records);")
    if count != "0":
        raise ValueError("Seed refused: database already contains projects or analysis history")
    call(args, "POST", "/profiles", {"profile_id":f"load-{args.seed}","version":"1","stack":"synthetic",
        "required_tools":["load"],"required_metrics":["code_smells"],
        "rules":[{"tool":"load","rule_id":"load-rule","category":"code_smell"}],
        "conditions":[{"metric":"code_smells","operator":"max","threshold":1000000}]})
    for index in range(projects):
        call(args, "POST", "/projects", {"key": f"load-{args.seed}-{index}", "name": f"Synthetic load {index}"})
        call(args,"PUT",f"/profiles/projects/load-{args.seed}-{index}",{"profile_id":f"load-{args.seed}","profile_version":"1","enforcement":"informative"})
    if sql(args, f"SELECT count(*) FROM projects WHERE key LIKE 'load-{args.seed}-%';") != str(projects):
        raise ValueError("API and PostgreSQL container do not expose the same fixture projects; history seed refused")
    # Metadata only: report contents are produced by measured publications, not forged historical findings.
    sql(args, f"""INSERT INTO analysis_records
      (analysis_id,project_key,commit_sha,branch,ci_provider,status,quality_gate,measurements,tool_executions,
       completeness,profile_id,profile_version,formula_version,gate_snapshot,affected_files,gate_failures,created_at)
      SELECT md5('{args.seed}:'||p||':'||h), 'load-{args.seed}-'||p, md5('{args.seed}:'||h),
       'refs/heads/main','local','completed','unknown','{{}}','[]','incomplete','','0','0','{{}}','{{}}','[]',
       to_char(timestamp '2026-09-05 00:00:00' - h*interval '1 minute','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')
      FROM generate_series(0,{projects - 1}) p CROSS JOIN generate_series(1,{histories}) h;""")


def report(args):
    findings = [{"ruleId": "load-rule", "level": "warning", "message": {"text": "Synthetic finding"},
        "locations": [{"physicalLocation": {"artifactLocation": {"uri": f"src/file-{i % 100}.cs"},
        "region": {"startLine": i + 1}}}]} for i in range(args.density)]
    data = json.dumps({"version": "2.1.0", "runs": [{"tool": {"driver": {"name": "load", "version": "1"}},
        "results": findings}]}, separators=(",", ":")).encode()
    # JSON permits trailing whitespace: size varies without altering findings or parser meaning.
    return data + b" " * max(0, args.report_bytes - len(data))


def percentiles(values):
    values = sorted(values)
    return {f"p{p}": values[min(len(values)-1, int((len(values)-1)*p/100))] if values else None for p in (50,95,99)}


def phase(args, duration, writers, readers, projects, reports, data, repeat, name, output):
    start = time.monotonic(); until = start + duration
    lock = threading.Lock(); samples = {"publication": [], "read": []}; errors = {}; outstanding = {}; completed = 0
    maximum_backlog = 0; maximum_age = 0; counter = 0
    digest = hashlib.sha256(data).hexdigest()

    def worker(index, writing):
        nonlocal counter, completed, maximum_backlog, maximum_age
        rng = random.Random(args.seed + index)
        while time.monotonic() < until:
            began = time.monotonic(); ingestion = None
            try:
                project = f"load-{args.seed}-{rng.randrange(projects)}"
                if writing:
                    with lock:
                        counter += 1; sequence = counter
                    identity = f"{args.run_id}-{repeat}-{name}-{sequence}"
                    payload = {"schema_version":1,"project_key":project,"repository_url":"https://example.invalid/load",
                        "ci_provider":"local","build_id":identity,"commit_sha":hashlib.sha256(identity.encode()).hexdigest()[:40],
                        "branch":"refs/heads/main","git_diff":{"state":"unavailable"},
                        "reports":[{"report_id":f"r-{i}","format":"sarif","format_version":"2.1.0","tool":"load",
                            "tool_version":"1","state":"measured","size_bytes":len(data),"sha256":digest} for i in range(reports)]}
                    receipt = call(args,"POST","/ingestions",payload); ingestion = receipt["ingestion_id"]
                    with lock: outstanding[ingestion] = began
                    for i in range(reports): call(args,"PUT",f"/ingestions/{ingestion}/reports/r-{i}",raw=data)
                    call(args,"POST",f"/ingestions/{ingestion}/finalize",{})
                    deadline = time.monotonic() + 600
                    while True:
                        receipt = call(args,"GET",f"/ingestions/{ingestion}")
                        if receipt["state"] in ("completed","failed"): break
                        if time.monotonic() > deadline: raise TimeoutError("receipt_timeout")
                        time.sleep(.25)
                    if receipt["state"] == "failed": raise RuntimeError("ingestion_failed")
                else:
                    path = "/insights/summary" if rng.randrange(2) else "/insights/analyses?limit=25&project_key=" + project
                    call(args,"GET",path)
                with lock:
                    samples["publication" if writing else "read"].append(time.monotonic()-began)
                    if writing: completed += 1
            except Exception as error:
                key = str(getattr(error,"code",type(error).__name__))
                with lock: errors[key] = errors.get(key,0)+1
            finally:
                with lock:
                    if ingestion: outstanding.pop(ingestion,None)

    with ThreadPoolExecutor(max_workers=writers+readers) as pool:
        futures = [pool.submit(worker,i,i<writers) for i in range(writers+readers)]
        while any(not f.done() for f in futures):
            with lock:
                backlog=len(outstanding); age=max((time.monotonic()-v for v in outstanding.values()),default=0)
                maximum_backlog=max(maximum_backlog,backlog); maximum_age=max(maximum_age,age)
                event={"event":"progress","repeat":repeat,"phase":name,"elapsed":time.monotonic()-start,
                    "submitted_not_terminal":backlog,"oldest_seconds":age,"completed":completed,"errors":dict(errors)}
            output.write(json.dumps(event)+"\n"); output.flush(); print(json.dumps(event),flush=True)
            if args.db_container:
                try:
                    backlog = json.loads(sql(args, "SELECT coalesce(json_agg(q),'[]') FROM (SELECT state,count(*) AS count,coalesce(max(extract(epoch FROM now()-created_at)),0) AS oldest_seconds FROM durable_jobs GROUP BY state) q;"))
                    output.write(json.dumps({"event":"durable_queue","repeat":repeat,"phase":name,"states":backlog})+"\n");output.flush()
                except Exception as error:
                    output.write(json.dumps({"event":"queue_observation_failed","error":type(error).__name__})+"\n")
            time.sleep(min(5,max(.1,until-time.monotonic())))
        for future in futures: future.result()
    elapsed=time.monotonic()-start
    result={"event":"phase_result","repeat":repeat,"phase":name,"duration_seconds":elapsed,"requested_seconds":duration,
        "latency_seconds":{k:percentiles(v) for k,v in samples.items()},"counts":{k:len(v) for k,v in samples.items()},
        "throughput_per_second":{k:len(v)/elapsed for k,v in samples.items()},"errors":errors,
        "max_submitted_not_terminal":maximum_backlog,"max_observed_age_seconds":maximum_age}
    output.write(json.dumps(result)+"\n");output.flush();print(json.dumps(result),flush=True)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url",required=True);parser.add_argument("--tier",choices=TIERS,default="A")
    for name,default in [("duration",1800),("warmup",300),("burst",300),("repeats",3),("report-bytes",1048576),("density",100),("seed",42)]:
        parser.add_argument("--"+name,type=int,default=default)
    parser.add_argument("--smoke",action="store_true",help="Use 2 projects, 2 histories, 1 publisher, 2 readers")
    parser.add_argument("--seed-fixtures",action="store_true");parser.add_argument("--db-container")
    parser.add_argument("--db-user",default="postgres");parser.add_argument("--db-name",default="codeinsights")
    parser.add_argument("--output",default=".artifacts/p1-load.jsonl")
    args=parser.parse_args();args.run_id=uuid.uuid4().hex
    if urllib.parse.urlparse(args.url).hostname not in ("127.0.0.1","localhost","::1"):
        parser.error("Only a loopback API on an isolated development database is permitted")
    if any(getattr(args,n)<0 for n in ("duration","warmup","burst","density")) or args.duration<1 or args.repeats<1:
        parser.error("Invalid negative or empty campaign settings")
    if not os.environ.get("CODEINSIGHTS_LOAD_TOKEN"): parser.error("CODEINSIGHTS_LOAD_TOKEN is required")
    projects,histories,reports,writers,readers=(2,2,3,1,2) if args.smoke else TIERS[args.tier]
    if args.seed_fixtures: seed(args,projects,histories)
    visible=call(args,"GET","/projects")
    if not all(any(p.get("key")==f"load-{args.seed}-{i}" for p in visible) for i in range(projects)):
        parser.error("Required seeded projects missing; use a new isolated database and --seed-fixtures")
    data=report(args)
    Path(args.output).parent.mkdir(parents=True,exist_ok=True)
    with open(args.output,"a",encoding="utf-8") as output:
        output.write(json.dumps({"event":"settings",**vars(args),"projects":projects,"histories":histories,
            "reports":reports,"writers":writers,"readers":readers,"actual_report_bytes":len(data)})+"\n")
        for repeat in range(args.repeats):
            for name,duration,multiplier in (("warmup",args.warmup,1),("steady",args.duration,1),("burst",args.burst,2)):
                if duration: phase(args,duration,writers*multiplier,readers,projects,reports,data,repeat,name,output)


if __name__=="__main__":
    try:
        main()
    except Exception as error:
        # Do not print urllib header exceptions: those can contain the bearer value.
        print(f"Campaign failed: {type(error).__name__}, HTTP {getattr(error, 'code', 'n/a')}", file=sys.stderr)
        sys.exit(1)
