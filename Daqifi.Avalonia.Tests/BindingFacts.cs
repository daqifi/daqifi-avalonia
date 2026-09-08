using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Reads the checkout's own source files and states facts about the bindings in them.
///
/// <para>
/// Most views in this repo declare no <c>x:DataType</c>, so their bindings are resolved by
/// reflection at runtime: a renamed, moved or deleted member fails <b>silently</b> — the control
/// renders blank, or, for a <c>bool</c> target, sits at its default, which for <c>IsVisible</c> is
/// <c>true</c> — while every head still builds green. Nothing in the compiler or the build gate can
/// see that break, which is why these facts are asserted textually here instead.
/// </para>
///
/// <para>
/// <c>ConnectionDialog.axaml</c> is the one exception: it declares <c>x:DataType</c> <b>and</b>
/// <c>x:CompileBindings="True"</c>, so its member names are the compiler's business rather than this
/// helper's. What is still worth pinning on such a view is the part compiled bindings do not check —
/// which attribute a binding feeds — and the declarations themselves, whose removal is silent.
/// Spreading this to the rest of the views is tracked separately; do not take this helper's continued
/// existence as a reason not to.
/// </para>
///
/// <para>
/// <c>MobileShellView.axaml</c> is <b>not</b> a second exception, and is the trap worth naming here:
/// it declares <c>x:DataType</c> but no <c>x:CompileBindings</c>, and
/// <c>AvaloniaUseCompiledBindingsByDefault</c> is <c>false</c>, so its <c>{Binding}</c>s are still
/// resolved by reflection and nothing checks their member names. Measured: <c>{Binding ManualIpZZZ}</c>
/// there builds with <c>0 Error(s)</c> and no diagnostic. Tracked as issue #326. Reading its
/// <c>x:DataType</c> as protection is the mistake this paragraph exists to prevent.
/// </para>
///
/// <para>
/// <see cref="AssertBinds"/> and <see cref="AssertExposes"/> are meant to be used together: the
/// binding exists in the markup, and the member it names is readable on the type the markup will
/// meet at runtime. <see cref="AssertBinds"/> carries most of that weight, because it is the half
/// nothing else in the build can see: without it the markup can go on naming a member that no longer
/// exists while every compiler check, both heads and every other test still pass.
/// </para>
///
/// <para>
/// <see cref="AssertExposes"/> is the narrower of the two, because call sites pass the member name as
/// <c>nameof</c>: deleting or renaming the member then breaks the build, and the runtime assertion
/// never gets to run. What it still catches is the shape the compiler is content with and a binding
/// is not — a member that becomes a public field or a method, or a property that loses its getter,
/// all of which leave <c>GetProperty</c> returning null or a write-only property behind.
/// </para>
/// </summary>
internal static class BindingFacts
{
    /// <summary>Reads a file from the checkout, given its repo-relative path with '/' separators.</summary>
    internal static string Source(string repoRelativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), repoRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Asserts that a view contains the given binding text verbatim.</summary>
    internal static void AssertBinds(string repoRelativeViewPath, string expectedBinding) =>
        Assert.Contains(expectedBinding, Source(repoRelativeViewPath), StringComparison.Ordinal);

    /// <summary>The XAML language namespace, which is where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Parses a view as XML and hands back its root element.</summary>
    private static XElement ViewRoot(string repoRelativeViewPath) =>
        XDocument.Parse(Source(repoRelativeViewPath)).Root
        ?? throw new InvalidOperationException($"{repoRelativeViewPath} has no root element.");

    /// <summary>
    /// Asserts that the view's <b>root element</b> carries the given XAML-namespace attribute with the
    /// given value.
    ///
    /// <para>
    /// Deliberately not <see cref="AssertBinds"/>. That is an ordinal substring search over the whole
    /// file, and the declarations this guards are the one case where the distinction bites: the literal
    /// <c>x:CompileBindings="True"</c> appearing <i>anywhere</i> — the prose comment in the view that
    /// explains the attribute is the obvious place — satisfies a substring search while the attribute
    /// itself is gone from the tag. Measured: with the attribute deleted from the <c>Window</c> and the
    /// literal left in that comment, the substring form of this guard passed, a deliberately dead
    /// binding built with <c>0 Error(s)</c>, and all 37 bindings were back on reflection. Parsing the
    /// markup is what makes the guard mean what it says (Qodo round 2 on PR #325).
    /// </para>
    /// </summary>
    internal static void AssertRootDeclares(string repoRelativeViewPath, string attributeName, string expectedValue)
    {
        var actual = ViewRoot(repoRelativeViewPath).Attribute(XamlNamespace + attributeName)?.Value;

        Assert.True(
            actual is not null,
            $"{repoRelativeViewPath}: the root element declares no x:{attributeName}. A copy of the "
            + "literal elsewhere in the file — a comment, say — does not enable compiled bindings.");
        Assert.Equal(expectedValue, actual);
    }

    /// <summary>
    /// Asserts that the <c>DataTemplate</c> inside the <b>named</b> list scopes itself to the given item
    /// type, and that <b>every</b> <c>DataTemplate</c> in the view scopes itself to something.
    ///
    /// <para>
    /// Bound to the owning list by name rather than checking that the type appears somewhere among the
    /// scopes, because set membership cannot tell two lists apart: exchanging the WiFi and USB
    /// <c>x:DataType</c>s leaves the set of declared scopes identical and would satisfy an
    /// any-of assertion for all three (Qodo round 3 on PR #325).
    /// </para>
    ///
    /// <para>
    /// The compiler does currently reject that particular swap — measured, <c>AVLN2000: Unable to
    /// resolve ... 'PortName' on type 'DaqifiStreamingDevice'</c> — but only because these two device
    /// types happen to expose different members today. That is a coincidence of the model, not a
    /// property of the guard, and it stops holding the moment the two types converge on the names this
    /// view binds. Naming the list costs one string and does not depend on that coincidence.
    /// </para>
    ///
    /// <para>
    /// The every-template half covers what a per-list check cannot: a template added later with no
    /// <c>x:DataType</c> of its own is the inherited-scope escape hatch this view exists to close, and a
    /// list naming the three that exist today would not notice a fourth.
    /// </para>
    /// </summary>
    internal static void AssertTemplateScopedTo(
        string repoRelativeViewPath, string listName, string expectedItemType)
    {
        var root = ViewRoot(repoRelativeViewPath);

        var templates = root.Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .ToList();

        var unscoped = templates.Count(template => template.Attribute(XamlNamespace + "DataType") is null);
        Assert.True(
            unscoped == 0,
            $"{repoRelativeViewPath}: {unscoped} of {templates.Count} DataTemplates declare no "
            + "x:DataType, so their bindings resolve against an inherited scope rather than the item.");

        var list = root.Descendants()
            .SingleOrDefault(element => element.Attribute(XamlNamespace + "Name")?.Value == listName);
        Assert.True(list is not null, $"{repoRelativeViewPath}: no element is x:Named '{listName}'.");

        var scoped = list!.Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .Select(template => template.Attribute(XamlNamespace + "DataType")?.Value)
            .ToList();

        Assert.True(
            scoped.Count == 1,
            $"{repoRelativeViewPath}: '{listName}' holds {scoped.Count} DataTemplates, expected exactly one.");
        Assert.Equal(expectedItemType, scoped[0]);
    }

    /// <summary>
    /// Asserts the other half: a reflection binding resolves against the runtime type, so the
    /// member must be public and readable there — the half the compiler never checks.
    /// </summary>
    internal static void AssertExposes(Type dataContext, string memberName)
    {
        var property = dataContext.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.True(property.CanRead, $"{dataContext.Name}.{memberName} cannot be read by a binding.");
    }

    /// <summary>Walks up from the test binary to the checkout, identified by the solution file.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Daqifi.Avalonia.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
