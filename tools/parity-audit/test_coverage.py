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

The third block covers `--bindings`, whose failure mode is the mirror image:
there, reading one binding path too generously *invents* reachability and hides
a gap. Its cases are the syntax that nearly did so — nested extensions,
`RelativeSource TemplatedParent`, and attributes sharing a line with the
declaration they decorate.
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


# --- --bindings ------------------------------------------------------------
# The binding half answers "does any view reach this member", so its failure
# mode is the opposite of the member regex's: over-reading a binding path
# invents reachability and hides a gap, exactly what these pin against.

def paths(markup, expected):
    """The binding paths one XAML attribute value yields."""
    got = [p for body in coverage.markup_bodies(markup)
           for p in coverage.binding_paths(body)]
    if got != expected:
        failures.append(f"binding_paths {markup!r}: expected {expected}, got {got}")


def names(path, expected):
    got = coverage.path_members(path)
    if got != expected:
        failures.append(f"path_members {path!r}: expected {expected}, got {got}")


paths('"{Binding SaveCommand}"', ['SaveCommand'])
paths('"{CompiledBinding SaveCommand}"', ['SaveCommand'])
paths('"{ReflectionBinding SaveCommand}"', ['SaveCommand'])
paths('"{Binding Path=SaveCommand}"', ['SaveCommand'])
# A nested extension must not end the outer one early, and the named arguments
# after the path are not themselves paths.
paths('"{Binding IsBusy, Converter={StaticResource BoolToVis}, Mode=OneWay}"', ['IsBusy'])
paths('"{Binding Value, StringFormat=HH:mm:ss}"', ['Value'])
# TemplatedParent / Self address the control, not a view-model. Counting a
# ComboBox template's IsDropDownOpen as a view-model member put it on the
# 2026-09-04 gap list until this dropped it.
paths('"{Binding Path=IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}"', [])
paths('"{TemplateBinding IsDropDownOpen}"', [])
paths('Text="literal"', [])

# Every segment of a path is a bound member: the port may reach the same value
# through a different parent (upstream `Port.PortName` -> downstream `PortName`).
names('SummaryLogger.SampleSize', ['SummaryLogger', 'SampleSize'])
names('$parent[UserControl].DataContext.Shell.IsDebugModeEnabled',
      ['DataContext', 'Shell', 'IsDebugModeEnabled'])
names('#DeviceList.SelectedItem', ['SelectedItem'])
names('!IsBusy', ['IsBusy'])
names('Devices[0].Name', ['Devices', 'Name'])

# Attributes and declarations share a line 32 times in this repo. Reading them
# as separate lines dropped all 32 and blamed each on the declaration below it.
if coverage.strip_attributes('    [ObservableProperty] private bool _isLoggingActive;') \
        != (['ObservableProperty'], 'private bool _isLoggingActive;'):
    failures.append("strip_attributes: inline [ObservableProperty] not split")
if coverage.strip_attributes('    [ObservableProperty]') != (['ObservableProperty'], ''):
    failures.append("strip_attributes: bare [ObservableProperty] not recognised")
if coverage.strip_attributes('    [RelayCommand(CanExecute = nameof(CanSave))]') \
        != (['RelayCommand'], ''):
    failures.append("strip_attributes: [RelayCommand(...)] not recognised")
if coverage.strip_attributes('    private bool _x;') != ([], '    private bool _x;'):
    failures.append("strip_attributes: plain declaration altered")

# Both halves against the real trees, so a silently-empty scan cannot pass.
# `CancelFirmwareUploadCommand` is the control the whole mode exists for — the
# 2026-09-03 symbol diff called it present, and no XAML in either app binds it.
# Pinned here is the *mechanism* that finds it (the generated name is collected,
# and the .axaml scan is real), not the gap itself: binding a Cancel button is a
# fix, and a fix must not turn this red.
commands, properties = coverage.collect_declared(
    os.path.join(coverage.REPO, 'Daqifi.Avalonia'), coverage.REPO)
bound = coverage.collect_bound(coverage.REPO, ('.axaml',))
if 'CancelFirmwareUploadCommand' not in commands:
    failures.append("collect_declared no longer generates CancelFirmwareUploadCommand "
                    "from its [RelayCommand]; --bindings cannot see the control")
if 'IsLoggingActive' not in properties:
    failures.append("collect_declared missed IsLoggingActive, an inline "
                    "[ObservableProperty] — the 32 inline declarations are being dropped")
if 'ClearDebugDataCommand' not in bound:
    failures.append("collect_bound did not find ClearDebugDataCommand, which "
                    "DebugWindow.axaml binds; the .axaml scan is not reading bindings")

if failures:
    print(f"FAIL ({len(failures)})")
    for f in failures:
        print(f"  {f}")
    sys.exit(1)
print("ok")
