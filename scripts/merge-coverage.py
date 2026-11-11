#!/usr/bin/env python3
"""Merges the Cobertura reports of the three CI platforms and checks line coverage per assembly.

A line counts as covered when any platform ran it, so Windows-only and macOS-only code is measured where it runs.
Generated code (*.g.cs: the P/Invoke marshalling stubs) is left out.

    scripts/merge-coverage.py linux.xml windows.xml macos.xml --min ErikTerm.Pty=80
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET


def source_key(filename):
    """The path from src/ on, the same on every platform; None for files outside the sources."""
    path = filename.replace("\\", "/")
    match = re.search(r"(?:^|/)(src/.*)$", path)
    return match.group(1) if match and not path.endswith(".g.cs") else None


def collect(paths):
    """{assembly: {(file, line): covered}}"""
    assemblies = {}
    for path in paths:
        for package in ET.parse(path).getroot().iter("package"):
            lines = assemblies.setdefault(package.get("name"), {})
            for cls in package.iter("class"):
                key = source_key(cls.get("filename") or "")
                if key is None:
                    continue
                for line in cls.find("lines"):
                    at = (key, int(line.get("number")))
                    lines[at] = lines.get(at, False) or int(line.get("hits")) > 0
    return assemblies


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("reports", nargs="+")
    parser.add_argument("--min", action="append", default=[], metavar="ASSEMBLY=PERCENT")
    args = parser.parse_args()
    minimum = {name: float(value) for name, value in (item.split("=") for item in args.min)}

    assemblies = collect(args.reports)
    failed = False
    all_covered = all_lines = 0
    for name in sorted(assemblies):
        lines = assemblies[name]
        covered = sum(lines.values())
        all_covered += covered
        all_lines += len(lines)
        percent = 100 * covered / max(len(lines), 1)
        below = name in minimum and percent < minimum[name]
        failed |= below
        print(f"{name:20} {covered:6}/{len(lines):6} {percent:5.1f}%{'  below ' + str(minimum[name]) if below else ''}")
    print(f"{'total':20} {all_covered:6}/{all_lines:6} {100 * all_covered / max(all_lines, 1):5.1f}%")
    for name in minimum:
        if name not in assemblies:
            print(f"{name}: no coverage reported")
            failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
