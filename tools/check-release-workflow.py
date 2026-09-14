#!/usr/bin/env python3
"""T-80 (E8-S1): static checks over .github/workflows/release.yml, without a runner and without actionlint.

Needs Python 3 and PyYAML only. Exits 0 when every check passes, 1 otherwise, and prints one PASS/FAIL line per check
and a final summary line on every outcome.

What it asserts:
  - the file parses as YAML, and release.yml runs only for pushed tags v* (no branches, pull requests or manual runs);
  - one job, on ci.yml's runner, with a timeout, contents: write, and no continue-on-error anywhere;
  - every ci.yml step is in release.yml, identical (run, shell, env, with, if), in the same order: the release build
    weakens no gate. Two differences are allowed: checkout may add with-keys (fetch-depth for the notes), and ci.yml's
    upload of the unsigned package is not repeated;
  - every action is one ci.yml uses or a first-party actions/* one, pinned to a major version;
  - every repository path a step names (tools/, src/, tests/, native/) is tracked by git;
  - the only secrets read are TUNQIO_SIGNING_PFX_BASE64 and TUNQIO_SIGNING_PFX_PASSWORD, and only through env;
  - no run script interpolates github.ref_name, github.head_ref or github.event directly (script injection);
  - every steps.<id>.outputs.<name> refers to a step that exists, earlier, whose script writes that name;
  - the secrets check precedes the build, signing follows staging, and the release is created last but for its check.
"""
import os
import re
import subprocess
import sys

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CI = os.path.join(ROOT, ".github", "workflows", "ci.yml")
RELEASE = os.path.join(ROOT, ".github", "workflows", "release.yml")
SECRETS = {"TUNQIO_SIGNING_PFX_BASE64", "TUNQIO_SIGNING_PFX_PASSWORD"}

failures = []
count = 0


def check(ok, message):
    global count
    count += 1
    print(("PASS  " if ok else "FAIL  ") + message)
    if not ok:
        failures.append(message)


def load(path):
    with open(path, encoding="utf-8") as f:
        doc = yaml.safe_load(f)
    # YAML 1.1 reads the bare key 'on' as the boolean true.
    if isinstance(doc, dict) and True in doc:
        doc["on"] = doc.pop(True)
    return doc


def text_of(step):
    return yaml.safe_dump(step, sort_keys=True)


def main():
    try:
        ci = load(CI)
        rel = load(RELEASE)
    except yaml.YAMLError as e:
        check(False, f"YAML parses: {e}")
        return finish()
    check(isinstance(rel, dict), "release.yml parses as a YAML mapping")

    check(rel.get("on") == {"push": {"tags": ["v*"]}}, "release.yml runs only on push of tags v* (no branches, PRs or workflow_dispatch)")
    check(rel.get("permissions") == {"contents": "write"}, "permissions are contents: write and nothing else")
    check((rel.get("env") or {}) == (ci.get("env") or {}), "workflow env matches ci.yml's")

    jobs = rel.get("jobs") or {}
    check(len(jobs) == 1, "one job")
    job = next(iter(jobs.values()))
    ci_job = next(iter(ci["jobs"].values()))
    check(job.get("runs-on") == ci_job.get("runs-on"), f"runs on ci.yml's runner ({ci_job.get('runs-on')})")
    check(isinstance(job.get("timeout-minutes"), int) and job["timeout-minutes"] >= ci_job.get("timeout-minutes", 0), "has a timeout no shorter than ci.yml's")
    steps = job.get("steps") or []
    ci_steps = ci_job.get("steps") or []
    check(all("continue-on-error" not in s for s in steps) and "continue-on-error" not in job, "no continue-on-error")

    # ---- every ci.yml step, identical and in order ---------------------------------------------------------------------
    last = -1
    for cs in ci_steps:
        label = cs.get("name") or f"{cs.get('uses')} {cs.get('with', {})}"
        if cs.get("uses", "").startswith("actions/upload-artifact") and (cs.get("with") or {}).get("name") == "tunqio-msix-unsigned":
            print(f"note  ci.yml step not repeated by design: upload of the unsigned package ({label})")
            continue
        idx = None
        for i, rs in enumerate(steps):
            if cs.get("name"):
                if rs.get("name") == cs["name"]:
                    idx = i
                    break
            elif cs.get("uses", "").startswith("actions/checkout"):
                if rs.get("uses") == cs["uses"] and all((rs.get("with") or {}).get(k) == v for k, v in (cs.get("with") or {}).items()):
                    idx = i
                    break
            elif text_of(rs) == text_of(cs):
                idx = i
                break
        if idx is None:
            check(False, f"ci.yml step present: {label}")
            continue
        rs = steps[idx]
        if cs.get("uses", "").startswith("actions/checkout") and not cs.get("name"):
            same = rs.get("uses") == cs.get("uses")
        else:
            same = text_of(rs) == text_of(cs)
        check(same and idx > last, f"ci.yml step identical and in order: {label}")
        last = max(last, idx)

    # ---- actions ------------------------------------------------------------------------------------------------------------
    ci_uses = {s["uses"] for s in ci_steps if "uses" in s}
    for s in steps:
        if "uses" not in s:
            continue
        u = s["uses"]
        ok = u in ci_uses or (u.startswith("actions/") and re.search(r"@v\d+(\.\d+){0,2}$", u))
        check(bool(ok), f"action allowed and pinned: {u}")

    # ---- paths --------------------------------------------------------------------------------------------------------------
    all_text = "\n".join(text_of(s) for s in steps)
    paths = sorted({m.rstrip(".") for m in re.findall(r"(?<![\w./-])(?:\./)?((?:tools|src|tests|native)/[\w./-]+)", all_text)})
    for p in paths:
        if p.startswith("native/bass"):
            print(f"note  {p}: fetched by tools/fetch-native.ps1, gitignored")
            continue
        tracked = subprocess.run(["git", "-C", ROOT, "ls-files", "--", p], capture_output=True, text=True).stdout.strip()
        check(bool(tracked), f"path tracked by git: {p}")

    # ---- secrets and injection ----------------------------------------------------------------------------------------------
    used = set(re.findall(r"secrets\.([A-Za-z0-9_]+)", all_text))
    check(used == SECRETS, f"secrets read are exactly {sorted(SECRETS)} (found {sorted(used)})")
    for s in steps:
        run = s.get("run", "")
        if "secrets." in run:
            check(False, f"secrets are passed through env, not interpolated into run: {s.get('name')}")
        if re.search(r"\$\{\{\s*github\.(ref_name|head_ref|event)", run):
            check(False, f"no github.ref_name/head_ref/event interpolated into run: {s.get('name')}")
    check(not any(re.search(r"\$\{\{\s*github\.(ref_name|head_ref|event)", s.get("run", "")) for s in steps), "no untrusted context interpolated into any run script")

    # ---- step outputs -------------------------------------------------------------------------------------------------------
    ids = {s["id"]: i for i, s in enumerate(steps) if "id" in s}
    for i, s in enumerate(steps):
        for sid, out in re.findall(r"steps\.([\w-]+)\.outputs\.([\w-]+)", text_of(s)):
            if sid not in ids or ids[sid] >= i:
                check(False, f"step '{s.get('name')}' reads steps.{sid}, which is not an earlier step")
                continue
            producer = steps[ids[sid]].get("run", "")
            scripts = re.findall(r"\./(tools/[\w.-]+\.ps1)", producer)
            body = producer + "".join(open(os.path.join(ROOT, sc), encoding="utf-8").read() for sc in scripts if os.path.exists(os.path.join(ROOT, sc)))
            check(re.search(r"\b" + re.escape(out) + r"\b", body) is not None, f"steps.{sid}.outputs.{out} is written by that step ({', '.join(scripts) or 'inline'})")

    # ---- order --------------------------------------------------------------------------------------------------------------
    names = [s.get("name", "") for s in steps]

    def at(fragment):
        for i, n in enumerate(names):
            if fragment in n:
                return i
        return None

    order = [at("Signing secrets"), at("Build (Release x64, warnings as errors)"), at("Stage release assets"), at("Sign the release package"), at("Create the GitHub Release")]
    check(all(o is not None for o in order) and order == sorted(order), "order: secrets check, build, stage, sign, create release")
    create = steps[order[-1]] if order[-1] is not None else {}
    check("gh release create" in create.get("run", "") and (create.get("env") or {}).get("GH_TOKEN") == "${{ github.token }}", "the release is created with gh and the workflow's own token")
    check(create.get("if") is None, "creating the release is not conditional (every earlier step must have passed)")
    return finish()


def finish():
    print(f"check-release-workflow: {count - len(failures)} of {count} checks PASS")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
