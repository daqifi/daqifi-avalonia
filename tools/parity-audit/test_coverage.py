#!/usr/bin/env python3
"""Self-test for coverage.py's declaration parsing.

Follows the .github/scripts/test_*.py idiom: plain python3, asserts, non-zero
exit on failure. CI's guard sweep currently globs .github/scripts only, so run
this by hand:

    python3 tools/parity-audit/test_coverage.py

The member regex is the part most likely to be wrong — its first revision
silently skipped `const`, which dropped 26 members from the audit's denominator
and hid a real difference. (Adding `abstract` and `required` in the same fix
recovered a further 9 and 5, taking the total from 606 to 646.) These cases pin
the declaration forms that matter.

The second block pins what the regex deliberately does NOT see, so the boundary
stays visible instead of being rediscovered. Those forms were swept by hand
against upstream: 39 symbols fall in the blind spot and exactly one
(ServiceLocator.RegisterSingleton) is absent from the port, a refactor rather
than a gap. See "Limits of this ledger" in docs/parity-ledger.md.
"""
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("coverage", os.path.join(HERE, "coverage.py"))
coverage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coverage)

failures = []


def member(line, expected):
    """MEMBER_RE should capture `expected` (or None) from a declaration line."""
    m = coverage.MEMBER_RE.match(line)
    got = m.group(1) if m else None
    if got != expected:
        failures.append(f"MEMBER_RE {line.strip()!r}: expected {expected!r}, got {got!r}")


def typ(line, expected):
    m = coverage.TYPE_RE.match(line)
    got = m.group(2) if m else None
    if got != expected:
        failures.append(f"TYPE_RE {line.strip()!r}: expected {expected!r}, got {got!r}")


# Constants — the regression this test exists for.
member('    public const string FALLBACK_CHANNEL_COLOR = "#FF808080";', 'FALLBACK_CHANNEL_COLOR')
member('    internal const string FALLBACK_CHANNEL_COLOR = "#FF808080";', 'FALLBACK_CHANNEL_COLOR')
member('    public const int MaxEntries = 100;', 'MaxEntries')
member('    internal const uint EsSystemRequired = 0x00000001;', 'EsSystemRequired')

# Ordinary members must keep parsing.
member('    public void Reset()', 'Reset')
member('    public string Name { get; set; }', 'Name')
member('    internal static List<Profile> ParseProfiles(XDocument doc)', 'ParseProfiles')
member('    public async Task<bool> ConnectAsync(', 'ConnectAsync')
member('    public static readonly IComparer<string> Comparer =', 'Comparer')

# Private and protected members are out of scope by design.
member('    private int _count;', None)
member('    private void Helper()', None)
member('    protected override void OnStartup(StartupEventArgs e)', None)

# An abstract declaration ending in `;` is still seen — the trailing `;` is
# irrelevant, only the `(` matters.
member('    public abstract bool Write(string command);', 'Write')

# KNOWN BLIND SPOTS, pinned so they stay visible rather than being rediscovered.
# A declaration must contain `(`, `{` or `=` after the name, and the name is taken
# from before any `<` — so these three forms are invisible to the audit. Swept by
# hand (39 symbols, 1 absent downstream and a refactor); widen these before
# trusting a future zero, and re-sweep upstream if you do.
member('    public event Action<DebugDataModel>? DebugDataReceived;', None)  # event
member('    internal bool IsSyncingFromMinimap;', None)                      # bare field
member('    public static TInterface Resolve<TInterface>()', None)           # generic method

# Types.
typ('public partial class DebugDataHistory : ObservableObject', 'DebugDataHistory')
typ('    internal sealed record SessionChannelInfo(string ChannelName);', 'SessionChannelInfo')
typ('public enum ChannelType', 'ChannelType')
typ('internal class ChannelBuffer', 'ChannelBuffer')

# find_upstream must not invent a path that isn't a daqifi-desktop checkout.
found = coverage.find_upstream()
if found is not None and not os.path.isdir(os.path.join(found, 'Daqifi.Desktop')):
    failures.append(f"find_upstream returned {found!r}, which has no Daqifi.Desktop")

if failures:
    print(f"FAIL ({len(failures)})")
    for f in failures:
        print(f"  {f}")
    sys.exit(1)
print("ok")
