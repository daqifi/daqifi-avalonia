using System.Reflection;
using System.Text.RegularExpressions;
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
    /// Asserts that the view opens neither escape hatch — <c>x:CompileBindings="False"</c> on a
    /// subtree, which is inherited and overridable, and <c>{ReflectionBinding}</c> on an individual
    /// binding — the latter in <b>both</b> its spellings, the markup extension and the
    /// <c>&lt;ReflectionBinding/&gt;</c> object element. Any of them puts markup back on reflection
    /// with the root declaration still in place,
    /// the binding count unchanged and the build still green, so every other guard on the view would
    /// be measuring less than it claims.
    ///
    /// <para>
    /// Both halves read the <b>parsed</b> markup, never the raw text. This is the same lesson as
    /// <see cref="AssertRootDeclares"/> applied to the hatches instead of the declarations, and here
    /// the substring form was a hole rather than a nuisance: XML permits
    /// <c>x:CompileBindings = 'False'</c>, and whitespace around the <c>=</c> and single quotes are
    /// the same attribute to every parser while not being the literal a search looks for. Measured on
    /// <c>MainWindow.axaml</c> at <c>7921989</c> (issue #374): that spelling on the app-settings
    /// drawer with <c>{Binding AppSettings.RecoveryNoticeZZZ}</c> inside it — a member that does not
    /// exist — was a forced, non-incremental <c>Build succeeded</c> with <b>zero</b> <c>AVLN2000</c>,
    /// and all seven of that view's guards passed; the same dead binding <i>without</i> the opt-out is
    /// <c>AVLN2000 … 'RecoveryNoticeZZZ' on type 'SettingsViewModel'</c> at line 514. A guard reading
    /// as protection while a dead binding builds clean is the defect #327 exists to remove, one layer
    /// up. Parsing also fires on no comment, which the substring form did.
    /// </para>
    ///
    /// <para>
    /// Anything other than <c>True</c> is an opt-out, <b>including a value that is not a boolean at
    /// all</b>. Deliberately not <c>bool.TryParse</c>: that returns false on a value it cannot read,
    /// which would skip the element while the guard still reported a pass — the silent-omission
    /// failure this family keeps producing (Qodo round 1 on PR #372, on a different guard in the same
    /// file). Whitespace is trimmed, because XAML trims it too and flagging <c>" True "</c> would be a
    /// false alarm rather than a catch.
    /// </para>
    ///
    /// <para>
    /// No view opens either hatch today, and this exists so that the first one has to be deliberate
    /// and explained rather than quietly absorbed. If a binding genuinely cannot be expressed, narrow
    /// the opt-out to that binding, say why in the markup, and teach this helper to allow exactly it.
    /// </para>
    /// </summary>
    internal static void AssertNoEscapeHatch(string repoRelativeViewPath)
    {
        var root = ViewRoot(repoRelativeViewPath);

        var opted = root.DescendantsAndSelf()
            .Select(element => (element, attribute: element.Attribute(XamlNamespace + "CompileBindings")))
            .Where(pair => pair.attribute is not null
                           && !string.Equals(
                               pair.attribute!.Value.Trim(), "True", StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"<{pair.element.Name.LocalName} x:CompileBindings=\"{pair.attribute!.Value}\">")
            .ToList();

        Assert.True(
            opted.Count == 0,
            $"{repoRelativeViewPath}: {string.Join(", ", opted)} — x:CompileBindings is inherited and "
            + "overridable per subtree, so this puts every binding below it back on reflection while "
            + "the root declaration, the binding count and the build all stay green.");

        // Attribute values and text nodes of the parsed tree, so the word appearing in a comment —
        // and every view here has a comment that discusses these hatches — is not read as a binding.
        var reflection = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes()
                .Select(attribute => (owner: element.Name.LocalName, on: attribute.Name.LocalName, text: attribute.Value))
                .Concat(element.Nodes().OfType<XText>()
                    .Select(node => (owner: element.Name.LocalName, on: "(content)", text: node.Value))))
            .Where(candidate => candidate.text.Contains("{ReflectionBinding", StringComparison.Ordinal))
            .Select(candidate => $"{candidate.owner}.{candidate.on}")
            // …and the OBJECT-ELEMENT spelling, <ReflectionBinding Path="…"/>, which is the same
            // markup extension written as an element and so never appears in an attribute value at
            // all. Measured on MainWindow.axaml: that form on a member that does not exist builds
            // Build succeeded with zero AVLN2000 and passed every guard in the class, so scanning
            // only attributes and text left exactly the hole this PR exists to close, one spelling
            // over. Found by Qodo on this PR.
            .Concat(root.DescendantsAndSelf()
                .Where(element => element.Name.LocalName == "ReflectionBinding")
                .Select(element => $"<ReflectionBinding> under {element.Parent?.Name.LocalName ?? "(root)"}"))
            .ToList();

        Assert.True(
            reflection.Count == 0,
            $"{repoRelativeViewPath}: {reflection.Count} binding(s) use {{ReflectionBinding}} "
            + $"({string.Join(", ", reflection)}), which opts that one binding out of compile checking "
            + "with the root declaration still in place.");
    }

    /// <summary>
    /// Asserts that a dialog's root <c>x:DataType</c> names <paramref name="viewModelType"/>, and that
    /// every place the app presents the dialog hands it an instance constructed as that type.
    ///
    /// <para>
    /// <c>x:DataType</c> is a <i>claim</i>: the compiler checks the bindings against the claim, never
    /// against the object the dialog will actually meet. Nothing in the type system ties the two
    /// together for these dialogs, because they have no <c>DataContext</c> of their own and are handed
    /// one by <c>IDialogService.ShowDialogAsync&lt;T&gt;(object ownerViewModel, object viewModel)</c> —
    /// whose view-model parameter is typed <c>object</c>. A call handing the wrong object compiles,
    /// and so does an <c>x:DataType</c> pointed at a type that shares the member names; either renders
    /// blank at run time, which is the failure #327 exists to remove, arriving one level up.
    /// </para>
    ///
    /// <para>
    /// So this reads the call sites out of <b>every</b> C# file in <c>Daqifi.Avalonia</c> — not only
    /// the file that holds them today — and requires each to pass a local constructed as
    /// <paramref name="viewModelType"/> in a scope that encloses the call — a same-named local in another
    /// method does not count (see <see cref="BlockStillOpen"/>). The dialog's class name is taken from its own
    /// <c>x:Class</c>, so the call-site search cannot drift from the view it is guarding. A call in a
    /// shape the parser does not follow (a named argument, an inline <c>new</c>, a property result)
    /// <b>fails</b> rather than being skipped, and so does a direct <c>new Dialog(</c>, which would
    /// present the dialog by a route this guard cannot see: silently checking the subset it happened
    /// to understand is the defect Qodo found in the first version of this guard (round 1 on PR #372).
    /// Deliberately not a Roslyn semantic model — the test project references no compiler API, and
    /// failing loudly on an unknown shape gives the same protection for far less machinery.
    /// </para>
    ///
    /// <para>
    /// Read off the source rather than by constructing anything: these view models resolve
    /// <c>App.ServiceProvider</c> and their hosts open the application database, configuration and
    /// logs — under a test run, the developer's real <c>~/Library/Application Support/DAQiFi</c>.
    /// </para>
    /// </summary>
    internal static void AssertDialogIsHandedItsDeclaredViewModel(string repoRelativeViewPath, Type viewModelType)
    {
        var root = ViewRoot(repoRelativeViewPath);

        var dialogClass = root.Attribute(XamlNamespace + "Class")?.Value;
        Assert.True(dialogClass is not null, $"{repoRelativeViewPath}: the root element declares no x:Class.");
        var dialog = dialogClass!.Split('.')[^1];

        // x:DataType="prefix:Name", and xmlns:prefix must name the view model's namespace. Avalonia
        // spells a CLR-namespace prefix either way; both are in use in this checkout.
        var declared = root.Attribute(XamlNamespace + "DataType")?.Value;
        Assert.True(declared is not null, $"{repoRelativeViewPath}: the root element declares no x:DataType.");
        var parts = declared!.Split(':');
        Assert.True(
            parts.Length == 2 && parts[1] == viewModelType.Name,
            $"{repoRelativeViewPath}: x:DataType is \"{declared}\", which does not name {viewModelType.Name}.");

        var prefix = root.Attribute(XNamespace.Xmlns + parts[0])?.Value;
        Assert.True(
            prefix == $"using:{viewModelType.Namespace}" || prefix == $"clr-namespace:{viewModelType.Namespace}",
            $"{repoRelativeViewPath}: xmlns:{parts[0]} is {prefix ?? "absent"}, so x:DataType=\"{declared}\" "
            + $"does not name {viewModelType.FullName}.");

        var sources = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "Daqifi.Avalonia"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var escaped = Regex.Escape(dialog);
        var mentions = 0;
        var understood = 0;
        var unsupported = new List<string>();

        foreach (var path in sources)
        {
            var source = File.ReadAllText(path);
            var file = Path.GetFileName(path);

            Assert.False(
                Regex.IsMatch(source, $@"\bnew\s+{escaped}\s*[({{]"),
                $"{file}: constructs {dialog} directly. This guard follows ShowDialogAsync<{dialog}> call "
                + "sites only, so a dialog presented another way is handed a DataContext nothing here "
                + $"checks against x:DataType=\"{declared}\". Teach this helper the new route.");

            var present = Regex.Matches(source, $@"ShowDialogAsync\s*<\s*{escaped}\s*>");
            if (present.Count == 0) { continue; }

            mentions += present.Count;

            var parsed = Regex.Matches(
                source,
                $@"ShowDialogAsync\s*<\s*{escaped}\s*>\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*(?<arg>[A-Za-z_][A-Za-z0-9_]*)\s*\)");
            understood += parsed.Count;

            foreach (Match site in parsed)
            {
                var local = site.Groups["arg"].Value;

                // The constructing declaration has to be the one the argument REFERS TO, not merely one
                // with the same spelling somewhere in the file (Qodo, shepherd round on PR #376):
                // DaqifiViewModel.cs declares `exportDialogViewModel` in two methods, so a call handing
                // some other object under that name in one method passed on the other's construction.
                // Only declarations that precede the call and whose block still encloses it count.
                var constructed = Regex.Matches(
                        source[..site.Index],
                        $@"\b(var|{Regex.Escape(viewModelType.Name)})\s+{Regex.Escape(local)}\s*=\s*new\s+{Regex.Escape(viewModelType.Name)}\s*\(")
                    .Any(declaration => BlockStillOpen(source, declaration.Index, site.Index));

                Assert.True(
                    constructed,
                    $"{file}: ShowDialogAsync<{dialog}> is handed '{local}', which is not a local constructed "
                    + $"as a {viewModelType.Name} in a scope enclosing that call. That parameter is typed "
                    + $"object, so the compiler accepts anything, and {repoRelativeViewPath} claims "
                    + $"x:DataType=\"{declared}\" — the dialog would compile and render blank.");
            }

            if (parsed.Count < present.Count)
            {
                unsupported.Add($"{file} ({present.Count - parsed.Count})");
            }
        }

        Assert.True(
            mentions > 0,
            $"no ShowDialogAsync<{dialog}> call site exists anywhere in Daqifi.Avalonia. If the dialog is "
            + "now presented some other way, this guard has to follow it — the x:DataType claim is "
            + "unchecked until something pins it to the object the dialog is given.");

        Assert.True(
            unsupported.Count == 0,
            $"ShowDialogAsync<{dialog}> is called in {mentions} place(s) but only {understood} are in the "
            + $"(owner, local) shape this guard can follow: {string.Join(", ", unsupported)}. An "
            + "unrecognised call is NOT covered, and silently skipping it is the failure this assertion "
            + "exists to prevent.");
    }

    /// <summary>
    /// True when the block holding the text at <paramref name="from"/> is still open at
    /// <paramref name="to"/>: walking forward, brace depth never drops below where it started. A local
    /// declared at <paramref name="from"/> is then in scope at <paramref name="to"/>, and since C# forbids
    /// redeclaring a local's name inside its own scope, the identifier there refers to it. A declaration
    /// in another method closes its block first and fails. Braces are counted raw: interpolated strings
    /// balance, and a stray brace in a literal or comment can only make this stricter unless it exactly
    /// cancels a real one. The one shadowing it cannot see is a lambda or local-function parameter
    /// reusing the name, which no call site in this checkout does.
    /// </summary>
    private static bool BlockStillOpen(string source, int from, int to)
    {
        var depth = 0;
        for (var i = from; i < to; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth < 0) { return false; }
        }

        return true;
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
    /// The every-template half covers what a per-list check cannot: a list naming the three templates
    /// that exist today would not notice a fourth added later with no <c>x:DataType</c> of its own.
    /// </para>
    ///
    /// <para>
    /// <b>What an undeclared template actually does</b> (issue #336; the earlier wording here said it
    /// resolves against an inherited scope, which is not what happens). Measured on Avalonia 12.1
    /// with <c>x:CompileBindings="True"</c> on the root, a <c>DataTemplate</c>'s bindings resolve
    /// against, in order: its own <c>x:DataType</c>, which <b>overrides</b> everything below it; else
    /// a scope <b>inferred</b> from the <c>ItemsSource</c> beside it, where the shape supports that —
    /// <c>ItemsControl</c>/<c>ListBox.ItemTemplate</c> and <c>DataGridTemplateColumn.CellTemplate</c>
    /// do; else <b>nothing at all</b>, <c>XamlX.TypeSystem.XamlPseudoType</c>, on which no member
    /// resolves, so every binding in the template is a hard <c>AVLN2000</c> even with correct member
    /// names. It never falls back to the ancestor or view-model scope.
    /// </para>
    ///
    /// <para>
    /// Two consequences worth carrying to the views this is still being spread across. First,
    /// omitting <c>x:DataType</c> is never <i>silently</i> unchecked: the no-scope case is a build
    /// error, so an undeclared template either infers the right type or fails the build. Second, and
    /// the reason not to add declarations by reflex, an explicit <c>x:DataType</c> is an override, so
    /// a plausible-but-wrong one is <b>worse than none</b> — it replaces a correct inferred scope
    /// with a wrong one, and compiles whenever the two types happen to share the member names, which
    /// is the reads-as-protection failure these guards exist to remove. Declare a scope where the
    /// build asks for one (<c>ContentControl.ContentTemplate</c> and resource-dictionary templates
    /// are the shapes that infer nothing), or where the template needs to narrow a wider item type.
    /// </para>
    ///
    /// <para>
    /// This overload therefore states a stricter rule than the general one: it is for views whose
    /// templates are all expected to be declared. Where a view's templates are correctly left
    /// undeclared, guard that each one either declares a scope or binds nothing, rather than
    /// requiring the attribute.
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
            + "x:DataType, so their bindings resolve against a scope inferred from the surrounding "
            + "ItemsSource, or — where the shape supports no such inference — against nothing at "
            + "all (XamlPseudoType, a build error). Not against an inherited scope.");

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
    internal static string RepoRoot()
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
