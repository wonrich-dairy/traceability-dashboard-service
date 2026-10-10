#!/usr/bin/env python3
"""Fail if CodeQL found a High or Critical security issue (SCRUM-112).

    python3 scripts/codeql-gate.py <directory with .sarif files>

CodeQL gives each security query a numeric "security-severity". GitHub labels 9.0 and above
Critical and 7.0 to 8.9 High; those fail. Lower-severity findings are listed but do not block.

Dismissing an alert in the Security tab does not change this file, so it does not unblock the
build. Triage happens in .github/codeql/codeql-config.yml, where it is reviewed like code.
"""
import json
import os
import pathlib
import sys

BLOCKING = 7.0


def label(score: float) -> str:
    if score >= 9.0:
        return "Critical"
    if score >= 7.0:
        return "High"
    if score >= 4.0:
        return "Medium"
    return "Low"


def findings(directory: str):
    for path in pathlib.Path(directory).rglob("*.sarif"):
        for run in json.loads(path.read_text()).get("runs", []):
            tool = run.get("tool", {})
            severity = {}
            for component in [tool.get("driver", {}), *tool.get("extensions", [])]:
                for rule in component.get("rules", []):
                    score = rule.get("properties", {}).get("security-severity")
                    if score is not None:
                        severity[rule["id"]] = float(score)

            for result in run.get("results", []):
                if result.get("suppressions"):
                    continue
                rule_id = result.get("ruleId") or result.get("rule", {}).get("id")
                if rule_id not in severity:
                    continue  # not a security query
                location = (result.get("locations") or [{}])[0].get("physicalLocation", {})
                where = "{}:{}".format(
                    location.get("artifactLocation", {}).get("uri", "?"),
                    location.get("region", {}).get("startLine", "?"),
                )
                message = result.get("message", {}).get("text", "").splitlines()[0][:120]
                yield severity[rule_id], rule_id, where, message


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2

    rows = sorted(findings(sys.argv[1]), reverse=True)
    blocking = [row for row in rows if row[0] >= BLOCKING]

    lines = ["### CodeQL security findings", ""]
    if rows:
        lines += ["| Severity | Rule | Location | Message |", "|---|---|---|---|"]
        lines += [f"| {label(s)} ({s}) | `{r}` | `{w}` | {m} |" for s, r, w, m in rows]
        lines += ["", f"High / Critical: **{len(blocking)}** (blocking). Total: {len(rows)}."]
    else:
        lines.append("No security findings.")
    report = "\n".join(lines)

    print(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a") as f:
            f.write(report + "\n")

    if blocking:
        print(f"::error::{len(blocking)} High or Critical CodeQL finding(s). "
              "Fix them, or exclude the rule in .github/codeql/codeql-config.yml with a reason.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
