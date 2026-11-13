#!/usr/bin/env python3
"""Points the Cobertura reports of the three CI platforms at this checkout, for SonarQube Cloud.

The test jobs write absolute paths of their own runner (/home/runner/work/..., /Users/runner/work/...,
D:\\a\\...). SonarQube Cloud only imports coverage for files it finds, so every file name becomes
<root>/src/... with forward slashes. Classes outside src/ and generated code (*.g.cs, obj/) are left out, as in
scripts/merge-coverage.py. The reports are rewritten in place; SonarQube Cloud adds up the hits of all three.
ErikTerm.App's files come from the test assembly (they're compiled into it, tests/ErikTerm.Tests/coverage.config);
their paths are the real src/ErikTerm.App/... ones, so they map like the libraries' files.

    scripts/sonar-coverage.py --root "$GITHUB_WORKSPACE" coverage/*/coverage.xml
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET


def source_key(filename):
    """The path from src/ on, the same on every platform; None for files outside the sources and generated code."""
    path = filename.replace("\\", "/")
    match = re.search(r"(?:^|/)(src/.*)$", path)
    if not match or path.endswith(".g.cs") or "/obj/" in match.group(1):
        return None
    return match.group(1)


def rewrite(path, root):
    """Rewrites one report; returns (kept, dropped) class counts."""
    tree = ET.parse(path)
    kept = dropped = 0
    for classes in tree.getroot().iter("classes"):
        for cls in list(classes):
            key = source_key(cls.get("filename") or "")
            if key is None:
                classes.remove(cls)
                dropped += 1
            else:
                cls.set("filename", f"{root}/{key}")
                kept += 1
    tree.write(path, encoding="utf-8", xml_declaration=True)
    return kept, dropped


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, help="the checkout the analysis runs in")
    parser.add_argument("reports", nargs="+")
    args = parser.parse_args()
    root = args.root.replace("\\", "/").rstrip("/")
    for report in args.reports:
        kept, dropped = rewrite(report, root)
        print(f"{report}: {kept} classes under src/, {dropped} left out")
        if kept == 0 and dropped == 0:
            # An empty report: the collector measured nothing (so far dotnet-coverage on the macOS runner, see
            # docs/development/code-analysis.md). Nothing to map; the other reports still count.
            print(f"::warning::{report} is empty: no coverage was collected for it")
        elif kept == 0:
            print(f"{report}: no file under src/", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
