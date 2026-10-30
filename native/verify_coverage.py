import argparse
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("results", type=Path)
    parser.add_argument("gcov", type=Path)
    args = parser.parse_args()
    required = {
        "Storage/WriteAheadLogger.cs", "Wal/WalFileFactory.cs", "Wal/WalFileSelection.cs",
        "Wal/WalDirectory.cs", "Wal/WalDirectoryDurability.cs", "Wal/IWalDirectoryOperations.cs",
        "Wal/PosixWalDirectoryOperations.cs",
        *["Wal/Native/" + name + ".cs" for name in (
            "LiburingWalContext", "NativeWalApi", "NativeWalException", "NativeWalFile",
            "NativeWalHandle", "NativeWalPlatform", "NativeWalSignal",
        )],
    }
    lines, branches, observed = {}, {}, set()
    reports = list(args.results.glob("Nealytics.Engine.Tests.*/**/coverage.cobertura.xml"))
    assert reports, "No managed coverage reports were produced"
    for report in reports:
        for item in ET.parse(report).findall(".//class"):
            source = item.attrib["filename"].replace("\\", "/")
            matches = [suffix for suffix in required if source.endswith(suffix)]
            if not matches:
                continue
            name = matches[0]
            observed.add(name)
            for line in item.findall("./lines/line"):
                key = name, int(line.attrib["number"])
                lines[key] = lines.get(key, False) or int(line.attrib["hits"]) > 0
                condition = line.attrib.get("condition-coverage")
                if condition:
                    covered, total = map(int, re.search(r"\((\d+)/(\d+)\)", condition).groups())
                    previous = branches.get(key, (0, total))
                    assert previous[1] == total, (key, "Incompatible source coverage reports")
                    branches[key] = max(previous[0], covered), total
    assert observed == required, ("Missing maintained source coverage", sorted(required - observed))
    uncovered = [key for key, hit in lines.items() if not hit]
    partial = [key for key, (covered, total) in branches.items() if covered != total]
    assert not uncovered and not partial, {"uncovered_lines": uncovered, "incomplete_branches": partial}

    statements, native_branches, gaps = 0, 0, []
    for line in args.gcov.read_text().splitlines():
        statement = re.match(r"\s*([^:]+):\s*(\d+):", line)
        if statement and statement[1].strip() != "-":
            statements += 1
            count = statement[1].strip().rstrip("*")
            if not count.isdigit() or int(count) == 0:
                gaps.append(line)
        if line.startswith("branch "):
            native_branches += 1
            taken = re.search(r"taken (\d+)", line)
            if not taken or int(taken[1]) == 0:
                gaps.append(line)
    assert statements > 0 and native_branches > 0, "No maintained native bridge coverage was produced"
    assert not gaps, {"native_coverage_gaps": gaps}
    print(json.dumps({"managed_lines": len(lines), "managed_branch_outcomes": sum(total for _, total in branches.values()),
                      "native_statements": statements, "native_branch_outcomes": native_branches,
                      "line_and_branch_coverage": "100%"}))


if __name__ == "__main__":
    main()
