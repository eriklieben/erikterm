#!/usr/bin/env python3
"""Tests for fuzz/report.py: the log parsing, the findings it reads, and the job summary it writes (above all, that
nothing of a failing input gets into it). Standard library only:

    python3 -m unittest discover -s fuzz -p 'test_*.py'
"""
import contextlib
import hashlib
import io
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.realpath(__file__)))
import report  # noqa: E402  (after the path)

# A libFuzzer log as fuzz/run.sh writes it for a crash: progress lines, the .NET exception, and the failing input as
# libFuzzer prints it (bytes in hex, text, Base64), which must never reach the summary.
SECRET = 'SECRETINPUT'
CRASH_LOG = f"""INFO: Seed: 1234
#2\tINITED cov: 10 ft: 11 corp: 3/120b exec/s: 0 rss: 30Mb
#40\tNEW    cov: 12 ft: 14 corp: 4/150b lim: 4 exec/s: 20 rss: 31Mb L: 30/30 MS: 1 InsertByte- {SECRET}
Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.
   at ErikTerm.Core.VtParser.Advance(Byte b) in /src/ErikTerm.Core/VtParser.cs:line 312
   at ErikTerm.Fuzz.Program.Terminal(ReadOnlySpan`1 data) in /src/fuzz/ErikTerm.Fuzz/Program.cs:line 40
==1== ERROR: libFuzzer: deadly signal
MS: 1 InsertByte-; base unit: 0000
0x53,0x45,0x43,
{SECRET}
Base64: U0VDUkVUSU5QVVQ=
stat::number_of_executed_units: 4096
stat::average_exec_per_sec:     2048
stat::new_units_added:          7
stat::peak_rss_mb:              64
"""


class FrameTests(unittest.TestCase):
    def test_a_frame_with_a_file_has_its_method_file_and_line(self):
        # Act
        result = report.frame('   at Foo.Bar(Int32 x) in /src/Foo.cs:line 12')

        # Assert
        self.assertEqual(result, ('Foo.Bar(Int32 x)', '/src/Foo.cs', '12'))

    def test_a_frame_without_a_file_has_only_its_method(self):
        # Act
        result = report.frame('   at Foo.Bar()')

        # Assert
        self.assertEqual(result, ('Foo.Bar()', None, None))

    def test_the_first_in_that_leaves_a_file_and_line_splits_method_and_file(self):
        # Act
        result = report.frame('\tat A.B() in C:\\my code\\in here\\A.cs:line 7')

        # Assert
        self.assertEqual(result, ('A.B()', 'C:\\my code\\in here\\A.cs', '7'))

    def test_an_in_without_a_line_number_is_part_of_the_method(self):
        # Act
        result = report.frame('   at A in B:line x')

        # Assert
        self.assertEqual(result, ('A in B:line x', None, None))

    def test_lines_that_arent_frames_are_none(self):
        # Arrange
        lines = ['at Foo.Bar()', '   at ', '   at', '   Foo.Bar()', '']

        # Act
        results = [report.frame(line) for line in lines]

        # Assert
        self.assertEqual(results, [None] * len(lines))


class ProgressTests(unittest.TestCase):
    def test_a_progress_line_has_the_corpus_files_and_size(self):
        # Act
        result = report.progress('#40\tNEW    cov: 12 ft: 14 corp: 4/150b lim: 4 exec/s: 20 rss: 31Mb')

        # Assert
        self.assertEqual(result, (4, '150b'))

    def test_lines_without_a_corpus_and_a_rate_are_none(self):
        # Arrange
        lines = ['#2 DONE corp: 3/4Kb', '#2 pulse exec/s: 3', '#9 REDUCE corp: x/8b exec/s: 1', '#1 OTHER corp: 1/1b exec/s: 1',
                 'stat::peak_rss_mb: 64', '#2 pulse corp: 3/x exec/s: z']

        # Act
        results = [report.progress(line) for line in lines]

        # Assert
        self.assertEqual(results, [None] * len(lines))


class PrintableTests(unittest.TestCase):
    def test_control_and_non_ascii_characters_become_question_marks(self):
        # Act
        result = report.printable('a\x1b[31mb\u00e9', 100)

        # Assert
        self.assertEqual(result, 'a?[31mb?')

    def test_long_text_is_cut_off(self):
        # Act
        result = report.printable('x' * 10, 4)

        # Assert
        self.assertEqual(result, 'xxxx...')


class StackTraceTests(unittest.TestCase):
    def test_an_exception_has_its_type_message_and_frames_without_directories(self):
        # Act
        traces = report.stack_traces(CRASH_LOG.splitlines())

        # Assert
        self.assertEqual(traces, [(
            'System.IndexOutOfRangeException',
            'Index was outside the bounds of the array.',
            ['   at ErikTerm.Core.VtParser.Advance(Byte b) (VtParser.cs:312)',
             '   at ErikTerm.Fuzz.Program.Terminal(ReadOnlySpan`1 data) (Program.cs:40)'])])

    def test_a_stack_overflow_keeps_its_repeat_count(self):
        # Arrange
        lines = ['Stack overflow.', 'Repeated 1000 times:', '--------------------------------',
                 '   at A.Recurse()', '--------------------------------', '   at A.Main()']

        # Act
        traces = report.stack_traces(lines)

        # Assert
        self.assertEqual(traces, [('System.StackOverflowException', '',
                                   ['Repeated 1000 times:', '   at A.Recurse()', '   at A.Main()'])])

    def test_an_exception_name_without_frames_is_no_trace(self):
        # Act
        traces = report.stack_traces(['System.Exception: just a line in the log', 'next line'])

        # Assert
        self.assertEqual(traces, [])


class KindTests(unittest.TestCase):
    def test_the_kind_comes_from_the_file_name_and_the_log(self):
        # Arrange
        cases = [
            ('timeout-abc', '', 'timeout'),
            ('oom-abc', '', 'oom'),
            ('crash-abc', 'System.OutOfMemoryException', 'crash (out of memory: past the 1 GB GC heap limit)'),
            ('crash-abc', 'Stack overflow.', 'crash (stack overflow)'),
            ('crash-abc', 'libFuzzer: fuzz target exited', 'crash (the .NET process exited)'),
            ('crash-abc', 'System.FormatException', 'crash (unhandled exception)'),
        ]

        # Act
        results = [report.kind_of(name, log) for name, log, _ in cases]

        # Assert
        self.assertEqual(results, [expected for _, _, expected in cases])


class TargetTests(unittest.TestCase):
    def test_a_target_is_a_folder_in_fuzz_seeds(self):
        # Act
        result = report.known_target('terminal')

        # Assert
        self.assertEqual(result, 'terminal')

    def test_anything_else_is_not_a_target(self):
        # Arrange
        names = ['nothing', '../seeds', '..', '.', '', 'terminal/', '/etc']

        # Act
        results = [report.known_target(name) for name in names]

        # Assert
        self.assertEqual(results, [None] * len(names))


class FindingsTests(unittest.TestCase):
    def setUp(self):
        self.dir = os.path.realpath(tempfile.mkdtemp(prefix='erikterm-report-'))
        self.addCleanup(lambda: __import__('shutil').rmtree(self.dir))

    def test_regular_files_are_read_in_name_order(self):
        # Arrange
        for name, data in [('timeout-2', b'bb'), ('crash-1', b'a')]:
            with open(os.path.join(self.dir, name), 'wb') as f:
                f.write(data)

        # Act
        found = report.read_findings(self.dir)

        # Assert
        self.assertEqual(found, [('crash-1', b'a'), ('timeout-2', b'bb')])

    def test_links_out_of_the_folder_and_subfolders_are_skipped(self):
        # Arrange
        outside = os.path.join(os.path.dirname(self.dir), os.path.basename(self.dir) + '-outside')
        with open(outside, 'wb') as f:
            f.write(b'not a finding')
        self.addCleanup(os.remove, outside)
        os.symlink(outside, os.path.join(self.dir, 'crash-link'))
        os.mkdir(os.path.join(self.dir, 'crash-dir'))

        # Act
        found = report.read_findings(self.dir)

        # Assert
        self.assertEqual(found, [])

    def test_a_missing_folder_has_no_findings(self):
        # Act
        found = report.read_findings(os.path.join(self.dir, 'missing'))

        # Assert
        self.assertEqual(found, [])


class MainTests(unittest.TestCase):
    """report.main() as the workflow runs it: the log in RUNNER_TEMP, the findings in FUZZ_WORK."""

    def setUp(self):
        self.temp = os.path.realpath(tempfile.mkdtemp(prefix='erikterm-report-'))
        self.addCleanup(lambda: __import__('shutil').rmtree(self.temp))
        self.findings = os.path.join(self.temp, 'work', 'findings', 'terminal')
        os.makedirs(self.findings)

    def write_log(self, text):
        with open(os.path.join(self.temp, 'fuzz.log'), 'w', encoding='utf-8') as f:
            f.write(text)

    def add_finding(self, name, data):
        with open(os.path.join(self.findings, name), 'wb') as f:
            f.write(data)

    def run_main(self, *args):
        """(exit code, stdout) of report.main() with these arguments. A refusal (sys.exit) propagates as SystemExit."""
        env = {'RUNNER_TEMP': self.temp, 'FUZZ_WORK': os.path.join(self.temp, 'work')}
        out = io.StringIO()
        with mock.patch.object(sys, 'argv', ['report.py', *args]), mock.patch.dict(os.environ, env), \
                contextlib.redirect_stdout(out):
            return report.main(), out.getvalue()

    def test_a_crash_is_reported_with_its_hash_and_stack_but_not_its_input(self):
        # Arrange
        self.write_log(CRASH_LOG)
        self.add_finding('crash-0123', SECRET.encode())

        # Act
        code, out = self.run_main('terminal', '77')

        # Assert
        self.assertEqual(code, 1)
        self.assertIn('### Fuzz target `terminal`: FAILED', out)
        self.assertIn('| 4096 | 2048 | 4 files, 150b | 7 | 64 MB |', out)
        self.assertIn(f'**crash (unhandled exception)**: 11 bytes, SHA-256 `{hashlib.sha256(SECRET.encode()).hexdigest()}`', out)
        self.assertIn('   at ErikTerm.Core.VtParser.Advance(Byte b) (VtParser.cs:312)', out)
        self.assertIn('private security advisory', out)
        self.assertNotIn(SECRET, out)
        self.assertNotIn('U0VDUkVUSU5QVVQ', out)

    def test_a_long_stack_is_cut_off(self):
        # Arrange
        frames = '\n'.join(f'   at A.M{i}()' for i in range(20))
        self.write_log(f'System.Exception: boom\n{frames}\n')
        self.add_finding('crash-1', b'x')

        # Act
        code, out = self.run_main('terminal', '1')

        # Assert
        self.assertEqual(code, 1)
        self.assertIn('   at A.M11()', out)
        self.assertNotIn('   at A.M12()', out)
        self.assertIn(f'   ... {20 - report.MAX_FRAMES} more', out)

    def test_a_timeout_without_a_stack_says_so(self):
        # Arrange
        self.write_log('ALARM: working on the last Unit for 11 seconds\n')
        self.add_finding('timeout-1', b'x')

        # Act
        code, out = self.run_main('terminal', '70')

        # Assert
        self.assertEqual(code, 1)
        self.assertIn('**timeout**: 1 bytes', out)
        self.assertIn('ran for more than the 10 s timeout', out)

    def test_a_clean_run_has_no_findings(self):
        # Arrange
        self.write_log('#2\tDONE   cov: 10 corp: 3/120b exec/s: 5 rss: 30Mb\nstat::number_of_executed_units: 10\n')

        # Act
        code, out = self.run_main('terminal', '0')

        # Assert
        self.assertEqual(code, 0)
        self.assertIn('### Fuzz target `terminal`: no findings', out)
        self.assertIn('| 10 | ? | 3 files, 120b | ? | ? MB |', out)

    def test_a_missing_log_still_reports(self):
        # Act
        code, out = self.run_main('terminal', '0')

        # Assert
        self.assertEqual(code, 0)
        self.assertIn('| ? | ? | ? | ? | ? MB |', out)

    def test_a_failure_without_a_finding_shows_the_log_without_input_lines(self):
        # Arrange
        self.write_log(f'error: the driver failed\n#3\tNEW corp: 1/1b exec/s: 1 {SECRET}\nMS: 1 {SECRET}\n'
                       f'0x41,0x42,\nBase64: {SECRET}\nlast line\x1b[0m\n')

        # Act
        code, out = self.run_main('terminal', '2')

        # Assert
        self.assertEqual(code, 1)
        self.assertIn('libFuzzer exited with 2 without a finding', out)
        self.assertIn('error: the driver failed', out)
        self.assertIn('last line?[0m', out)
        self.assertNotIn(SECRET, out)
        self.assertNotIn('0x41', out)

    def test_an_unknown_target_is_refused(self):
        # Act
        with self.assertRaises(SystemExit) as refused:
            self.run_main('../../etc', '0')

        # Assert
        self.assertIn('not a fuzz target', str(refused.exception.code))

    def test_the_old_arguments_are_refused(self):
        # Act
        with self.assertRaises(SystemExit) as refused:
            self.run_main('terminal', 'fuzz.log', 'findings', '0')

        # Assert
        self.assertIn('usage: report.py <target> <libFuzzer exit code>', str(refused.exception.code))


if __name__ == '__main__':
    unittest.main()
