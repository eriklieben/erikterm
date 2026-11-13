#!/usr/bin/env python3
"""Summarizes one fuzz run of fuzz/run.sh without disclosing the inputs that failed.

    python3 fuzz/report.py <target> <libFuzzer log> <findings directory> <libFuzzer exit code>

Writes Markdown to stdout (the workflow appends it to the job summary): the run's statistics, and per finding its kind,
size, SHA-256 and the top frames of the .NET stack trace. libFuzzer's log has the failing input in it (as hex, as text
and as Base64, and as dictionary entries on every "NEW" line), and the log, the job summary and artifacts of a public
repository are all readable by anyone, so nothing from the log is copied here except the stack trace, which is
reduced to method names and file:line. docs/development/fuzzing.md.
"""
import hashlib
import os
import re
import sys
import tempfile

MAX_FRAMES = 12
MAX_MESSAGE = 160

# The first line of a .NET exception as SharpFuzz prints it, and what the runtime prints for a stack overflow.
EXCEPTION = re.compile(r'^(?:Unhandled exception\. )?([A-Za-z_][\w.`]*(?:Exception|Error))(?:: (.*))?$')
FRAME = re.compile(r'^\s+at (.+?)(?: in (.+):line (\d+))?$')
STAT = re.compile(r'^stat::(\w+):\s+(\d+)$')
PROGRESS = re.compile(r'^#(\d+)\s+(?:INITED|DONE|pulse|NEW|REDUCE).*?corp: (\d+)/(\S+).*?exec/s: (\d+)')


def printable(text, limit):
    text = ''.join(c if ' ' <= c <= '~' else '?' for c in text)
    return text if len(text) <= limit else text[:limit] + '...'


def stack_traces(lines):
    """Each .NET stack trace in the log: (exception type, message, frames)."""
    traces = []
    i = 0
    while i < len(lines):
        line = lines[i]
        if line.startswith('Stack overflow.'):
            exc, message = 'System.StackOverflowException', ''
        elif (m := EXCEPTION.match(line)) and i + 1 < len(lines) and FRAME.match(lines[i + 1]):
            exc, message = m.group(1), m.group(2) or ''
        else:
            i += 1
            continue
        frames = []
        i += 1
        while i < len(lines) and (FRAME.match(lines[i]) or lines[i].startswith(('Repeated ', '----', '   at '))):
            f = FRAME.match(lines[i])
            if f:
                where = f' ({os.path.basename(f.group(2))}:{f.group(3)})' if f.group(2) else ''
                frames.append('   at ' + printable(f.group(1), 200) + where)
            elif lines[i].startswith('Repeated '):
                frames.append(printable(lines[i].strip(), 60))  # a stack overflow's recursion
            i += 1
        traces.append((exc, printable(message, MAX_MESSAGE), frames))
    return traces


def kind_of(name, log_text):
    prefix = name.split('-', 1)[0]
    if prefix != 'crash':
        return prefix  # timeout, oom, leak, slow-unit
    if 'System.OutOfMemoryException' in log_text:
        return 'crash (out of memory: past the 1 GB GC heap limit)'
    if 'Stack overflow.' in log_text:
        return 'crash (stack overflow)'
    if 'libFuzzer: fuzz target exited' in log_text:
        return 'crash (the .NET process exited)'
    return 'crash (unhandled exception)'


TARGET = re.compile(r'^[A-Za-z0-9_-]{1,64}$')


def allowed_roots():
    """Where the log and the findings may be: the checkout (the working directory) and the runner's temp directory."""
    roots = [os.getcwd(), tempfile.gettempdir()]
    if os.environ.get('RUNNER_TEMP'):
        roots.append(os.environ['RUNNER_TEMP'])
    return [os.path.realpath(r) for r in roots]


def inside(path, roots):
    """The resolved path when it is in one of roots (symbolic links followed), else None."""
    real = os.path.realpath(path)
    for root in roots:
        if os.path.commonpath([real, root]) == root:
            return real
    return None


def read_findings(findings_dir):
    """(name, contents) of each regular file in the findings directory, none that leave it through a link."""
    if not os.path.isdir(findings_dir):
        return []
    found = []
    for name in sorted(os.listdir(findings_dir)):
        path = inside(os.path.join(findings_dir, name), [findings_dir])
        if path is None or not os.path.isfile(path):
            continue
        with open(path, 'rb') as f:
            found.append((name, f.read()))
    return found


def main():
    target, log_arg, findings_arg, exit_code = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
    if not TARGET.match(target):
        sys.exit(f'report.py: not a fuzz target name: {target!r}')
    roots = allowed_roots()
    log_path, findings_dir = inside(log_arg, roots), inside(findings_arg, roots)
    if log_path is None or findings_dir is None:
        sys.exit('report.py: the log and the findings directory must be in the working directory or the temp directory')
    try:
        with open(log_path, encoding='utf-8', errors='replace') as f:
            log_text = f.read()
    except FileNotFoundError:
        log_text = ''
    lines = log_text.splitlines()

    stats = {m.group(1): int(m.group(2)) for m in map(STAT.match, lines) if m}
    corpus = None
    for m in map(PROGRESS.match, lines):
        if m:
            corpus = (int(m.group(2)), m.group(3))

    findings = read_findings(findings_dir)
    status = 'finding' if findings else ('ok' if exit_code == 0 else 'error')

    out = [f'### Fuzz target `{target}`: {"FAILED" if status != "ok" else "no findings"}', '']
    out.append('| executions | per second | corpus | new inputs | peak RSS |')
    out.append('|---:|---:|---:|---:|---:|')
    corp = f'{corpus[0]} files, {corpus[1]}' if corpus else '?'
    out.append(f'| {stats.get("number_of_executed_units", "?")} | {stats.get("average_exec_per_sec", "?")} | {corp} '
               f'| {stats.get("new_units_added", "?")} | {stats.get("peak_rss_mb", "?")} MB |')
    out.append('')

    traces = stack_traces(lines)
    for name, data in findings:
        out.append(f'**{kind_of(name, log_text)}**: {len(data)} bytes, SHA-256 `{hashlib.sha256(data).hexdigest()}`')
        out.append('')
        if traces:
            exc, message, frames = traces[-1]
            out.append('```')
            out.append(f'{exc}: {message}' if message else exc)
            out.extend(frames[:MAX_FRAMES])
            if len(frames) > MAX_FRAMES:
                out.append(f'   ... {len(frames) - MAX_FRAMES} more')
            out.append('```')
        elif name.startswith('timeout'):
            out.append('One input ran for more than the 10 s timeout (no stack trace: libFuzzer stops the .NET process).')
        out.append('')

    if findings:
        out.append('The input is not published: it stays on the runner (docs/development/fuzzing.md, "Findings"). '
                   'Treat this as a possible security issue until it is understood, and track it in a private '
                   'security advisory, not a public issue.')
    elif status == 'error':
        # Not a finding (the build failed, or the driver did): the end of the log, without the input-shaped lines.
        out.append(f'libFuzzer exited with {exit_code} without a finding. The end of its log:')
        out.append('')
        out.append('```')
        safe = [l for l in lines if not re.match(r'^(Base64:|0x[0-9a-f]{2},|MS: |#\d+\s+(NEW|REDUCE))', l)]
        out.extend(printable(l, 300) for l in safe[-30:])
        out.append('```')

    print('\n'.join(out))
    return 1 if status != 'ok' else 0


if __name__ == '__main__':
    sys.exit(main())
