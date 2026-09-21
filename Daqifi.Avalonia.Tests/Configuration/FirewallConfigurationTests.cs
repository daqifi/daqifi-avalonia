using System;
using System.IO;
using Daqifi.Desktop.Common;
using Daqifi.Desktop.Configuration;
using Xunit;

namespace Daqifi.Avalonia.Tests.Configuration;

/// <summary>
/// Characterisation tests for the firewall-rule bootstrap: the un-elevated early return in
/// <see cref="FirewallConfiguration.InitializeFirewallRules"/>, and the input validation in
/// <see cref="WindowsFirewallWrapper"/> that has to reject a bad rule name *before* the
/// Windows Firewall COM objects are ever reached.
///
/// <para>
/// Nothing pinned any of this. That matters most for the validation half: every guard in
/// <see cref="WindowsFirewallWrapper"/> exists to keep a caller-supplied string out of a COM
/// <c>InvokeMember</c> call, so a guard that quietly stopped rejecting would not fail a build,
/// would not change a screen, and would only show up as a malformed firewall rule on a user's
/// machine.
/// </para>
///
/// <para>
/// Every test here runs on any OS. The COM path is Windows-only, which is exactly what makes
/// these assertions sharp on a non-Windows host: a name the guard accepts reaches
/// <see cref="Type.GetTypeFromProgID"/> and fails, so "returned false" and "threw" tell the two
/// branches apart with no mocking at all. <see cref="RuleExists_reaches_COM_and_fails_for_a_VALID_name"/>
/// is the positive control that keeps the rejection tests honest — without it, a
/// <c>RuleExists</c> that returned false unconditionally would pass every other row.
/// </para>
/// </summary>
public class FirewallConfigurationTests
{
    #region The elevation gate

    /// <summary>
    /// The one production call site guards on <see cref="AppDataPaths.IsElevated"/>, and
    /// <see cref="FirewallConfiguration.InitializeFirewallRules"/> re-checks elevation itself
    /// before doing anything. On an un-elevated host it must take the early return: no COM, no
    /// throw, no rule.
    ///
    /// <para>
    /// SURVIVING MUTATION, recorded rather than hidden: deleting the elevation early return
    /// entirely leaves this test GREEN. <c>InitializeFirewallRules</c> wraps its whole body in a
    /// <c>catch (Exception)</c> that logs and shows a message box, so falling through to the COM
    /// path on a non-Windows host throws, is swallowed, and the method still returns normally.
    /// Nothing a black-box caller can observe distinguishes the two. So this row pins "the
    /// bootstrap does not blow up on an un-elevated host" — real, but weaker than it looks — and
    /// the elevation gate's correctness rests on it reading the same
    /// <see cref="AppDataPaths.IsElevated"/> the caller gates on, which is one hop to verify by
    /// eye. Giving it a sharper assertion would mean re-introducing an injection seam, which is
    /// the thing this change removed.
    /// </para>
    /// </summary>
    [Fact]
    public void InitializeFirewallRules_returns_quietly_on_an_un_elevated_host()
    {
        // An elevated host is not just outside what this row characterises — it is a host where
        // CALLING the method at all would be wrong. Past the gate, InitializeFirewallRules goes on
        // to add a real Windows Firewall rule, so on an administrator-token Windows machine this
        // test would mutate the developer's actual firewall configuration. The guard therefore has
        // to wrap the call, not merely the assertion in front of it.
        //
        // xunit 2.9.3 has no dynamic skip (Assert.Skip is v3), so this returns rather than
        // reporting Skipped, matching the two platform-conditional rows below. The hosts that
        // matter — CI's ubuntu runner and the dev Macs — are un-elevated, so the body does run.
        if (AppDataPaths.IsElevated)
        {
            return;
        }

        // Must not throw: the un-elevated branch informs the user and returns. It must not fall
        // through to the COM path, which on a non-Windows host cannot even be constructed.
        FirewallConfiguration.InitializeFirewallRules();
    }

    /// <summary>
    /// <see cref="AppDataPaths.IsElevated"/> is the app's single answer to "is this process
    /// elevated", and it reports false on every non-Windows head — firewall auto-configuration
    /// is a Windows-only concern. Pinned because the elevation gate above is only meaningful if
    /// this holds.
    /// </summary>
    [Fact]
    public void IsElevated_is_false_on_every_non_Windows_host()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(AppDataPaths.IsElevated);
    }

    #endregion

    #region RuleExists — the name guard runs before COM

    /// <summary>
    /// A rule name carrying any of the characters that could be used to inject into the COM
    /// call, or one that is empty or over the 255-character limit, is rejected outright:
    /// <c>false</c>, with no COM object ever created. On this non-Windows host that is
    /// observable — reaching COM throws (see the positive control below), so a plain
    /// <c>false</c> proves the guard short-circuited.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rule<name")]
    [InlineData("rule>name")]
    [InlineData("rule|name")]
    [InlineData("rule&name")]
    [InlineData("rule;name")]
    [InlineData("rule$name")]
    [InlineData("rule`name")]
    [InlineData("rule\0name")]
    [InlineData("rule\rname")]
    [InlineData("rule\nname")]
    public void RuleExists_rejects_a_dangerous_or_empty_name_without_reaching_COM(string ruleName)
    {
        Assert.False(new WindowsFirewallWrapper().RuleExists(ruleName));
    }

    /// <summary>256 characters is one over the limit; 255 is inside it.</summary>
    [Fact]
    public void RuleExists_rejects_a_name_longer_than_255_characters()
    {
        Assert.False(new WindowsFirewallWrapper().RuleExists(new string('a', 256)));
    }

    /// <summary>
    /// POSITIVE CONTROL. A name the guard accepts goes on to the Windows Firewall COM objects,
    /// which do not exist on this host, so the call throws <see cref="InvalidOperationException"/>
    /// rather than returning false. Without this row every rejection test above would still pass
    /// against a <c>RuleExists</c> that had stopped doing anything at all.
    /// </summary>
    [Fact]
    public void RuleExists_reaches_COM_and_fails_for_a_VALID_name()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(
            () => new WindowsFirewallWrapper().RuleExists("DAQiFi Desktop"));
    }

    #endregion

    #region CreateUdpRule — argument validation runs before COM

    /// <summary>
    /// A bad rule name is an <see cref="ArgumentException"/> on <c>ruleName</c>, thrown before
    /// any COM object is created — a different failure mode from <c>RuleExists</c>, which
    /// answers false for the same input.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("rule;name")]
    public void CreateUdpRule_rejects_a_bad_rule_name(string ruleName)
    {
        var exe = WriteTempFile(".exe");
        var ex = Assert.Throws<ArgumentException>(
            () => new WindowsFirewallWrapper().CreateUdpRule(ruleName, exe, 30303));
        Assert.Equal("ruleName", ex.ParamName);
    }

    /// <summary>An absent or blank application path is rejected on <c>applicationPath</c>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateUdpRule_rejects_a_blank_application_path(string path)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new WindowsFirewallWrapper().CreateUdpRule("DAQiFi Desktop", path, 30303));
        Assert.Equal("applicationPath", ex.ParamName);
    }

    /// <summary>
    /// A path that does not exist is rejected. This is the guard that stops a firewall rule
    /// being written for an executable that was never there.
    /// </summary>
    [Fact]
    public void CreateUdpRule_rejects_an_application_path_that_does_not_exist()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"daqifi-absent-{Guid.NewGuid():N}.exe");

        var ex = Assert.Throws<ArgumentException>(
            () => new WindowsFirewallWrapper().CreateUdpRule("DAQiFi Desktop", missing, 30303));
        Assert.Equal("applicationPath", ex.ParamName);
    }

    /// <summary>
    /// A file that exists but is not an <c>.exe</c> is rejected — existence alone is not enough.
    /// </summary>
    [Fact]
    public void CreateUdpRule_rejects_an_application_path_that_is_not_an_exe()
    {
        var notAnExe = WriteTempFile(".txt");

        var ex = Assert.Throws<ArgumentException>(
            () => new WindowsFirewallWrapper().CreateUdpRule("DAQiFi Desktop", notAnExe, 30303));
        Assert.Equal("applicationPath", ex.ParamName);
    }

    /// <summary>
    /// POSITIVE CONTROL for the rows above: inputs that clear every argument guard go on to the
    /// COM path and fail there, not in validation. An <see cref="InvalidOperationException"/> —
    /// never an <see cref="ArgumentException"/> — is how you know validation passed.
    /// </summary>
    [Fact]
    public void CreateUdpRule_reaches_COM_for_inputs_that_clear_every_guard()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exe = WriteTempFile(".exe");

        Assert.Throws<InvalidOperationException>(
            () => new WindowsFirewallWrapper().CreateUdpRule("DAQiFi Desktop", exe, 30303));
    }

    #endregion

    private static string WriteTempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"daqifi-firewall-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
