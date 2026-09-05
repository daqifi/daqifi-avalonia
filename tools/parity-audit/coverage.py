#!/usr/bin/env python3
"""Coverage diff between daqifi-desktop (WPF) and this port, at two levels.

    python3 tools/parity-audit/coverage.py [--names] [path-to-daqifi-desktop]
    python3 tools/parity-audit/coverage.py --bindings [path-to-daqifi-desktop]

**Declared** (default mode). Every ported symbol carries a
`// @port: <upstream fully-qualified symbol>` backlink. Upstream symbols with no
backlink are *candidates* for a parity gap. This replaces the `relation: subset`
/ `not_yet_ported` signal from the `.portomatic/map/*.yaml` shards, which no
longer exist in either checkout.

Output is candidates, not verdicts. Every hit needs triage by hand, because a
missing backlink has four innocent causes besides a real gap:

  * the symbol was renamed downstream (DebugDataHistory -> DebugDataCollection)
  * it is WPF-only scar tissue correctly dropped (BooleanToVisibilityConverter)
  * it is a nested type or member the regex mis-attributes to its outer type
  * it is dead state upstream: written, never read, never surfaced to a user

`--names` filters to the subset whose bare identifier appears nowhere in the
port at all, which removes most renames and is the fastest place to start.

**Bound** (`--bindings`). Declared is not the same as reachable, and the 2026-09-03
audit conflated them: it reported "67 command bindings upstream, 0 absent
downstream" from a *symbol* diff, which counts a command as present the moment it
is declared. A command that exists downstream but that no view binds is invisible
to the user and indistinguishable from one that was never ported. So this mode
reads the XAML on both sides instead of the C#, and reports three things:

  A. members WPF binds in `.xaml` that no `.axaml` binds downstream — the parity
     half, and the one the declared-level diff cannot see;
  B. downstream commands that no `.axaml` binds — a command's only purpose is to
     be invoked from a view, so one nothing binds and no C# executes is dead;
  C. downstream `[ObservableProperty]` state that no `.axaml` binds, flagging the
     subset that is only ever assigned and never read back.

B and C need no upstream checkout and say nothing about parity on their own; they
find the *port's* unreachable surface, which is the defect class A was blind to.
Both are noisy by construction — plenty of observable state is legitimately
consumed only from C# — so triage every hit. C especially: treat it as a reading
list, not a defect list.

Known-true control, so a change to this file that breaks the check is visible:
`--bindings` must report `CancelFirmwareUploadCommand` and
`CancelUploadFirmwareCommand` under B. Neither app binds either one.

See docs/parity-ledger.md for the last triage and what it concluded.
"""
import argparse
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def find_upstream():
    """Locate a daqifi-desktop checkout by walking up from this repo.

    Handles the plain checkout (sibling of daqifi-avalonia) and a git worktree
    under .claude/worktrees/, where the sibling is several levels further up.
    Returns None rather than guessing wrong; the caller then asks for the path.
    """
    d = REPO
    while True:
        cand = os.path.join(os.path.dirname(d), 'daqifi-desktop')
        if os.path.isdir(os.path.join(cand, 'Daqifi.Desktop')):
            return cand
        parent = os.path.dirname(d)
        if parent == d:
            return None
        d = parent

NS_RE = re.compile(r'^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]?\s*$')
TYPE_RE = re.compile(
    r'^(\s*)(?:\[[^\]]*\]\s*)*'
    r'(?:(?:public|internal|private|protected|sealed|static|abstract|partial)\s+)*'
    r'\b(?:class|interface|enum|struct|record)\s+([A-Za-z0-9_]+)'
)
MEMBER_RE = re.compile(
    r'^\s+(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal)\s+'
    # `const` and `volatile` belong here, not in the type slot: without them
    # `public const int Foo = 1` parses `const` as the type and never matches.
    r'(?:(?:static|virtual|override|async|sealed|partial|readonly|const|volatile'
    r'|new|extern|unsafe|required|abstract)\s+)*'
    r'(?:[A-Za-z0-9_<>,\[\]\?\.]+)\s+'
    r'([A-Za-z0-9_]+)\s*[\(\{=]'
)
SKIP = {'get', 'set', 'if', 'return', 'new', 'switch', 'while', 'for', 'foreach', 'using', 'lock'}
PRUNE = {'obj', 'bin', '.git', 'third_party', '.claude'}


def norm(s):
    return re.sub(r'<.*?>|`\d+', '', s)


def walk(root, exts):
    for dirpath, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in PRUNE]
        for f in sorted(files):
            if f.endswith(exts):
                yield os.path.join(dirpath, f)


def read(path):
    with open(path, errors='replace') as fh:
        return fh.read()


def collect_backlinks(port_root):
    """Every `@port:` target declared in the port, plus each implied ancestor."""
    types, members = set(), set()
    pat = re.compile(r'@port:\s*([A-Za-z0-9_.<>,`]+)')
    for path in walk(port_root, ('.cs', '.axaml')):
        for m in pat.finditer(read(path)):
            fqn = norm(m.group(1))
            members.add(fqn)
            parts = fqn.split('.')
            for i in range(2, len(parts) + 1):
                types.add('.'.join(parts[:i]))
    return types, members


def collect_upstream(upstream_root):
    """Upstream types and their public/internal members, by declaration order."""
    types, members = {}, {}
    for path in walk(upstream_root, ('.cs',)):
        rel = os.path.relpath(path, upstream_root)
        ns, stack = None, []
        for line in read(path).splitlines():
            m = NS_RE.match(line)
            if m:
                ns = m.group(1)
                continue
            m = TYPE_RE.match(line)
            if m and ns:
                indent = len(m.group(1))
                while stack and stack[-1][1] >= indent:
                    stack.pop()
                stack.append((m.group(2), indent))
                types.setdefault(f"{ns}.{m.group(2)}", rel)
                continue
            if not (ns and stack):
                continue
            m = MEMBER_RE.match(line)
            if m and m.group(1) not in SKIP and m.group(1) != stack[-1][0]:
                members.setdefault(f"{ns}.{stack[-1][0]}.{m.group(1)}", rel)
    return types, members


# --- bindings: what the XAML on each side actually reaches -------------------
#
# The binding half deliberately parses markup rather than compiled symbols. WPF's
# `{Binding ...}` and Avalonia's `{Binding}` / `{CompiledBinding}` /
# `{ReflectionBinding}` are the only ways a view names a view-model member, so a
# member absent from every one of them is a member no view can show or invoke.

MARKUP_RE = re.compile(r'\{\s*(?:Binding|CompiledBinding|ReflectionBinding)\b')
# A binding can also be written as an element, and inside `<MultiBinding>` it has
# to be. Both trees do: 19 in the port, 5 upstream. Scanning only the markup form
# reports every member reached that way as unreachable.
ELEMENT_RE = re.compile(r'<(?:Compiled|Reflection)?Binding\b([^>]*)>')
ELEMENT_PATH_RE = re.compile(r'\bPath\s*=\s*"([^"]*)"')
IDENT_RE = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')
# `$parent[Window]`, `$parent`, `$self` — Avalonia's relative-source prefixes.
RELATIVE_RE = re.compile(r'\$(?:parent|self)(?:\[[^\]]*\])?\.?')
# WPF's equivalents. TemplatedParent and Self address the control, not a
# view-model, so those bindings are control-template plumbing and are dropped
# whole — `IsDropDownOpen` on a ComboBox template is not a parity signal.
TEMPLATE_SOURCE_RE = re.compile(
    r'RelativeSource\s*=\s*[\'"{\s]*(?:RelativeSource\s+)?(?:TemplatedParent|Self)\b')


def markup_bodies(text):
    """Yield the body of every binding markup extension, brace-balanced.

    Nested extensions are common (`{Binding X, Converter={StaticResource Y}}`),
    so the closing brace has to be matched by depth rather than by `[^}]*`.
    """
    for m in MARKUP_RE.finditer(text):
        depth, i = 0, m.start()
        while i < len(text):
            if text[i] == '{':
                depth += 1
            elif text[i] == '}':
                depth -= 1
                if depth == 0:
                    break
            i += 1
        yield text[m.end():i]


def split_args(body):
    """Split a markup body on top-level commas."""
    out, depth, cur = [], 0, ''
    for ch in body:
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur)
            cur = ''
        else:
            cur += ch
    out.append(cur)
    return out


def binding_paths(body):
    """The binding paths in one markup body: the positional first argument, and
    any explicit `Path=`. Other named arguments (Converter, Mode, StringFormat,
    ElementName…) are not paths."""
    if TEMPLATE_SOURCE_RE.search(body):
        return []
    got = []
    for i, part in enumerate(split_args(body)):
        p = part.strip()
        if not p:
            continue
        if '=' in p:
            key, _, val = p.partition('=')
            if key.strip() != 'Path':
                continue
            p = val.strip()
        elif i != 0:
            continue
        got.append(p)
    return got


def path_members(path):
    """The member identifiers a binding path names.

    Every segment counts, not just the leaf: WPF binds `SummaryLogger.SampleSize`
    and the port may reach the same value through a different parent, so both
    `SummaryLogger` and `SampleSize` are evidence of a bound member.
    """
    p = RELATIVE_RE.sub('', path).lstrip('!^ ')
    if p.startswith('#'):                 # #elementName.Member
        p = p.split('.', 1)[1] if '.' in p else ''
    p = re.sub(r'\([^)]*\)', '', p)       # (ns:Type.AttachedProperty)
    p = re.sub(r'\[[^\]]*\]', '', p)      # indexers
    return IDENT_RE.findall(p)


def element_paths(text):
    """Binding paths written as `<Binding Path="…"/>` rather than `{Binding …}`."""
    for m in ELEMENT_RE.finditer(text):
        attrs = m.group(1)
        if TEMPLATE_SOURCE_RE.search(attrs):
            continue
        yield from ELEMENT_PATH_RE.findall(attrs)


def collect_bound(root, exts):
    """name -> set of files whose XAML binds it."""
    by_name = {}
    for path in walk(root, exts):
        text = read(path)
        paths = [bp for body in markup_bodies(text) for bp in binding_paths(body)]
        paths += element_paths(text)
        for bp in paths:
            for name in path_members(bp):
                by_name.setdefault(name, set()).add(path)
    return by_name


ATTRIBUTE_RE = re.compile(r'^\s*\[([A-Za-z0-9_]+)[^\]]*\]\s*')
DECL_METHOD_RE = re.compile(
    r'^\s*(?:(?:public|private|internal|protected|static|async|partial|override|virtual)\s+)*'
    r'[A-Za-z0-9_<>,\[\]\?\.]+\s+([A-Za-z0-9_]+)\s*\(')
DECL_FIELD_RE = re.compile(
    r'^\s*(?:(?:private|internal|protected|public|readonly|static|partial)\s+)*'
    r'[A-Za-z0-9_<>,\[\]\?\.]+\s+([A-Za-z0-9_]+)\s*[;=]')
DECL_COMMAND_RE = re.compile(
    r'^\s*public\s+(?:(?:static|readonly|virtual|override)\s+)*'
    r'(?:I?(?:Async)?RelayCommand|ICommand)[A-Za-z0-9_<>,\?\.]*\s+([A-Za-z0-9_]+)\s*[\{=]')
# `[NotifyCanExecuteChangedFor(nameof(FooCommand))]` and its property twin are
# change-notification wiring, not a use. Counting them as one is how a command
# nothing invokes looks invoked.
NOTIFY_RE = re.compile(r'Notify(?:CanExecuteChanged|PropertyChanged)For\(\s*nameof\([A-Za-z0-9_]+\)\s*\)')


def strip_attributes(line):
    """Peel leading attributes off a declaration line.

    Returns `(names, rest)`. Both `[ObservableProperty]\\nprivate bool _x;` and
    `[ObservableProperty] private bool _x;` occur in this repo — 32 of the
    latter — so the attribute and the declaration have to be handled whether or
    not a newline separates them. Treating them as separate lines unconditionally
    dropped those 32 *and* mis-attributed each one to the next declaration below
    it, which is how a `private static readonly Brush[]` field was once reported
    as unbound observable state.
    """
    names, rest = [], line
    while True:
        m = ATTRIBUTE_RE.match(rest)
        if not m:
            return names, rest
        names.append(m.group(1))
        rest = rest[m.end():]


def collect_declared(port_root, repo_root):
    """MVVM-toolkit generated surface in the port: (commands, properties).

    Each is a list of `(generated name, repo-relative file, line)`, one entry per
    **declaration**. `[RelayCommand]` on `FooAsync` generates `FooCommand`, and
    `[ObservableProperty]` on `_foo` generates `Foo` — the generated names are
    what XAML binds, and the only place they appear in source is the attribute
    above the private member.

    Keying by name instead would silently merge declarations: `IsSettingsOpen`,
    `HasConnectedDevice`, `IsLoggingActive` and `IsSelected` are each declared by
    two different view-models here, so a name-keyed dict undercounts the surface
    and reports only the first site of each.
    """
    commands, properties = [], []
    for path in walk(port_root, ('.cs',)):
        rel = os.path.relpath(path, repo_root)
        pending = None
        for i, line in enumerate(read(path).splitlines()):
            attrs, rest = strip_attributes(line)
            if 'RelayCommand' in attrs:
                pending = 'cmd'
            elif 'ObservableProperty' in attrs:
                pending = 'prop'
            elif attrs and not rest.strip():
                continue          # an unrelated attribute on its own line
            if not rest.strip() or rest.lstrip().startswith('//'):
                continue          # the declaration is on a later line
            if pending:
                if pending == 'cmd':
                    m = DECL_METHOD_RE.match(rest)
                    if m:
                        commands.append(
                            (re.sub(r'Async$', '', m.group(1)) + 'Command', rel, i + 1))
                else:
                    m = DECL_FIELD_RE.match(rest)
                    if m:
                        bare = m.group(1).lstrip('_')
                        properties.append((bare[:1].upper() + bare[1:], rel, i + 1))
                pending = None
                continue
            m = DECL_COMMAND_RE.match(rest)
            if m:
                commands.append((m.group(1), rel, i + 1))
    return commands, properties


def csharp_index(repo_root):
    """repo-relative path -> source with notification wiring blanked out."""
    return {os.path.relpath(p, repo_root): NOTIFY_RE.sub('', read(p))
            for p in walk(repo_root, ('.cs',))}


def referenced_in(name, sources, exclude):
    pat = re.compile(r'\b' + re.escape(name) + r'\b')
    return sorted(rel for rel, text in sources.items()
                  if rel != exclude and pat.search(text))


def read_anywhere(name, sources):
    """True if `name` appears somewhere that is not the left side of an assignment."""
    assign = re.compile(r'\b' + re.escape(name) + r'\s*=(?!=)')
    pat = re.compile(r'\b' + re.escape(name) + r'\b')
    return any(pat.search(assign.sub('', text)) for text in sources.values())


def report_bindings(upstream_proj, port):
    up_bound = collect_bound(upstream_proj, ('.xaml',))
    port_bound = collect_bound(REPO, ('.axaml',))
    commands, properties = collect_declared(port, REPO)
    sources = csharp_index(REPO)

    absent = sorted(n for n in up_bound if n not in port_bound)
    unbound_cmds = sorted(d for d in commands if d[0] not in port_bound)
    unbound_props = sorted(d for d in properties if d[0] not in port_bound)

    print(f"bound upstream  {len(up_bound):5}   not bound downstream {len(absent):5}")
    print(f"port commands   {len(commands):5}   bound by no .axaml   {len(unbound_cmds):5}")
    print(f"port observable {len(properties):5}   bound by no .axaml   {len(unbound_props):5}")

    print("\n== A. BOUND UPSTREAM, BOUND BY NO .axaml DOWNSTREAM")
    for name in absent:
        where = ', '.join(sorted(os.path.basename(f) for f in up_bound[name]))
        print(f"   {name}\n       {where}")

    print("\n== B. PORT COMMANDS BOUND BY NO .axaml")
    for name, rel, line in unbound_cmds:
        refs = referenced_in(name, sources, rel)
        via = f"invoked from {', '.join(os.path.basename(r) for r in refs)}" if refs \
            else "NOT invoked from any .cs either"
        print(f"   {name}\n       {rel}:{line}\n       {via}")

    print("\n== C. PORT OBSERVABLE PROPERTIES BOUND BY NO .axaml")
    for name, rel, line in unbound_props:
        mark = '' if read_anywhere(name, sources) else '   [write-only]'
        print(f"   {name}{mark}\n       {rel}:{line}")


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('upstream', nargs='?',
                    help='daqifi-desktop checkout (default: found next to this repo)')
    ap.add_argument('--names', action='store_true',
                    help='only report candidates whose bare name is absent from the port')
    ap.add_argument('--bindings', action='store_true',
                    help='diff what the XAML binds, not what the C# declares')
    args = ap.parse_args()

    upstream = args.upstream or find_upstream()
    if not upstream:
        sys.exit("could not find a daqifi-desktop checkout next to this repo; "
                 "pass its path: python3 tools/parity-audit/coverage.py <path>")

    up_proj = os.path.join(upstream, 'Daqifi.Desktop')
    if not os.path.isdir(up_proj):
        sys.exit(f"not a daqifi-desktop checkout: {upstream}")

    port = os.path.join(REPO, 'Daqifi.Avalonia')

    if args.bindings:
        report_bindings(up_proj, port)
        return

    linked_types, linked_members = collect_backlinks(port)
    up_types, up_members = collect_upstream(up_proj)

    blob = "\n".join(read(p) for p in walk(REPO, ('.cs', '.axaml'))) if args.names else None

    def absent(fqn):
        if blob is None:
            return True
        return not re.search(r'\b' + re.escape(fqn.split('.')[-1]) + r'\b', blob)

    miss_t = [t for t in sorted(up_types) if t not in linked_types and absent(t)]
    miss_m = [m for m in sorted(up_members) if norm(m) not in linked_members and absent(m)]

    print(f"upstream types   {len(up_types):5}   no backlink {len(miss_t):5}")
    print(f"upstream members {len(up_members):5}   no backlink {len(miss_m):5}")
    for label, miss, src in (("TYPES", miss_t, up_types), ("MEMBERS", miss_m, up_members)):
        if not miss:
            continue
        print(f"\n== {label}")
        for fqn in miss:
            print(f"   {fqn}\n       {src[fqn]}")


if __name__ == '__main__':
    main()
