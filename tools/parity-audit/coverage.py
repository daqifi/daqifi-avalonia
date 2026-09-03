#!/usr/bin/env python3
"""Symbol-coverage diff between daqifi-desktop (WPF) and this port.

Every ported symbol carries a `// @port: <upstream fully-qualified symbol>`
backlink. Upstream symbols with no backlink are *candidates* for a parity gap.

This replaces the `relation: subset` / `not_yet_ported` signal from the
`.portomatic/map/*.yaml` shards, which no longer exist in either checkout.

    python3 tools/parity-audit/coverage.py [path-to-daqifi-desktop]

Output is candidates, not verdicts. Every hit needs triage by hand, because a
missing backlink has four innocent causes besides a real gap:

  * the symbol was renamed downstream (DebugDataHistory -> DebugDataCollection)
  * it is WPF-only scar tissue correctly dropped (BooleanToVisibilityConverter)
  * it is a nested type or member the regex mis-attributes to its outer type
  * it is dead state upstream: written, never read, never surfaced to a user

`--names` filters to the subset whose bare identifier appears nowhere in the
port at all, which removes most renames and is the fastest place to start.

See docs/parity-ledger.md for the last triage and what it concluded.
"""
import argparse
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_UPSTREAM = "/Users/tylerkron/projects/daqifi/daqifi-desktop"

NS_RE = re.compile(r'^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]?\s*$')
TYPE_RE = re.compile(
    r'^(\s*)(?:\[[^\]]*\]\s*)*'
    r'(?:(?:public|internal|private|protected|sealed|static|abstract|partial)\s+)*'
    r'\b(?:class|interface|enum|struct|record)\s+([A-Za-z0-9_]+)'
)
MEMBER_RE = re.compile(
    r'^\s+(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal)\s+'
    r'(?:(?:static|virtual|override|async|sealed|partial|readonly|new|extern|unsafe)\s+)*'
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


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('upstream', nargs='?', default=DEFAULT_UPSTREAM,
                    help=f'daqifi-desktop checkout (default: {DEFAULT_UPSTREAM})')
    ap.add_argument('--names', action='store_true',
                    help='only report candidates whose bare name is absent from the port')
    args = ap.parse_args()

    up_proj = os.path.join(args.upstream, 'Daqifi.Desktop')
    if not os.path.isdir(up_proj):
        sys.exit(f"not a daqifi-desktop checkout: {args.upstream}")

    port = os.path.join(REPO, 'Daqifi.Avalonia')
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
