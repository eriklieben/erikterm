#!/usr/bin/env python3
"""Merges the Cobertura reports of the three CI platforms and checks line coverage per project.

A line counts as covered when any platform ran it, so Windows-only and macOS-only code is measured where it runs.
Generated code (*.g.cs and anything under obj/: the P/Invoke and COM marshalling stubs) is left out.

Lines count for the project their file lives in (src/<project>/...), not for the assembly that ran them: the tests
compile part of ErikTerm.App into the test assembly (tests/ErikTerm.Tests/coverage.config), and that code is
reported as ErikTerm.App. The total covers the gated projects (--min) only, so it means the same as before App was
counted; the other projects follow it, without a gate. For ErikTerm.App only the files compiled into the tests are
in the reports, so its line also says how many of its source files that is (docs/development/code-analysis.md).

    scripts/merge-coverage.py linux.xml windows.xml macos.xml --min ErikTerm.Pty=80
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

REPOSITORY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def source_key(filename):
    """The path from src/ on, the same on every platform; None for files outside the sources and generated code."""
    path = filename.replace("\\", "/")
    match = re.search(r"(?:^|/)(src/.*)$", path)
    if not match or path.endswith(".g.cs") or "/obj/" in match.group(1):
        return None
    return match.group(1)


def project_of(key):
    """src/ErikTerm.Core/Grid.cs -> ErikTerm.Core"""
    return key.split("/")[1]


def collect(paths):
    """{project: {(file, line): covered}}"""
    projects = {}
    for path in paths:
        for cls in ET.parse(path).getroot().iter("class"):
            key = source_key(cls.get("filename") or "")
            if key is None:
                continue
            lines = projects.setdefault(project_of(key), {})
            for line in cls.find("lines"):
                at = (key, int(line.get("number")))
                lines[at] = lines.get(at, False) or int(line.get("hits")) > 0
    return projects


def source_files(project):
    """The project's .cs files on disk (src/<project>/...), without build output; None when it isn't there."""
    top = os.path.join(REPOSITORY, "src", project)
    if not os.path.isdir(top):
        return None
    found = set()
    for folder, dirs, files in os.walk(top):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in files:
            if name.endswith(".cs") and not name.endswith(".g.cs"):
                found.add(os.path.relpath(os.path.join(folder, name), REPOSITORY).replace(os.sep, "/"))
    return found


def row(name, lines, minimum=None, note=""):
    """Prints one project; returns (covered lines, below the minimum)."""
    covered = sum(lines.values())
    percent = 100 * covered / max(len(lines), 1)
    below = minimum is not None and percent < minimum
    print(f"{name:20} {covered:6}/{len(lines):6} {percent:5.1f}%{'  below ' + str(minimum) if below else ''}{note}")
    return covered, below


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("reports", nargs="+")
    parser.add_argument("--min", action="append", default=[], metavar="PROJECT=PERCENT")
    args = parser.parse_args()
    minimum = {name: float(value) for name, value in (item.split("=") for item in args.min)}

    projects = collect(args.reports)
    gated = [name for name in sorted(projects) if not minimum or name in minimum]
    failed = False
    all_covered = all_lines = 0
    for name in gated:
        lines = projects[name]
        covered, below = row(name, lines, minimum.get(name))
        failed |= below
        all_covered += covered
        all_lines += len(lines)
    print(f"{'total':20} {all_covered:6}/{all_lines:6} {100 * all_covered / max(all_lines, 1):5.1f}%")
    for name in sorted(set(projects) - set(gated)):
        on_disk = source_files(name)
        in_report = {key for key, _ in projects[name]}
        files = f", {len(in_report)} of {len(on_disk)} source files" if on_disk else ""
        row(name, projects[name], note=f"  (no gate{files})")
    for name in minimum:
        if name not in projects:
            print(f"{name}: no coverage reported")
            failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
