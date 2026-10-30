import json
from pathlib import Path
import sys


report = Path(sys.argv[1])
if not report.exists():
    print("Mutation diagnostics: no report was produced")
    sys.exit(0)

data = json.loads(report.read_text())
tests = {test["id"]: test["name"] for file in data.get("testFiles", {}).values() for test in file["tests"]}
for name, file in data["files"].items():
    for mutant in file.get("mutants", []):
        if mutant["status"] in ("Survived", "NoCoverage"):
            line = mutant["location"]["start"]["line"]
            print(json.dumps({
                "status": mutant["status"], "file": name.split("/Nealytics.Engine/")[-1], "id": mutant["id"],
                "line": line, "original": file["source"].splitlines()[line - 1].strip(),
                "replacement": mutant["replacement"],
                "covered_by": [tests.get(identity, identity) for identity in mutant.get("coveredBy", [])[:3]],
            }))
