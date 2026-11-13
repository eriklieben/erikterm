#!/usr/bin/env python3
"""Summarizes one fuzz run of fuzz/run.sh without disclosing the inputs that failed.

    python3 fuzz/report.py <target> <libFuzzer exit code>

The log is $RUNNER_TEMP/fuzz.log (else fuzz.log in the temp directory) and the findings are in
$FUZZ_WORK/findings/<target>/ (FUZZ_WORK defaults to artifacts/fuzz, as in fuzz/run.sh). The target must be one of
the folders in fuzz/seeds/.

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
STAT = re.compile(r'^stat::(\w+):\s+(\d+)$')
PROGRESS = re.compile(r'#\d+\s+(?:INITED|DONE|pulse|NEW|REDUCE)')
CORPUS = re.compile(r'(\d+)/(\S+)')


def printable(text, limit):
    text = ''.join(c if ' ' <= c <= '~' else '?' for c in text)
    return text if len(text) <= limit else text[:limit] + '...'


def frame(line):
    """An indented '   at Method(...) in File.cs:line 12' line as (method, file, line), file and line None when
    absent; None for any other line. Plain string scanning: no regular expression that could backtrack."""
    body = line.lstrip()
    if body == line or not body.startswith('at ') or len(body) == 3:
        return None
    body = body[3:]
    start = body.find(' in ', 1)
    while start > 0:
        where, sep, number = body[start + 4:].rpartition(':line ')
        if sep and where and number.isdigit():
            return body[:start], where, number
        start = body.find(' in ', start + 1)
    return body, None, None


def progress(line):
    """The corpus (files, size) of a libFuzzer progress line ('#123 NEW ... corp: 45/6789b ... exec/s: 10'), or None."""
    m = PROGRESS.match(line)
    if not m:
        return None
    at = line.find('corp: ', m.end())
    c = CORPUS.match(line, at + 6) if at >= 0 else None
    if not c:
        return None
    rate = line.find('exec/s: ', c.end())
    return (int(c.group(1)), c.group(2)) if rate >= 0 and line[rate + 8:rate + 9].isdigit() else None


def trace_start(lines, i):
    """(exception type, message) when a stack trace starts at lines[i], else None."""
    if lines[i].startswith('Stack overflow.'):
        return 'System.StackOverflowException', ''
    m = EXCEPTION.match(lines[i])
    if m and i + 1 < len(lines) and frame(lines[i + 1]):
        return m.group(1), m.group(2) or ''
    return None


def in_trace(line):
    """Whether a line still belongs to a stack trace: a frame, or a stack overflow's repeat marks."""
    return frame(line) is not None or line.startswith(('Repeated ', '----', '   at '))


def frame_line(line):
    """A trace line as the summary shows it (methods and file:line only), or None for a separator."""
    f = frame(line)
    if f:
        method, file, number = f
        where = f' ({os.path.basename(file)}:{number})' if file else ''
        return '   at ' + printable(method, 200) + where
    if line.startswith('Repeated '):
        return printable(line.strip(), 60)  # a stack overflow's recursion
    return None


def stack_traces(lines):
    """Each .NET stack trace in the log: (exception type, message, frames)."""
    traces = []
    i = 0
    while i < len(lines):
        start = trace_start(lines, i)
        i += 1
        if start is None:
            continue
        frames = []
        while i < len(lines) and in_trace(lines[i]):
            shown = frame_line(lines[i])
            if shown is not None:
                frames.append(shown)
            i += 1
        traces.append((start[0], printable(start[1], MAX_MESSAGE), frames))
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


HERE = os.path.dirname(os.path.realpath(__file__))


def known_target(name):
    """The fuzz target called name, taken from the folders in fuzz/seeds/ (one per target), or None."""
    seeds = os.path.join(HERE, 'seeds')
    return next((t for t in sorted(os.listdir(seeds)) if t == name and os.path.isdir(os.path.join(seeds, t))), None)


def read_findings(findings_dir):
    """(name, contents) of each regular file in the findings directory, none that leave it through a link."""
    if not os.path.isdir(findings_dir):
        return []
    found = []
    for name in sorted(os.listdir(findings_dir)):
        path = os.path.realpath(os.path.join(findings_dir, name))
        if os.path.dirname(path) != findings_dir or not os.path.isfile(path):
            continue
        with open(path, 'rb') as f:
            found.append((name, f.read()))
    return found


def read_log(path):
    """The libFuzzer log, or '' when there is none (the run didn't get that far)."""
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            return f.read()
    except FileNotFoundError:
        return ''


def run_stats(lines):
    """libFuzzer's closing stat:: lines as a dict, and the corpus of its last progress line (or None)."""
    stats = {m.group(1): int(m.group(2)) for m in map(STAT.match, lines) if m}
    corpus = None
    for line in lines:
        corpus = progress(line) or corpus
    return stats, corpus


def stats_table(stats, corpus):
    """The run's statistics as a Markdown table, '?' for what the log doesn't say."""
    corp = f'{corpus[0]} files, {corpus[1]}' if corpus else '?'
    return [
        '| executions | per second | corpus | new inputs | peak RSS |',
        '|---:|---:|---:|---:|---:|',
        f'| {stats.get("number_of_executed_units", "?")} | {stats.get("average_exec_per_sec", "?")} | {corp} '
        f'| {stats.get("new_units_added", "?")} | {stats.get("peak_rss_mb", "?")} MB |',
        '',
    ]


def finding_lines(name, data, log_text, traces):
    """One finding: its kind, size and SHA-256, and the last stack trace (never the input itself)."""
    out = [f'**{kind_of(name, log_text)}**: {len(data)} bytes, SHA-256 `{hashlib.sha256(data).hexdigest()}`', '']
    if traces:
        exc, message, frames = traces[-1]
        out.extend(['```', f'{exc}: {message}' if message else exc, *frames[:MAX_FRAMES]])
        if len(frames) > MAX_FRAMES:
            out.append(f'   ... {len(frames) - MAX_FRAMES} more')
        out.append('```')
    elif name.startswith('timeout'):
        out.append('One input ran for more than the 10 s timeout (no stack trace: libFuzzer stops the .NET process).')
    out.append('')
    return out


def error_lines(exit_code, lines):
    """Not a finding (the build failed, or the driver did): the end of the log, without the input-shaped lines."""
    safe = [l for l in lines if not re.match(r'^(Base64:|0x[0-9a-f]{2},|MS: |#\d+\s+(NEW|REDUCE))', l)]
    return [f'libFuzzer exited with {exit_code} without a finding. The end of its log:', '', '```',
            *(printable(l, 300) for l in safe[-30:]), '```']


def main():
    if len(sys.argv) != 3:
        sys.exit('usage: report.py <target> <libFuzzer exit code>')
    target, exit_code = known_target(sys.argv[1]), int(sys.argv[2])
    if target is None:
        sys.exit(f'report.py: not a fuzz target (no fuzz/seeds/ folder of that name): {sys.argv[1]!r}')
    # Where fuzz/run.sh and the workflow put them: the log in the runner's temp directory, the findings in FUZZ_WORK.
    log_path = os.path.join(os.environ.get('RUNNER_TEMP') or tempfile.gettempdir(), 'fuzz.log')
    work = os.environ.get('FUZZ_WORK') or os.path.join(os.path.dirname(HERE), 'artifacts', 'fuzz')
    findings_dir = os.path.realpath(os.path.join(work, 'findings', target))
    log_text = read_log(log_path)
    lines = log_text.splitlines()
    stats, corpus = run_stats(lines)

    findings = read_findings(findings_dir)
    if findings:
        status = 'finding'
    else:
        status = 'ok' if exit_code == 0 else 'error'

    out = [f'### Fuzz target `{target}`: {"FAILED" if status != "ok" else "no findings"}', '']
    out.extend(stats_table(stats, corpus))
    traces = stack_traces(lines)
    for name, data in findings:
        out.extend(finding_lines(name, data, log_text, traces))
    if findings:
        out.append('The input is not published: it stays on the runner (docs/development/fuzzing.md, "Findings"). '
                   'Treat this as a possible security issue until it is understood, and track it in a private '
                   'security advisory, not a public issue.')
    elif status == 'error':
        out.extend(error_lines(exit_code, lines))

    print('\n'.join(out))
    return 1 if status != 'ok' else 0


if __name__ == '__main__':
    sys.exit(main())
