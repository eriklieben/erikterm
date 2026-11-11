#!/usr/bin/env python3
"""Converts an xUnit (v2/v3) test file to TUnit, mechanically.

    scripts/xunit-to-tunit.py tests/ErikTerm.Tests/NewTests.cs            # rewrites the file in place
    scripts/xunit-to-tunit.py --stdout Some.cs > Converted.cs              # or print the result
    scripts/xunit-to-tunit.py --check a.cs b.cs                            # report what needs a hand, change nothing
    scripts/xunit-to-tunit.py --build tests/ErikTerm.Tests/NewTests.cs    # recommended: let the compiler help

--build asks the compiler instead of guessing: a probe build (the file temporarily rewritten with every assertion
subject wrapped in `(expr).__TypeProbe<n>`, restored afterwards) makes the compiler name each subject's static type,
which decides IsEquivalentTo vs IsEqualTo (arrays, lists, dictionaries), turns spans into arrays and widens numeric
subjects to the expected value's type (xUnit's Equal<T> did that). A final build then lists every error and warning
still left in the converted file. It uses the nearest .csproj above the file (or --project). Convert all of a
branch's xUnit files in one run (`--build a.cs b.cs`): a file the project can't compile at the declaration level (an
unresolved [Fact]) makes the compiler skip every method body, so it would report no types and no errors.

What it does:
  * attributes: [Fact]/[Theory] -> [Test] (Skip/DisplayName/Timeout/Explicit kept), [InlineData] -> [Arguments],
    [MemberData] -> [MethodDataSource], [Trait] -> [Property], [Collection] -> [NotInParallel],
    [LinuxFact]/[WindowsFact]/[MacFact] -> [Test, RunOn(OS.X)]; custom Fact/Theory attribute classes are removed.
  * Assert.* -> `await Assert.That(actual).Xxx(expected)` for the common assertions (Equal, NotEqual, True, False,
    Null, NotNull, Same, Empty, Single, Contains, DoesNotContain, StartsWith, EndsWith, Matches, InRange, IsType,
    Throws*, All, Collection, Skip*). Ordered collection equality -> IsEquivalentTo(.., CollectionOrdering.Matching)
    when the expression looks like a collection (the script has no type information: see "manual fixes").
  * signatures: methods that now await become `async Task` (helpers too; their call sites get `await`).
  * TheoryData members -> IEnumerable<(..)> methods; IDisposable/IAsyncLifetime -> [After(Test)]/[Before(Test)];
    IClassFixture<T> -> [ClassDataSource<T>(Shared = SharedType.PerClass)];
    TestContext.Current.CancellationToken -> a CancellationToken test parameter.
  * AAA: inserts `// Arrange`, `// Act`, `// Assert` where the shape of the test makes them clear (setup, one act,
    then assertions; an assertion whose subject is the act gets the act hoisted into a local). Where it can't tell,
    it leaves `// TODO(AAA): ...` and anything it could not convert gets `// TODO(TUnit): ...`.

Manual fixes it leaves for you (the compiler or a test run points at each): without --build, collections compared
with IsEqualTo (a test run fails on them); dictionary Contains(key) -> ContainsKey; await inside lock/unsafe (marked
TODO(TUnit)); span locals (`out var text` of a ReadOnlySpan) that live across an await (CS4007: convert them to a
string/array first); assertions inside lambdas; and the TODO(AAA) tests, which need a human to split them by
behaviour ([Arguments] rows, separate tests, or one walk-through). Then check [NotInParallel] for tests that share
process-wide state: TUnit runs every test in parallel. Python 3 standard library only.
"""

import argparse
import glob
import os
import re
import subprocess
import sys

# --------------------------------------------------------------------------------------------------------------------
# Lexing: a "mask" of the source with the same length, where string/char literals keep only their delimiters (contents
# become '_') and comments become spaces. Brackets, commas and keywords found in the mask are real code.

IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')


def _skip_hole(s, k):
    """k is just after the '{' of an interpolation hole; returns the index of its closing '}'."""
    depth = 0
    n = len(s)
    while k < n:
        c = s[k]
        e = literal_end(s, k)
        if e > 0:
            k = e
            continue
        if c in '([{':
            depth += 1
        elif c in ')]':
            depth -= 1
        elif c == '}':
            if depth == 0:
                return k
            depth -= 1
        elif c == ':' and depth == 0:  # format specifier: runs to the '}'
            j = s.find('}', k)
            return j if j >= 0 else n
        k += 1
    return n


def literal_end(s, i):
    """If a string or char literal starts at i, the index just past it (u8 suffix included); else -1."""
    n = len(s)
    j = i
    dollars = 0
    verbatim = False
    while j < n and s[j] in '$@':
        if s[j] == '$':
            dollars += 1
        else:
            verbatim = True
        j += 1
    if j >= n or s[j] not in '"\'':
        return -1
    if s[j] == "'":
        if j != i:
            return -1
        k = j + 1
        if k < n and s[k] == '\\':
            k += 2
            while k < n and s[k] != "'":
                k += 1
        else:
            k += 1
        return min(k + 1, n)
    q = 0
    while j + q < n and s[j + q] == '"':
        q += 1
    if q >= 3 and not verbatim:  # raw string literal
        k = j + q
        while k < n:
            if s.startswith('"' * q, k):
                end = k + q
                break
            if dollars and s.startswith('{' * dollars, k):
                k = _skip_hole(s, k + dollars)
                k += dollars
                continue
            k += 1
        else:
            end = n
    elif q == 2:
        end = j + 2
    else:
        k = j + 1
        while k < n:
            c = s[k]
            if verbatim:
                if c == '"':
                    if k + 1 < n and s[k + 1] == '"':
                        k += 2
                        continue
                    break
            else:
                if c == '\\':
                    k += 2
                    continue
                if c == '"' or c == '\n':
                    break
            if dollars and c == '{':
                if k + 1 < n and s[k + 1] == '{':
                    k += 2
                    continue
                k = _skip_hole(s, k + 1) + 1
                continue
            k += 1
        end = min(k + 1, n)
    if s.startswith('u8', end) or s.startswith('U8', end):
        end += 2
    return end


def mask(s):
    out = list(s)
    i, n = 0, len(s)
    while i < n:
        if s.startswith('//', i):
            j = s.find('\n', i)
            j = n if j < 0 else j
            for k in range(i, j):
                out[k] = ' '
            i = j
            continue
        if s.startswith('/*', i):
            j = s.find('*/', i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if out[k] != '\n':
                    out[k] = ' '
            i = j
            continue
        c = s[i]
        if c in '"\'$@' and (i == 0 or not (s[i - 1].isalnum() or s[i - 1] == '_')):
            e = literal_end(s, i)
            if e > 0:
                quote = "'" if s[i] == "'" else '"'
                for k in range(i, e):
                    if out[k] != '\n':
                        out[k] = '_'
                out[i] = quote
                last = e - 1
                if s.startswith('u8', e - 2) or s.startswith('U8', e - 2):
                    last = e - 3
                out[last] = quote
                i = e
                continue
        i += 1
    return ''.join(out)


def match_close(m, i):
    """m[i] is one of ([{ ; returns the index of the matching closer (in the mask)."""
    pairs = {'(': ')', '[': ']', '{': '}'}
    stack = [pairs[m[i]]]
    k = i + 1
    while k < len(m):
        c = m[k]
        if c in pairs:
            stack.append(pairs[c])
        elif c in ')]}':
            if not stack or c != stack[-1]:
                return -1
            stack.pop()
            if not stack:
                return k
        k += 1
    return -1


GENERIC_OK = re.compile(r'[\w\s,.?\[\]()<>:]')


def generic_close(m, i):
    """If m[i] == '<' opens a generic argument list, the index of its '>'; else -1."""
    if i == 0 or not (m[i - 1].isalnum() or m[i - 1] == '_'):
        return -1
    if i + 1 >= len(m) or not (m[i + 1].isalpha() or m[i + 1] in '_(['):
        return -1
    depth = 0
    k = i
    while k < len(m):
        c = m[k]
        if c == '<':
            depth += 1
        elif c == '>':
            depth -= 1
            if depth == 0:
                return k
        elif not GENERIC_OK.match(c):
            return -1
        k += 1
    return -1


def split_top(m, start, end):
    """Spans of the comma-separated pieces of m[start:end] at bracket depth 0."""
    spans = []
    depth = 0
    k = start
    piece = start
    while k < end:
        c = m[k]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        elif c == '<':
            g = generic_close(m, k)
            if g > 0 and g < end:
                k = g + 1
                continue
        elif c == ',' and depth == 0:
            spans.append((piece, k))
            piece = k + 1
        k += 1
    if m[piece:end].strip() or spans:
        spans.append((piece, end))
    return spans


def line_indent(s, pos):
    ls = s.rfind('\n', 0, pos) + 1
    j = ls
    while j < len(s) and s[j] in ' \t':
        j += 1
    return s[ls:j]


# --------------------------------------------------------------------------------------------------------------------
# Assertions

COLLECTION_TAILS = {'ToArray', 'ToList', 'ToImmutableArray', 'ToImmutableList', 'Select', 'SelectMany', 'Where',
                    'OrderBy', 'OrderByDescending', 'ThenBy', 'Reverse', 'Skip', 'Take', 'SkipWhile', 'TakeWhile',
                    'Concat', 'Distinct', 'Keys', 'Values', 'Split', 'Cast', 'OfType', 'GetBytes', 'Chunk', 'Zip',
                    'Prepend', 'Append', 'Range', 'Repeat', 'AsEnumerable', 'Lines', 'ReadAllLines', 'ReadAllBytes'}


COLLECTION_PROPERTIES = {'Keys', 'Values'}


def last_member(m):
    """(name, is_call) of the last member access in an expression mask: `a.B(x)` -> ('B', True), `a.B` -> ('B', False)."""
    m = m.rstrip()
    if m.endswith(')'):
        depth = 0
        for k in range(len(m) - 1, -1, -1):
            if m[k] == ')':
                depth += 1
            elif m[k] == '(':
                depth -= 1
                if depth == 0:
                    head = m[:k].rstrip()
                    if head.endswith('>'):
                        lt = head.rfind('<')
                        head = head[:lt] if lt > 0 else head
                    mm = re.search(r'\.\s*([A-Za-z_]\w*)$', head)
                    return (mm.group(1), True) if mm else (None, True)
        return None, True
    mm = re.search(r'\.\s*([A-Za-z_]\w*)$', m)
    return (mm.group(1), False) if mm else (None, False)


def looks_like_collection(expr):
    mk = mask(expr).strip()
    if not mk:
        return False
    if mk.startswith('['):
        return True
    if re.match(r'new\s*(\w[\w.<>,\s?]*)?\[\s*\d*\s*\]', mk) or re.match(r'new\s+(List|HashSet|SortedSet|Queue|Stack|LinkedList|ImmutableArray)\s*<', mk):
        return True
    if re.match(r'(Array\.Empty|Enumerable\.\w+|ImmutableArray\.Create)', mk):
        return True
    if re.search(r'"u8$', mk):
        return True
    # `x.Split(..)` / `x.ToArray()` / `d.Keys`, but not a property that happens to share a name (`Colors.Split`).
    name, call = last_member(mk)
    return name in COLLECTION_TAILS and (call or name in COLLECTION_PROPERTIES)


def is_dictionary(expr):
    return bool(re.match(r'new\s+(Dictionary|SortedDictionary|ConcurrentDictionary)\s*<', mask(expr).strip()))


def is_lambda(expr):
    mk = mask(expr).strip()
    return bool(re.match(r'(async\s+)?(\(?\s*[\w\s,]*\)?)\s*=>', mk)) or bool(re.match(r'(async\s+)?(static\s+)?delegate\b', mk))


def subject(expr):
    """The value as it goes into Assert.That(...)."""
    e = expr.strip()
    mk = mask(e).strip()
    if re.search(r'"u8$', mk):
        return e + '.ToArray()'
    if mk.startswith('[') and match_close(mk, 0) == len(mk) - 1:
        # A collection expression has no type of its own: `[a, b]` -> `new[] { a, b }`.
        inner = e[1:-1].strip()
        return f'new[] {{ {inner} }}' if inner else e
    return e


# --------------------------------------------------------------------------------------------------------------------
# Types (from a probe build, see --build): the compiler names the static type of each assertion's subject.

def bare_type(t):
    """'System.Collections.Generic.List<ErikTerm.Core.Row>' -> 'List<Row>': drops every qualifier."""
    return re.sub(r'\b[A-Za-z_]\w*\.(?=[A-Za-z_])', '', t.strip())


NUMERIC = ['sbyte', 'byte', 'short', 'ushort', 'char', 'int', 'uint', 'long', 'ulong', 'float', 'double', 'decimal']
# Implicit numeric conversions (C# spec 10.2.3): from -> the types it widens to.
WIDENS = {
    'sbyte': {'short', 'int', 'long', 'float', 'double', 'decimal'},
    'byte': {'short', 'ushort', 'int', 'uint', 'long', 'ulong', 'float', 'double', 'decimal'},
    'short': {'int', 'long', 'float', 'double', 'decimal'},
    'ushort': {'int', 'uint', 'long', 'ulong', 'float', 'double', 'decimal'},
    'char': {'ushort', 'int', 'uint', 'long', 'ulong', 'float', 'double', 'decimal'},
    'int': {'long', 'float', 'double', 'decimal'},
    'uint': {'long', 'ulong', 'float', 'double', 'decimal'},
    'long': {'float', 'double', 'decimal'},
    'ulong': {'float', 'double', 'decimal'},
    'float': {'double'},
}
CLR_NAMES = {'SByte': 'sbyte', 'Byte': 'byte', 'Int16': 'short', 'UInt16': 'ushort', 'Char': 'char', 'Int32': 'int',
             'UInt32': 'uint', 'Int64': 'long', 'UInt64': 'ulong', 'Single': 'float', 'Double': 'double',
             'Decimal': 'decimal', 'String': 'string', 'Object': 'object', 'Boolean': 'bool'}
COLLECTION_TYPES = re.compile(r'^(List|IList|IReadOnlyList|IEnumerable|ICollection|IReadOnlyCollection|HashSet|ISet|'
                              r'IReadOnlySet|SortedSet|ImmutableArray|ImmutableList|ImmutableHashSet|Queue|Stack|'
                              r'LinkedList|ArraySegment|IOrderedEnumerable|Collection|ReadOnlyCollection|IGrouping)<')
DICTIONARY_TYPES = re.compile(r'^(Dictionary|IDictionary|IReadOnlyDictionary|SortedDictionary|ConcurrentDictionary|'
                              r'ImmutableDictionary|FrozenDictionary|SortedList)<')
SPAN_TYPES = re.compile(r'^(ReadOnlySpan|Span|ReadOnlyMemory|Memory)<')


def type_kind(t):
    """'collection' / 'dictionary' / 'span' / 'string' / a numeric keyword / 'object' / 'other' / None (unknown)."""
    if not t:
        return None
    b = bare_type(t)
    b = CLR_NAMES.get(b, b)
    if b.endswith('?') and b[:-1] in NUMERIC:
        return 'other'  # Nullable<T>: compared as a value
    if b == 'string':
        return 'string'
    if b in NUMERIC or b == 'object':
        return b
    if SPAN_TYPES.match(b):
        return 'span'
    if b.endswith('[]') or b.endswith('[]?'):
        return 'collection'
    if DICTIONARY_TYPES.match(b):
        return 'dictionary'
    if COLLECTION_TYPES.match(b):
        return 'collection'
    return 'other'


def is_int_literal(expr):
    return bool(re.fullmatch(r'-?(0[xX][0-9a-fA-F_]+|0[bB][01_]+|[0-9][0-9_]*)', expr.strip()))


def float_literal(expr):
    """A tolerance literal as a float: '0.5' -> '0.5f'; None when it isn't a plain numeric literal."""
    e = expr.strip()
    if re.fullmatch(r'-?[0-9][0-9_]*(\.[0-9_]+)?([eE][-+]?[0-9]+)?[dD]?', e):
        return re.sub(r'[dD]$', '', e) + 'f'
    if re.fullmatch(r'-?[0-9][0-9_]*(\.[0-9_]+)?([eE][-+]?[0-9]+)?[fF]', e):
        return e
    return None


def cast_subject(that_text, type_name):
    """`await Assert.That(x)` -> `await Assert.That((T)x)`; a compound subject gets parentheses."""
    head = 'await Assert.That('
    inner = that_text[len(head):-1]
    simple = re.fullmatch(r'[A-Za-z_][\w.]*(\[[^\[\]]*\])?', mask(inner).strip())
    return f'{head}({type_name}){inner if simple else "(" + inner + ")"})'


def suffix_subject(that_text, suffix):
    """`await Assert.That(x)` -> `await Assert.That(x.ToCharArray())`; a compound subject gets parentheses."""
    head = 'await Assert.That('
    inner = that_text[len(head):-1]
    simple = re.fullmatch(r'[A-Za-z_][\w.]*(\([^()]*\)|\[[^\[\]]*\])*', mask(inner).strip())
    return f'{head}{inner if simple else "(" + inner + ")"}{suffix})'


def csharp_string(text):
    return '"' + text.replace('\\', '\\\\').replace('"', '\\"') + '"'


def assignable_local(s, pos, ident):
    """True if `ident` is a local declared earlier in the method around s[pos] (not a foreach/using/fixed variable)."""
    m = mask(s[:pos])
    starts = [mt.start() for mt in METHOD.finditer(m)]
    body = m[starts[-1]:] if starts else m[-4000:]
    x = re.escape(ident)
    if re.search(r'\b(foreach\s*\([^;]*?\s|using\s*\(?\s*(var|[\w.<>\[\]?]+)\s+|fixed\s*\([^;]*?\s)' + x + r'\b', body):
        return False
    not_types = {'return', 'await', 'else', 'yield', 'throw', 'case', 'goto', 'new', 'in', 'typeof', 'nameof', 'when',
                 'and', 'or', 'not', 'ref', 'out', 'params', 'this', 'base', 'static', 'const'}
    for mt in re.finditer(r'([\w.<>\[\]?,]*[\w>\]?])\s+' + x + r'\s*(=(?!=)|[,;)])', body):
        if mt.group(1) not in not_types:
            return True
    return False


def named(arg):
    mm = re.match(r'\s*([A-Za-z_]\w*)\s*:(?!:)\s*(.*)$', arg, re.S)
    if mm and mm.group(1) not in ('global',):
        return mm.group(1), mm.group(2).strip()
    return None, arg.strip()


class Converter:
    def __init__(self, text, types=None, probe=False):
        self.text = text
        self.todos = []
        self.unconverted = []
        self.types = types or {}  # probe id -> static type name, from a probe build
        self.probe = probe        # emit `(expr).__TypeProbe<id>` so a build reports each subject's type
        self.next_id = 0
        self.toplevel = None
        self.sync_spans = []

    def probed(self, expr, side=''):
        """Registers an expression for a type probe; returns (text to emit, known type or None)."""
        pid = f'{self.next_id}{side}'
        if self.probe:
            return f'({expr}).__TypeProbe{pid}', None
        return expr, self.types.get(pid)

    def that(self, expr, expected=None):
        """`await Assert.That(<subject>)`, with spans turned into arrays; also returns the subject's type kind."""
        self.next_id += 1
        sub = subject(expr)
        emitted, t = self.probed(sub)
        kind = type_kind(t)
        if kind == 'span':
            emitted = suffix_subject(f'await Assert.That({emitted})', '.ToArray()')[len('await Assert.That('):-1]
        return f'await Assert.That({emitted})', kind, t

    def expected(self, expr):
        """The expected value of an Equal (probed with the subject's id); returns (text, type kind)."""
        emitted, t = self.probed(subject(expr), 'E')
        kind = type_kind(t)
        if kind == 'span':
            emitted = suffix_subject(f'await Assert.That({emitted})', '.ToArray()')[len('await Assert.That('):-1]
        return emitted, kind

    # Converts every Assert.* call inside an expression/statement text (recursively for nested calls).
    def in_sync_helper(self, s, pos):
        return s is self.toplevel and any(a <= pos < b for a, b in self.sync_spans)

    def find_sync_helpers(self, text):
        """Bodies of non-test methods used as method groups (`xs.Select(Parse)`): they must stay synchronous."""
        self.toplevel = text
        m = mask(text)
        self.sync_spans = []
        for meth in find_methods(text):
            if is_test(meth):
                continue
            x = re.escape(meth.name)
            uses = [mt for mt in re.finditer(r'(?<![\w.])' + x + r'\b(?!\s*[(<\w])', m)
                    if mt.start() != meth.mt.start('name') and not m[:mt.start()].rstrip().endswith('nameof(')]
            if uses:
                self.sync_spans.append((meth.body_start, meth.body_end))

    def convert(self, s):
        out = []
        m = mask(s)
        i = 0
        for mt in re.finditer(r'(?<![\w.])Assert\s*\.\s*([A-Za-z_]\w*)', m):
            if mt.start() < i:
                continue
            k = mt.end()
            gen = ''
            if k < len(m) and m[k] == '<':
                g = generic_close(m, k)
                if g < 0:
                    continue
                gen = s[k + 1:g]
                k = g + 1
            while k < len(m) and m[k] in ' \t\r\n':
                k += 1
            if k >= len(m) or m[k] != '(':
                continue
            close = match_close(m, k)
            if close < 0:
                continue
            args = [self.convert(s[a:b]) for a, b in split_top(m, k + 1, close)]
            name = mt.group(1)
            before = s[i:mt.start()]
            awaited = bool(re.search(r'\bawait\s+$', before))
            end = close + 1
            stmt = self._statement_context(m, mt.start(), end)
            rep = self.assertion(name, gen.strip(), args, s, mt.start(), end, stmt)
            if rep is None:
                out.append(s[i:end])
                i = end
                continue
            if isinstance(rep, tuple):  # statement replacement that also eats the ';'
                rep, end = rep
            elif rep.startswith('await '):
                # The awaited value used further: `Assert.Single(x).Name` -> `(await ...HasSingleItem()).Name`.
                # Throws* results are nullable in TUnit (the exception or null): `!` where the value is used.
                after = m[end:].lstrip()
                member = bool(re.match(r'(\.|\[|\?\.|\?\[|!)', after))
                assigned = bool(re.search(r'(?<![=!<>])=\s*(await\s+)?$', mask(before)))
                throws = name.startswith('Throws')
                if member or (throws and assigned):
                    if awaited:
                        before = re.sub(r'\bawait\s+$', '', before)
                    rep = f'({rep}){"!" if throws else ""}'
                elif awaited:
                    rep = rep[len('await '):]
            out.append(before)
            out.append(rep)
            i = end
        out.append(s[i:])
        return ''.join(out)

    @staticmethod
    def _statement_context(m, start, end):
        k = start - 1
        while k >= 0 and m[k] in ' \t\r\n':
            k -= 1
        prev_ok = k < 0 or m[k] in ';{}'
        j = end
        while j < len(m) and m[j] in ' \t\r\n':
            j += 1
        return prev_ok and j < len(m) and m[j] == ';'

    def todo(self, what):
        self.todos.append(what)
        return f'/* TODO(TUnit): {what} */ '

    def assertion(self, name, gen, args, s, start, end, stmt):
        a = [x.strip() for x in args]
        n = len(a)
        that = lambda x: self.that(x)[0]

        if name in ('Equal', 'StrictEqual', 'NotEqual', 'NotStrictEqual') and n >= 2:
            neg = name.startswith('Not')
            e, act = a[0], a[1]
            tolerance = []
            extra = ''
            for x in a[2:]:
                key, val = named(x)
                if key in (None, 'precision') and re.fullmatch(r'\d+', val):
                    p = int(val)
                    tolerance.append(f'{0.5 * 10 ** -p:.{p + 1}f}')
                elif key == 'tolerance' or (key is None and float_literal(val)):
                    tolerance.append(val)  # Equal(double expected, double actual, double tolerance)
                elif key == 'ignoreCase' and val == 'true':
                    extra += '.IgnoringCase()'
                elif re.search(r'StringComparer\.\w*IgnoreCase', val):
                    extra += '.IgnoringCase()'
                else:
                    return self.todo(f'Assert.{name} with {x.strip()}') + s[start:end]
            if e == 'null':
                return f'{that(act)}.{"IsNotNull" if neg else "IsNull"}()'
            if e in ('true', 'false'):
                sub_text, _, atype = self.that(act)
                if atype is not None and bare_type(atype) not in ('bool', 'Boolean'):
                    sub_text = cast_subject(sub_text, 'bool')  # Equal<object>(true, x)
                return f'{sub_text}.Is{"True" if (e == "true") != neg else "False"}()'
            if not neg and mask(e).strip() == '[]':
                return f'{that(act)}.IsEmpty()'
            sub_text, akind, atype = self.that(act)
            exp_text, ekind = self.expected(e)
            if akind in ('collection', 'span', 'dictionary', 'string', 'other') or akind in NUMERIC:
                collection = akind in ('collection', 'span')
                dictionary = akind == 'dictionary'
            elif akind == 'object' and (looks_like_collection(e) or ekind in ('collection', 'span')):
                # Equal<object>: xUnit compared the sequences; TUnit needs the subject as a sequence.
                sub_text = cast_subject(sub_text, 'IEnumerable<object>')
                collection, dictionary = True, False
            else:  # no type information: guess from the text
                dictionary = is_dictionary(e)
                collection = not dictionary and (looks_like_collection(e) or looks_like_collection(act))
            if dictionary:
                return f'{sub_text}.{"IsNotEquivalentTo" if neg else "IsEquivalentTo"}({exp_text}){extra}'
            if collection:
                return f'{sub_text}.{"IsNotEquivalentTo" if neg else "IsEquivalentTo"}({exp_text}, CollectionOrdering.Matching){extra}'
            if akind in NUMERIC and ekind in NUMERIC and akind != ekind:
                widen_actual = ekind in WIDENS.get(akind, ())
                constant_fits = is_int_literal(e) and akind not in ('char', 'float', 'double', 'decimal')
                if widen_actual and not constant_fits:
                    # Equal<T> picked the wider type; Assert.That(x) is typed by the subject: widen it to match.
                    sub_text = cast_subject(sub_text, ekind)
                    akind = ekind
            within = ''
            float_expected = akind is None and re.search(r'\d[fF]$', mask(e).strip())
            if tolerance and akind != 'float' and not float_expected and akind in (None, *NUMERIC) and akind != 'double':
                # Equal(double, double, tolerance/precision): xUnit compared as doubles (an int W widens).
                sub_text = cast_subject(sub_text, 'double')
            for tol in tolerance:
                if akind == 'float' or float_expected:
                    tol = float_literal(tol) or f'(float)({tol})'
                within += f'.Within({tol})'
            return f'{sub_text}.{"IsNotEqualTo" if neg else "IsEqualTo"}({exp_text}){within}{extra}'
        if name == 'Equivalent' and n >= 2:
            return f'{that(a[1])}.IsEquivalentTo({a[0]})'
        if name in ('True', 'False', 'Null', 'NotNull') and n in (1, 2) and stmt and self.in_sync_helper(s, start):
            # A helper used as a method group (`xs.Select(Parse)`) can't become async: fail synchronously.
            cond = {'True': f'!({a[0]})', 'False': a[0], 'Null': f'{a[0]} is not null', 'NotNull': f'{a[0]} is null'}[name]
            msg = named(a[1])[1] if n == 2 else csharp_string(f'Assert.{name} failed: {a[0]}')
            return (f'if ({cond}) Assert.Fail({msg});', self._end_with_semicolon(s, end))
        if name in ('True', 'False') and n in (1, 2):
            r = f'{that(a[0])}.Is{name}()'
            if n == 2:
                r += f'.Because({named(a[1])[1]})'
            return r
        if name in ('Null', 'NotNull') and n == 1:
            if name == 'NotNull' and stmt and re.fullmatch(r'[A-Za-z_]\w*', a[0]) and assignable_local(s, start, a[0]):
                # xUnit's NotNull tells the compiler the local isn't null; TUnit's returns the non-null value.
                return f'{a[0]} = {that(a[0])}.IsNotNull()'
            return f'{that(a[0])}.Is{name}()'
        if name in ('Same', 'NotSame') and n == 2:
            return f'{that(a[1])}.Is{"Not" if name == "NotSame" else ""}SameReferenceAs({a[0]})'
        if name in ('Empty', 'NotEmpty') and n == 1:
            return f'{that(a[0])}.Is{name}()'
        if name == 'Single' and n == 1:
            return f'{that(a[0])}.HasSingleItem()'
        if name == 'Single' and n == 2 and is_lambda(a[1]):
            return f'{that(a[0])}.HasSingleItem({a[1]})'
        if name in ('Contains', 'DoesNotContain') and n >= 2:
            if is_lambda(a[1]) and n == 2:
                sub_text, akind, _ = self.that(a[0])
                if akind == 'string':  # Contains(string, Predicate<char>): TUnit's string assertions take text
                    sub_text = suffix_subject(sub_text, '.ToCharArray()')
                return f'{sub_text}.{name}({a[1]})'
            if n == 2:
                sub_text, akind, _ = self.that(a[1])
                if akind == 'dictionary':  # Contains(key, dictionary)
                    return f'{sub_text}.{"ContainsKey" if name == "Contains" else "DoesNotContainKey"}({subject(a[0])})'
                return f'{sub_text}.{name}({subject(a[0])})'
            if n == 3 and 'StringComparison.' in a[2]:
                return f'{that(a[1])}.{name}({a[0]}, {a[2]})'
            return self.todo(f'Assert.{name} with a comparer') + s[start:end]
        if name in ('StartsWith', 'EndsWith', 'DoesNotStartWith', 'DoesNotEndWith') and n in (2, 3):
            cmp = f', {a[2]}' if n == 3 else ''
            return f'{that(a[1])}.{name}({a[0]}{cmp})'
        if name in ('Matches', 'DoesNotMatch') and n == 2:
            return f'{that(a[1])}.{name}({a[0]})'
        if name == 'InRange' and n == 3:
            return f'{that(a[0])}.IsBetween({a[1]}, {a[2]})'
        if name in ('IsType', 'IsNotType', 'IsAssignableFrom') and n == 1 and gen:
            method = {'IsType': 'IsTypeOf', 'IsNotType': 'IsNotTypeOf', 'IsAssignableFrom': 'IsAssignableTo'}[name]
            return f'{that(a[0])}.{method}<{gen}>()'
        if name in ('Throws', 'ThrowsAsync', 'ThrowsAny', 'ThrowsAnyAsync') and n >= 1:
            t = gen
            rest = a
            if not t and n == 2:
                mm = re.fullmatch(r'typeof\((.+)\)', a[0])
                if mm:
                    t, rest = mm.group(1), a[1:]
            if not t:
                return self.todo(f'Assert.{name} without a type argument') + s[start:end]
            method = 'Throws' if 'Any' in name else 'ThrowsExactly'
            if len(rest) == 2:  # (paramName, action)
                return f'{that(rest[1])}.{method}<{t}>().WithParameterName({rest[0]})'
            if len(rest) == 1:
                return f'{that(rest[0])}.{method}<{t}>()'
        if name == 'All' and n == 2:
            if stmt:
                r = self._foreach(a[0], a[1], s, start, end)
                if r:
                    return r
            return self.todo('Assert.All outside a statement: use `Assert.That(c).All(predicate)`') + s[start:end]
        if name == 'Collection' and n >= 1 and stmt:
            r = self._collection(a[0], a[1:], s, start, end)
            if r:
                return r
        if name == 'Fail':
            return None  # TUnit has Assert.Fail(string)
        if name == 'Skip' and n == 1:
            return f'Skip.Test({a[0]})'
        if name in ('SkipWhen', 'SkipUnless') and n == 2:
            return f'Skip.{name[4:]}({a[0]}, {a[1]})'
        self.unconverted.append(f'Assert.{name}')
        return self.todo(f'convert Assert.{name}') + s[start:end]

    def _end_with_semicolon(self, s, end):
        m = mask(s)
        j = end
        while j < len(m) and m[j] in ' \t\r\n':
            j += 1
        return j + 1  # past the ';'

    def _foreach(self, coll, fn, s, start, end):
        indent = line_indent(s, start)
        mm = re.match(r'\s*\(?\s*([A-Za-z_]\w*)\s*\)?\s*=>\s*(.*)$', fn, re.S)
        stop = self._end_with_semicolon(s, end)
        if mm:
            var, body = mm.group(1), mm.group(2).strip()
            if body.startswith('{'):
                inner = body[1:body.rfind('}')]
                return (f'foreach (var {var} in {coll})\n{indent}{{{inner}}}', stop)
            return (f'foreach (var {var} in {coll})\n{indent}    {body};', stop)
        if re.fullmatch(r'[A-Za-z_][\w.]*', fn.strip()):  # method group
            return (f'foreach (var item in {coll})\n{indent}    {fn.strip()}(item);', stop)
        return None

    def _collection(self, coll, inspectors, s, start, end):
        indent = line_indent(s, start)
        stop = self._end_with_semicolon(s, end)
        lines = [f'await Assert.That({coll}).Count().IsEqualTo({len(inspectors)});']
        for idx, fn in enumerate(inspectors):
            mm = re.match(r'\s*\(?\s*([A-Za-z_]\w*)\s*\)?\s*=>\s*(.*)$', fn, re.S)
            if not mm or mm.group(2).strip().startswith('{'):
                return None
            lines.append(f'{{ var {mm.group(1)} = {coll}.ElementAt({idx}); {mm.group(2).strip()}; }}')
        return (('\n' + indent).join(lines), stop)


# --------------------------------------------------------------------------------------------------------------------
# Attributes

OS_ATTRS = {'LinuxFact': 'OS.Linux', 'WindowsFact': 'OS.Windows', 'MacFact': 'OS.MacOs', 'MacOSFact': 'OS.MacOs',
            'LinuxTheory': 'OS.Linux', 'WindowsTheory': 'OS.Windows', 'MacTheory': 'OS.MacOs'}


def convert_attribute(attr, conv):
    """One attribute (without brackets) -> list of TUnit attributes."""
    mm = re.match(r'\s*([\w.]+?)(?:Attribute)?\s*(\((.*)\))?\s*$', attr, re.S)
    if not mm:
        return [attr.strip()]
    name, argtext = mm.group(1).split('.')[-1], mm.group(3)
    args = []
    if argtext is not None:
        m = mask(argtext)
        args = [argtext[a:b].strip() for a, b in split_top(m, 0, len(argtext))]
    if name in ('Fact', 'Theory') or name in OS_ATTRS:
        out = ['Test']
        if name in OS_ATTRS:
            out.append(f'RunOn({OS_ATTRS[name]})')
        for x in args:
            kv = re.match(r'(\w+)\s*=\s*(.*)$', x, re.S)
            if not kv:
                continue
            key, val = kv.group(1), kv.group(2).strip()
            if key == 'Skip':
                out.append(f'Skip({val})')
            elif key == 'DisplayName':
                out.append(f'DisplayName({val})')
            elif key == 'Timeout':
                out.append(f'Timeout({val})')
            elif key == 'Explicit' and val == 'true':
                out.append('Explicit')
            elif key in ('SkipWhen', 'SkipUnless', 'SkipType'):
                conv.todos.append(f'{name}({x})')
                out.append(f'/* TODO(TUnit): {x} -> Skip.When/Unless in the body */')
        return out
    if name == 'InlineData':
        return [f'Arguments({argtext or ""})']
    if name == 'MemberData':
        member = args[0] if args else ''
        member_type = None
        rest = []
        for x in args[1:]:
            kv = re.match(r'(\w+)\s*=\s*(.*)$', x, re.S)
            if kv and kv.group(1) == 'MemberType':
                member_type = kv.group(2).strip()
            elif kv and kv.group(1) in ('DisableDiscoveryEnumeration', 'Skip'):
                continue
            else:
                rest.append(x)
        parts = ([member_type] if member_type else []) + [member]
        if rest:
            parts.append(f'Arguments = [{", ".join(rest)}]')
        return [f'MethodDataSource({", ".join(parts)})']
    if name == 'ClassData':
        conv.todos.append('ClassData')
        return [f'/* TODO(TUnit): ClassData -> MethodDataSource or ClassDataSource */ ClassData({argtext})']
    if name == 'Trait':
        return [f'Property({argtext})']
    if name == 'Collection':
        return [f'NotInParallel({args[0] if args else ""})']
    return [attr.strip()]


def convert_attributes(text, conv):
    m = mask(text)
    out = []
    i = 0
    for mt in re.finditer(r'(?m)^[ \t]*\[', m):
        k = mt.end() - 1
        if k < i:
            continue
        close = match_close(m, k)
        if close < 0:
            continue
        inner = text[k + 1:close]
        im = mask(inner)
        if re.match(r'\s*(assembly|module|return|param|field|property|type|method)\s*:', im):
            continue
        attrs = [inner[a:b] for a, b in split_top(im, 0, len(inner))]
        if not any(re.match(r'\s*[\w.]*(Fact|Theory|InlineData|MemberData|ClassData|Trait|Collection)\b', mask(x)) for x in attrs):
            continue
        new = []
        for x in attrs:
            new.extend(convert_attribute(x, conv))
        out.append(text[i:k + 1])
        out.append(', '.join(new))
        i = close
    out.append(text[i:])
    return ''.join(out)


# --------------------------------------------------------------------------------------------------------------------
# Methods

MODIFIERS = r'(?:(?:public|private|protected|internal|static|unsafe|async|override|virtual|sealed|new|extern)\s+)*'
METHOD = re.compile(r'(?m)^(?P<indent>[ \t]*)(?P<mods>' + MODIFIERS + r')(?P<ret>(?:[\w.]+(?:<[^;{}=()]*?>)?(?:\[\])*\??|\([^;{}=]*?\)))\s+(?P<name>[A-Za-z_]\w*)\s*(?:<[\w\s,]*>)?\s*\(')
KEYWORDS = {'return', 'new', 'else', 'await', 'var', 'throw', 'using', 'case', 'goto', 'yield', 'is', 'as', 'in',
            'out', 'ref', 'await', 'typeof', 'nameof', 'when', 'and', 'or', 'not', 'if', 'while', 'for', 'foreach',
            'switch', 'lock', 'catch', 'class', 'struct', 'record', 'interface', 'enum', 'namespace', 'delegate',
            'event', 'operator', 'implicit', 'explicit', 'const', 'readonly', 'fixed'}


class Method:
    def __init__(self, text, m, mt):
        self.mt = mt
        self.name = mt.group('name')
        self.ret = mt.group('ret')
        self.mods = mt.group('mods')
        self.start = mt.start()
        pstart = mt.end() - 1
        self.pstart, self.pend = pstart, match_close(m, pstart)
        j = self.pend + 1
        while j < len(m) and m[j] in ' \t\r\n':
            j += 1
        # skip generic constraints
        while m.startswith('where', j):
            nxt = min([x for x in (m.find('{', j), m.find('=>', j)) if x >= 0] or [len(m)])
            j = nxt
        self.kind = None
        if m.startswith('=>', j):
            self.kind = 'expr'
            self.body_start = j
            depth = 0
            k = j
            while k < len(m):
                c = m[k]
                if c in '([{':
                    depth += 1
                elif c in ')]}':
                    depth -= 1
                elif c == ';' and depth == 0:
                    break
                k += 1
            self.body_end = k + 1
        elif j < len(m) and m[j] == '{':
            self.kind = 'block'
            self.body_start = j
            self.body_end = match_close(m, j) + 1
        self.attrs = attributes_before(text, m, self.start)


def attributes_before(text, m, pos):
    """The attribute text directly above a declaration."""
    k = pos
    attrs = []
    while True:
        j = k - 1
        while j >= 0 and m[j] in ' \t\r\n':
            j -= 1
        if j < 0 or m[j] != ']':
            break
        depth = 0
        a = j
        while a >= 0:
            if m[a] == ']':
                depth += 1
            elif m[a] == '[':
                depth -= 1
                if depth == 0:
                    break
            a -= 1
        if a < 0:
            break
        attrs.append(text[a + 1:j])
        k = a
    return attrs


def find_methods(text):
    m = mask(text)
    found = []
    for mt in METHOD.finditer(m):
        if mt.group('ret') in KEYWORDS or mt.group('name') in KEYWORDS:
            continue
        meth = Method(text, m, mt)
        if meth.pend < 0 or meth.kind is None or meth.body_end <= 0:
            continue
        found.append(meth)
    return found


def is_test(meth):
    return any(re.match(r'\s*(Test|Arguments|MethodDataSource)\b', mask(a)) for a in meth.attrs) or \
        any(re.search(r'(^|[\s,])Test\s*(,|$|\()', mask(a)) for a in meth.attrs)


def make_async(text, conv):
    """Methods whose bodies await become async; returns (text, names of non-test methods made async)."""
    made = set()
    for _ in range(5):
        changed = False
        for meth in sorted(find_methods(text), key=lambda x: -x.start):
            body = mask(text[meth.body_start:meth.body_end])
            if not re.search(r'\bawait\b', body) or re.search(r'\basync\b', meth.mods):
                continue
            ret = meth.ret
            if ret == 'void':
                new_ret = 'Task'
            elif ret in ('Task', 'ValueTask') or ret.startswith(('Task<', 'ValueTask<')):
                new_ret = ret
            else:
                new_ret = f'Task<{ret}>'
            mods = meth.mods
            prefix = ''
            if 'unsafe' in mods:
                conv.todos.append(f'{meth.name}: await in an unsafe method')
                prefix = '// TODO(TUnit): await is not allowed in an unsafe method — move the pointer code into a sync helper\n' + meth.mt.group('indent')
            a = meth.mt.start('mods')
            b = meth.mt.end('ret')
            text = text[:a] + prefix + mods + 'async ' + new_ret + text[b:]
            if not is_test(meth):
                made.add(meth.name)
            changed = True
        if made:
            text, added = add_awaits(text, made)
            changed = changed or added
        if not changed:
            break
    return text, made


def add_awaits(text, names):
    m = mask(text)
    decls = {meth.mt.start('name') for meth in find_methods(text)}
    out = []
    i = 0
    added = False
    for mt in re.finditer(r'(?<![\w.])(' + '|'.join(map(re.escape, names)) + r')\s*\(', m):
        if mt.start() in decls or mt.start() < i:
            continue
        before = m[:mt.start()].rstrip()
        if before.endswith('await') or before.endswith('nameof('):
            continue
        out.append(text[i:mt.start()])
        close = match_close(m, mt.end() - 1)
        after = m[close + 1:].lstrip() if close > 0 else ''
        if close > 0 and re.match(r'(\.|\[|\?\.|\?\[|!)', after):
            # `await` binds looser than member access: `(await Helper(x)).Name`.
            out.append('(await ' + text[mt.start():close + 1] + ')')
            i = close + 1
        else:
            out.append('await ')
            i = mt.start()
        added = True
    out.append(text[i:])
    return ''.join(out), added


# --------------------------------------------------------------------------------------------------------------------
# AAA

BLOCK_KEYWORDS = ('if', 'else', 'for', 'foreach', 'while', 'do', 'using', 'lock', 'try', 'catch', 'finally', 'switch',
                  'fixed', 'unsafe', 'checked', 'unchecked')
LOCAL_FUNC = re.compile(r'^(?:(?:static|async|unsafe)\s+)*[\w.<>\[\],?() ]+\s+[A-Za-z_]\w*\s*(?:<[^>]*>)?\s*\([^;]*\)\s*$', re.S)


def statements(m, start, end):
    """Top-level statement spans inside a block body m[start:end] (between the braces)."""
    spans = []
    k = start
    n = end
    while k < n:
        while k < n and m[k] in ' \t\r\n':
            k += 1
        if k >= n:
            break
        s0 = k
        word = re.match(r'[A-Za-z_]\w*', m[k:k + 20])
        first = word.group(0) if word else m[k]
        block_stmt = first in BLOCK_KEYWORDS or first == '{'
        depth = 0
        while k < n:
            c = m[k]
            if c in '([{':
                if c == '{' and depth == 0 and not block_stmt and LOCAL_FUNC.match(m[s0:k].strip() or 'x'):
                    block_stmt = True
                depth += 1
            elif c in ')]}':
                depth -= 1
                if depth == 0 and c == '}' and block_stmt:
                    j = k + 1
                    while j < n and m[j] in ' \t\r\n':
                        j += 1
                    if not re.match(r'(else|catch|finally|while)\b', m[j:j + 8]) and (j >= n or m[j] != ';'):
                        k += 1
                        break
            elif c == ';' and depth == 0:
                k += 1
                break
            k += 1
        spans.append((s0, k))
    return spans


def classify(m, a, b):
    st = m[a:b].strip()
    if re.match(r'(var\s+\w+\s*=\s*|[\w<>\[\]?,. ]+\s+\w+\s*=\s*|\w+\s*=\s*)?\(?await\s+Assert\s*\.', st) or re.match(r'Assert\s*\.', st):
        return 'assert'
    if re.match(r'Skip\s*\.', st):
        return 'skip'
    first = re.match(r'[A-Za-z_]\w*', st)
    if first and first.group(0) in BLOCK_KEYWORDS and 'Assert' in st:
        brace = st.find('{')
        if brace < 0:  # single embedded statement
            inner = re.sub(r'^\w+\s*(\([^{]*?\))?\s*', '', st, count=1)
            return 'assert' if re.match(r'(await\s+)?Assert\s*\.', inner.strip()) else 'mixed'
        close = match_close(st, brace)
        kinds = {classify(st, x, y) for x, y in statements(st, brace + 1, close)}
        if kinds <= {'assert'}:
            return 'assert'
        return 'mixed'
    if 'Assert.That(' in st:
        return 'mixed'
    return 'code'


def assert_subject(stmt_text):
    """For `await Assert.That(<subject>).X(...);` returns (subject_start, subject_end) in the statement text."""
    m = mask(stmt_text)
    mt = re.search(r'Assert\s*\.\s*That\s*\(', m)
    if not mt:
        return None
    close = match_close(m, mt.end() - 1)
    return (mt.end(), close) if close > 0 else None


def aaa(text, conv):
    for meth in sorted(find_methods(text), key=lambda x: -x.start):
        if not is_test(meth):
            continue
        body = text[meth.body_start:meth.body_end]
        if re.search(r'//\s*(Arrange|Act|Assert)\b', body) or 'TODO(AAA)' in body:
            continue
        indent = meth.mt.group('indent')
        inner_indent = indent + '    '
        if meth.kind == 'expr':
            expr = body[2:].strip()
            if expr.endswith(';'):
                expr = expr[:-1].rstrip()
            new_body = f'\n{indent}{{\n{inner_indent}{expr};\n{indent}}}'
            # keep the space before '=>' tidy
            head = text[:meth.body_start].rstrip()
            text = head + new_body + text[meth.body_end:]
            body = new_body.lstrip('\n').lstrip()
            meth_body_start = len(head) + 1 + len(indent)
        else:
            meth_body_start = meth.body_start
        text = aaa_block(text, meth_body_start, inner_indent, conv)
    return text


def aaa_block(text, body_start, inner_indent, conv):
    m = mask(text)
    body_end = match_close(m, body_start)
    spans = statements(m, body_start + 1, body_end)
    if not spans:
        return text
    kinds = [classify(m, a, b) for a, b in spans]
    sig = ''.join({'assert': 'A', 'code': 'C', 'mixed': 'M', 'skip': 'S'}[k] for k in kinds)

    def line_start(pos):
        return text.rfind('\n', 0, pos) + 1

    def lead_start(idx):
        """Start of the line where statement idx's leading comments begin."""
        prev_end = spans[idx - 1][1] if idx > 0 else body_start + 1
        j = prev_end
        while j < spans[idx][0] and text[j] in ' \t\r\n':
            j += 1
        nl = text.find('\n', prev_end)
        if nl >= 0 and nl < j:
            return line_start(j)
        return line_start(spans[idx][0]) if line_start(spans[idx][0]) > prev_end else spans[idx][0]

    inserts = []  # (pos, text)

    def marker(idx, name, blank=True):
        pos = lead_start(idx)
        pre = '\n' if blank and idx > 0 and not text[:pos].endswith('\n\n') else ''
        inserts.append((pos, f'{pre}{inner_indent}// {name}\n'))

    core = sig.lstrip('S')
    skip_n = len(sig) - len(core)
    if re.fullmatch(r'C*A+', core) and 'M' not in core:
        n_code = core.count('C')
        first_code = skip_n
        first_assert = skip_n + n_code
        if n_code >= 2:
            marker(first_code, 'Arrange', blank=False)
            marker(first_assert - 1, 'Act')
            marker(first_assert, 'Assert')
        elif n_code == 1:
            marker(first_code, 'Act', blank=skip_n > 0)
            marker(first_assert, 'Assert')
        else:
            asserts = spans[first_assert:]
            if len(asserts) == 1:
                hoisted = hoist(text, asserts[0], inner_indent)
                if hoisted is not None:
                    a, b = asserts[0]
                    pos = lead_start(first_assert)
                    pre = text[pos:a]
                    return text[:pos] + hoisted + text[b:]
            subjects = []
            for a, b in asserts:
                sub = assert_subject(text[a:b])
                subjects.append(text[a:b][sub[0]:sub[1]] if sub else '')
            if any('(' in mask(x) or '=>' in x for x in subjects):
                conv.todos.append('AAA: every assertion does its own act')
                inserts.append((lead_start(first_assert), f'{inner_indent}// TODO(AAA): each assertion runs its own act — use [Arguments] rows or separate tests\n'))
            else:
                marker(first_assert, 'Assert', blank=False)
    else:
        conv.todos.append('AAA: acts and asserts are interleaved')
        inserts.append((lead_start(0), f'{inner_indent}// TODO(AAA): acts and asserts are interleaved — split into separate tests (or numbered steps for a walk-through)\n'))
    for pos, ins in sorted(inserts, key=lambda x: -x[0]):
        text = text[:pos] + ins + text[pos:]
    return text


def hoist(text, span, indent):
    """`await Assert.That(<act>).X(..);` -> `// Act` local + `// Assert`; also for Throws with a lambda subject."""
    a, b = span
    st = text[a:b]
    sub = assert_subject(st)
    if not sub:
        return None
    expr = st[sub[0]:sub[1]].strip()
    mk = mask(expr)
    if not ('(' in mk or '=>' in mk):
        return None
    if re.search(r'\bawait\b', mk) and '=>' not in mk:
        return None
    local = 'act' if is_lambda(expr) else 'actual'
    if expr.endswith('.ToArray()') and re.search(r'"u8\.ToArray\(\)$', mk):
        return None
    new_assert = st[:sub[0]] + local + st[sub[1]:]
    return f'{indent}// Act\n{indent}var {local} = {expr};\n\n{indent}// Assert\n{indent}{new_assert}'


# --------------------------------------------------------------------------------------------------------------------
# Classes, data, usings

def convert_classes(text, conv):
    m = mask(text)
    # Custom Fact/Theory attribute classes (e.g. LinuxFactAttribute): TUnit has [RunOn]/[ExcludeOn] instead.
    for mt in reversed(list(re.finditer(r'(?m)^[ \t]*(?:public |internal )?(?:sealed )?class (\w+)Attribute\s*:\s*(?:Xunit\.)?(Fact|Theory)Attribute\b[^{]*', m))):
        brace = m.find('{', mt.end() - 1)
        close = match_close(m, brace)
        start = mt.start()
        # include the doc comment above
        ls = text.rfind('\n', 0, start)
        while ls > 0:
            prev = text.rfind('\n', 0, ls)
            if text[prev + 1:ls].strip().startswith('///'):
                ls = prev
            else:
                break
        end = text.find('\n', close)
        repl = f'// {mt.group(1)}Attribute: TUnit uses [Test] + [RunOn(OS.X)] / [ExcludeOn(OS.X)] instead.\n'
        text = text[:ls + 1] + repl + text[end + 1:]
        m = mask(text)
    # IDisposable / IAsyncLifetime / IClassFixture<T> on test classes.
    out = text
    for mt in reversed(list(re.finditer(r'(?m)^([ \t]*)((?:public |internal |sealed |static |partial |abstract )*class\s+\w+[^:{\n]*)\s*:\s*([^{\n]+?)\s*(\{|\n)', mask(out)))):
        bases = [x.strip() for x in split_top(mask(mt.group(3)), 0, len(mt.group(3))) and
                 [out[mt.start(3) + a:mt.start(3) + b] for a, b in split_top(mask(mt.group(3)), 0, len(mt.group(3)))]]
        keep, attrs = [], []
        disposable = async_life = False
        for b in bases:
            if b in ('IDisposable', 'System.IDisposable'):
                disposable = True
            elif b in ('IAsyncDisposable', 'System.IAsyncDisposable'):
                disposable = True
            elif b in ('IAsyncLifetime', 'Xunit.IAsyncLifetime'):
                async_life = True
            elif re.match(r'(Xunit\.)?IClassFixture<(.+)>$', b):
                attrs.append(f'ClassDataSource<{re.match(r"(?:Xunit[.])?IClassFixture<(.+)>$", b).group(1)}>(Shared = SharedType.PerClass)')
            elif re.match(r'(Xunit\.)?ICollectionFixture<', b):
                conv.todos.append('ICollectionFixture')
                attrs.append(f'/* TODO(TUnit): {b} -> ClassDataSource<T>(Shared = SharedType.Keyed, Key = "...") */')
            else:
                keep.append(b)
        if not (disposable or async_life or attrs):
            continue
        cls_start, cls_brace = mt.start(), mask(out).find('{', mt.start())
        cls_end = match_close(mask(out), cls_brace)
        body = out[cls_brace:cls_end]
        ind = mt.group(1) + '    '
        if disposable:
            body = re.sub(r'(?m)^([ \t]*)public (void Dispose\(\)|async ValueTask DisposeAsync\(\)|ValueTask DisposeAsync\(\))',
                          lambda x: f'{x.group(1)}[After(Test)]\n{x.group(1)}public {x.group(2).replace("Dispose()", "Cleanup()").replace("DisposeAsync()", "CleanupAsync()")}', body)
        if async_life:
            body = re.sub(r'(?m)^([ \t]*)public (async )?(ValueTask|Task) InitializeAsync\(\)',
                          lambda x: f'{x.group(1)}[Before(Test)]\n{x.group(1)}public {x.group(2) or ""}{x.group(3)} InitializeAsync()', body)
            if not disposable:
                body = re.sub(r'(?m)^([ \t]*)public (async )?(ValueTask|Task) DisposeAsync\(\)',
                              lambda x: f'{x.group(1)}[After(Test)]\n{x.group(1)}public {x.group(2) or ""}{x.group(3)} CleanupAsync()', body)
        header = mt.group(2).rstrip() + (' : ' + ', '.join(keep) if keep else '')
        attr_text = ''.join(f'{mt.group(1)}[{a}]\n' for a in attrs)
        out = out[:cls_start] + attr_text + mt.group(1) + header + '\n' + mt.group(1) + body + out[cls_end:]
    return out


def convert_theory_data(text, conv):
    m = mask(text)
    # public static TheoryData<A, B> Name => new() { { a, b }, ... };   (or = new TheoryData<A, B> { ... };)
    for mt in reversed(list(re.finditer(r'(?m)^([ \t]*)((?:public |internal |private |protected )?static )(?:readonly )?TheoryData<', m))):
        lt = mt.end() - 1
        gt = generic_close(m, lt) if generic_close(m, lt) > 0 else m.find('>', lt)
        types = [text[lt + 1:gt][a:b].strip() for a, b in split_top(mask(text[lt + 1:gt]), 0, gt - lt - 1)]
        elem = types[0] if len(types) == 1 else '(' + ', '.join(types) + ')'
        rest = m[gt + 1:]
        nm = re.match(r'\s*([A-Za-z_]\w*)\s*(\(\s*\))?\s*(=>|=|\{)', rest)
        if not nm:
            continue
        name = nm.group(1)
        after = gt + 1 + nm.end()
        if nm.group(2) or nm.group(3) == '{':  # a method: new TheoryData<..>() + .Add(..) calls
            brace = m.find('{', gt + 1 + nm.start(3)) if nm.group(3) != '=>' else -1
            if brace < 0:
                continue
            close = match_close(m, brace)
            body = text[brace:close + 1]
            body = re.sub(r'new\s+TheoryData<[^>]*>\s*\(\s*\)', f'new List<{elem}>()', body)
            body = re.sub(r'\bnew\s*\(\s*\)', f'new List<{elem}>()', body)
            body = re.sub(r'\bvar (\w+) = new List', r'var \1 = new List', body)
            if len(types) > 1:
                bm = mask(body)
                pieces, last = [], 0
                for am in re.finditer(r'\.Add\s*\(', bm):
                    o = am.end() - 1
                    c = match_close(bm, o)
                    if len(split_top(bm, o + 1, c)) > 1:
                        pieces.append(body[last:o + 1] + '(' + body[o + 1:c] + ')')
                        last = c
                pieces.append(body[last:])
                body = ''.join(pieces)
            head = f'{mt.group(1)}{mt.group(2)}IEnumerable<{elem}> {name}()\n{mt.group(1)}'
            text = text[:mt.start()] + head + body + text[close + 1:]
        else:  # property / field with a collection initializer
            brace = m.find('{', after)
            close = match_close(m, brace)
            entries = []
            bm = m[brace + 1:close]
            for a, b in split_top(bm, 0, len(bm)):
                e = text[brace + 1 + a:brace + 1 + b].strip()
                em = mask(e)
                if em.startswith('{') and em.endswith('}'):
                    e = '(' + e[1:-1].strip() + ')'
                if e:
                    entries.append(e)
            ind = mt.group(1)
            semi = m.find(';', close)
            items = (',\n' + ind + '    ').join(entries)
            new = f'{ind}{mt.group(2)}IEnumerable<{elem}> {name}() =>\n{ind}[\n{ind}    {items},\n{ind}];'
            text = text[:mt.start()] + new + text[semi + 1:]
        m = mask(text)
    return text


def convert_cancellation(text):
    if 'TestContext.Current.CancellationToken' not in text:
        return text
    for meth in sorted(find_methods(text), key=lambda x: -x.start):
        body = text[meth.body_start:meth.body_end]
        if 'TestContext.Current.CancellationToken' not in body:
            continue
        body = body.replace('TestContext.Current.CancellationToken', 'cancellationToken')
        params = text[meth.pstart + 1:meth.pend].strip()
        new_params = f'{params}, CancellationToken cancellationToken' if params else 'CancellationToken cancellationToken'
        text = text[:meth.pstart + 1] + new_params + text[meth.pend:meth.body_start] + body + text[meth.body_end:]
    return text


def convert_usings(text):
    text = re.sub(r'(?m)^using\s+(static\s+)?Xunit(\.[\w.]+)?\s*;[ \t]*\r?\n', '', text)
    return text


def flag_await_blocks(text, conv):
    """`await` can't go inside `lock` or `unsafe` blocks: leave a TODO above each such block."""
    m = mask(text)
    for mt in reversed(list(re.finditer(r'(?m)^([ \t]*)(lock\s*\(|unsafe\s*\{)', m))):
        brace = m.find('{', mt.end() - 1)
        close = match_close(m, brace) if brace >= 0 else -1
        if close < 0 or not re.search(r'\bawait\b', m[brace:close]):
            continue
        what = 'lock' if mt.group(2).startswith('lock') else 'unsafe'
        hint = ('copy what you need under the lock, assert after it' if what == 'lock' else
                'move the pointer code into a sync helper that returns plain values')
        conv.todos.append(f'await inside {what}')
        text = text[:mt.start()] + f'{mt.group(1)}// TODO(TUnit): await is not allowed inside {what}: {hint}\n' + text[mt.start():]
    return text


def convert(text, types=None, probe=False):
    conv = Converter(text, types, probe)
    text = convert_usings(text)
    text = convert_classes(text, conv)
    text = convert_theory_data(text, conv)
    text = convert_attributes(text, conv)
    conv.find_sync_helpers(text)
    text = conv.convert(text)
    text = convert_cancellation(text)
    text, _ = make_async(text, conv)
    text = flag_await_blocks(text, conv)
    text = aaa(text, conv)
    if re.search(r'\bITestOutputHelper\b', text):
        conv.todos.append('ITestOutputHelper -> Console.WriteLine / TestContext.Current.OutputWriter')
    return text, conv


# --------------------------------------------------------------------------------------------------------------------
# --build: ask the compiler

PROBE_ERROR = re.compile(r"(?P<file>[^\s(][^(]*?)\((?P<line>\d+),(?P<col>\d+)\): error CS1061: '(?P<type>.+?)' does not "
                         r"contain a definition for '__TypeProbe(?P<id>\d+E?)'")
DIAGNOSTIC = re.compile(r"(?P<file>[^\s(][^(]*?)\((?P<line>\d+),(?P<col>\d+)\): (?P<level>error|warning) (?P<code>\w+): "
                        r"(?P<message>.*?)(?: \[[^\]]*\])?$")


def find_project(path):
    d = os.path.dirname(os.path.abspath(path))
    while True:
        found = sorted(glob.glob(os.path.join(d, '*.csproj')))
        if found:
            return found[0]
        parent = os.path.dirname(d)
        if parent == d:
            return None
        d = parent


def dotnet_build(project):
    r = subprocess.run(['dotnet', 'build', project, '-nologo'], capture_output=True, text=True)
    return r.stdout + r.stderr


def diagnostics(output, path):
    """Unique (line, col, level, code, message) diagnostics of one file in build output."""
    want = os.path.abspath(path)
    seen = set()
    for line in output.splitlines():
        mt = DIAGNOSTIC.search(line.strip())
        if mt and os.path.abspath(mt.group('file')) == want:
            seen.add((int(mt.group('line')), int(mt.group('col')), mt.group('level'), mt.group('code'), mt.group('message')))
    return sorted(seen)


def probe_types(paths, sources, project):
    """Builds the project with every assertion subject wrapped in `(expr).__TypeProbe<id>`: the compiler's CS1061
    errors name each subject's static type. The original files are always put back."""
    try:
        for p in paths:
            probe, _ = convert(sources[p], probe=True)
            with open(p, 'w', encoding='utf-8') as f:
                f.write(probe)
        output = dotnet_build(project)
    finally:
        for p in paths:
            with open(p, 'w', encoding='utf-8') as f:
                f.write(sources[p])
    types = {p: {} for p in paths}
    by_file = {os.path.abspath(p): p for p in paths}
    for line in output.splitlines():
        mt = PROBE_ERROR.search(line.strip())
        if mt and os.path.abspath(mt.group('file')) in by_file:
            types[by_file[os.path.abspath(mt.group('file'))]][mt.group('id')] = mt.group('type')
    return types, output


def other_errors(output, paths):
    """Errors in files other than the ones being converted (e.g. another file still on xUnit)."""
    mine = {os.path.abspath(p) for p in paths}
    found = set()
    for line in output.splitlines():
        mt = DIAGNOSTIC.search(line.strip())
        if mt and mt.group('level') == 'error' and os.path.abspath(mt.group('file')) not in mine:
            found.add(f"{mt.group('file')}({mt.group('line')},{mt.group('col')}): {mt.group('code')}: {mt.group('message')}")
    return sorted(found)


def warn_other_errors(output, paths, when):
    errs = other_errors(output, paths)
    if errs:
        print(f'{when}: {len(errs)} error(s) in other files. Declaration errors there (an xUnit [Fact] the project '
              'can no longer resolve, say) make the compiler skip method bodies, so this file\'s types and errors go '
              'unreported: convert those files in the same run, or fix them first. The first ones:', file=sys.stderr)
        for e in errs[:5]:
            print('    ' + e, file=sys.stderr)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('files', nargs='+')
    ap.add_argument('--stdout', action='store_true', help='print the converted file instead of rewriting it')
    ap.add_argument('--check', action='store_true', help='only report TODOs, change nothing')
    ap.add_argument('--build', action='store_true',
                    help='use the compiler: a probe build reports the type of every assertion subject (collections, '
                         'spans, numeric widening), then a final build lists what is left to fix')
    ap.add_argument('--project', help='the .csproj that compiles the files (default: the nearest one above the first file)')
    args = ap.parse_args()
    sources = {}
    for path in args.files:
        with open(path, encoding='utf-8-sig') as f:
            sources[path] = f.read()
    types = {p: {} for p in args.files}
    project = None
    if args.build:
        project = args.project or find_project(args.files[0])
        if not project:
            print('--build: no .csproj found above the file; use --project', file=sys.stderr)
            return 2
        print(f'probe build of {project} ...', file=sys.stderr)
        types, output = probe_types(args.files, sources, project)
        warn_other_errors(output, args.files, 'probe build')
        for p in args.files:
            if not types[p]:
                print(f'{p}: the probe build reported no types (does the project restore and build?); '
                      'guessing from the text instead', file=sys.stderr)
    status = 0
    for path in args.files:
        out, conv = convert(sources[path], types[path])
        todos = out.count('TODO(TUnit)') + out.count('TODO(AAA)')
        if args.stdout:
            sys.stdout.write(out)
        elif not args.check:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(out)
        print(f'{path}: {todos} TODO(s)' + (': ' + '; '.join(sorted(set(conv.todos))) if conv.todos else ''), file=sys.stderr)
        status |= 1 if todos else 0
    if args.build and not args.stdout and not args.check:
        output = dotnet_build(project)
        warn_other_errors(output, args.files, 'final build')
        for path in args.files:
            left = [d for d in diagnostics(output, path) if d[3] != 'NU1903']
            for line, col, level, code, message in left:
                print(f'{path}({line},{col}): {level} {code}: {message}', file=sys.stderr)
            if left:
                status |= 1
            print(f'{path}: {sum(1 for d in left if d[2] == "error")} error(s), '
                  f'{sum(1 for d in left if d[2] == "warning")} warning(s) left after conversion', file=sys.stderr)
    return status


if __name__ == '__main__':
    sys.exit(main())
