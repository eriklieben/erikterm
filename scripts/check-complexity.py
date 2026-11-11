#!/usr/bin/env python3
"""Keeps method complexity from growing, from the CA1502 warnings of `dotnet build -p:Quality=true`.

Methods already over the limit are listed in build/complexity-baseline.txt. A new one fails, and so does a listed one
that got more complex. Refactoring one lowers the bar: refresh the list with --write.

    dotnet build src/ErikTerm.App -p:Quality=true --no-incremental | tee quality.log
    scripts/check-complexity.py quality.log build/complexity-baseline.txt [--write]
"""
import re
import sys

WARNING = re.compile(r"(src[/\\][^(]+)\(\d+,\d+\): warning CA1502: '([^']+)' has a cyclomatic complexity of '(\d+)'")


def found(log):
    """{"file::method": complexity}; overloads in one file share the highest."""
    methods = {}
    with open(log, errors="replace") as lines:
        for line in lines:
            match = WARNING.search(line)
            if match:
                key = f"{match[1].replace(chr(92), '/')}::{match[2]}"
                methods[key] = max(methods.get(key, 0), int(match[3]))
    return methods


def load(path):
    baseline = {}
    with open(path) as lines:
        for line in lines:
            line = line.strip()
            if line and not line.startswith("#"):
                value, key = line.split(None, 1)
                baseline[key] = int(value)
    return baseline


def main():
    args = [a for a in sys.argv[1:] if a != "--write"]
    if len(args) != 2:
        print(__doc__)
        return 2
    log, path = args
    now = found(log)
    if not now:
        print(f"no CA1502 warnings in {log}: was it built with -p:Quality=true?")
        return 2
    if "--write" in sys.argv:
        with open(path, "w") as out:
            out.write("# Methods over the complexity limit when the check was introduced. Only ever shrinks: see scripts/check-complexity.py.\n")
            for key in sorted(now):
                out.write(f"{now[key]:4} {key}\n")
        print(f"{len(now)} methods written to {path}")
        return 0

    baseline = load(path)
    failed = False
    for key, value in sorted(now.items()):
        if key not in baseline:
            print(f"new method over the limit: {key} ({value}). Split it up.")
            failed = True
        elif value > baseline[key]:
            print(f"more complex than before: {key} ({baseline[key]} -> {value}). Split it up.")
            failed = True
    improved = sum(1 for key, value in baseline.items() if now.get(key, 0) < value)
    print(f"{len(now)} methods over the limit, baseline {len(baseline)}, improved {improved}")
    if improved:
        print(f"lower the bar: scripts/check-complexity.py {log} {path} --write")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
