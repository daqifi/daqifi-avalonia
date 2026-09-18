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
    /// <c>&lt;ReflectionBinding/&gt;</c> object element, each with or without a namespace prefix and
    /// with or without the class name's <c>Extension</c> suffix. Any of them puts markup back on reflection
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
            .Where(candidate => ReflectionBindingExtensionUse.IsMatch(candidate.text))
            .Select(candidate => $"{candidate.owner}.{candidate.on}")
            // …and the OBJECT-ELEMENT spelling, <ReflectionBinding Path="…"/>, which is the same
            // markup extension written as an element and so never appears in an attribute value at
            // all. Measured on MainWindow.axaml: that form on a member that does not exist builds
            // Build succeeded with zero AVLN2000 and passed every guard in the class, so scanning
            // only attributes and text left exactly the hole this PR exists to close, one spelling
            // over. Found by Qodo on PR #375. Matched on the local name, so a prefix cannot hide it,
            // and with the class name's own Extension suffix too — see ReflectionBindingNames.
            .Concat(root.DescendantsAndSelf()
                .Where(element => ReflectionBindingNames.Contains(element.Name.LocalName))
                .Select(element => $"<{element.Name.LocalName}> under {element.Parent?.Name.LocalName ?? "(root)"}"))
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
    /// together, because the dialog is handed its view model through a parameter typed <c>object</c>
    /// — <c>IDialogService.ShowDialogAsync&lt;T&gt;(object ownerViewModel, object viewModel)</c>, or
    /// <c>Window.DataContext</c> after a direct construction. Handing the wrong object compiles, and so
    /// does an <c>x:DataType</c> pointed at a type that shares the member names; either renders blank
    /// at run time, which is the failure #327 exists to remove, arriving one level up.
    /// </para>
    ///
    /// <para>
    /// So this reads <b>every</b> C# file in <c>Daqifi.Avalonia</c> for the two routes the app uses —
    /// <c>ShowDialogAsync&lt;Dialog&gt;(owner, local)</c>, and
    /// <c>var d = new Dialog(); d.DataContext = local;</c> — and requires <c>local</c> to be a
    /// <c>var</c> constructed as <paramref name="viewModelType"/> in a block that encloses the use (see
    /// <see cref="BlockStillOpen"/>), and never named again in that block except to reach a member.
    /// <c>var</c> only, because a field cannot be one: an initialized field reads as an enclosing
    /// declaration and any method can reassign it (Qodo round 1 on PR #378). The "never named again"
    /// rule is what an assignment, compound assignment, tuple element or <c>ref</c>/<c>out</c> write
    /// has in common, so it fails all of them without telling them apart (Qodo rounds 3–4 on #378),
    /// and it also fails a closer same-named declaration that would shadow the construction.
    /// </para>
    ///
    /// <para>
    /// The dialog's class name is taken from its own <c>x:Class</c>, and matched bare, qualified or
    /// <c>global::</c>-qualified. What <i>counts</i> as a use — either route, any construction
    /// including the target-typed <c>Dialog? d = new()</c>, and any <c>using</c> alias for the dialog —
    /// is read off the raw text, so one inside a comment, string or interpolation hole counts and then
    /// fails as unparsed rather than vanishing (Qodo, third shepherd round on PR #376). What
    /// <i>satisfies</i> the guard is read off <see cref="CodeOnly"/>, so a commented-out construction
    /// cannot. A use in any other shape (a named argument, an inline <c>new</c>, an object initializer)
    /// <b>fails</b>: silently checking the subset it understood is the defect Qodo found in the first
    /// version of this guard (round 1 on PR #372). Deliberately not a Roslyn semantic model — the test
    /// project references no compiler API, and failing loudly on an unknown shape gives the same
    /// protection for far less machinery. Read off the source rather than by constructing anything:
    /// these view models' hosts open the application database, configuration and logs.
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

        const string id = @"[A-Za-z_][A-Za-z0-9_]*";
        var type = $@"(?:global::)?(?:{id}\.)*{Regex.Escape(dialog)}";
        var viewModel = $@"(?:global::)?(?:{id}\.)*{Regex.Escape(viewModelType.Name)}";

        // Counted on raw text: every call, every construction (`new T(`, `new T {`, `T x = new(`,
        // `T? x = new(`), every alias. Understood on code only: the two routes, in exactly one shape each.
        var counted = new Regex(
            $@"ShowDialogAsync\s*<\s*{type}\s*>|\bnew\s+{type}\s*[({{]|(?<![\w.]){type}\s*\??\s+{id}\s*=\s*new\s*\("
            + $@"|\busing\s+{id}\s*=\s*{type}\s*;");
        var understood = new Regex(
            $@"ShowDialogAsync\s*<\s*{type}\s*>\(\s*{id}\s*,\s*(?<arg>{id})\s*\)"
            + $@"|\bvar\s+(?<d>{id})\s*=\s*new\s+{type}\s*\(\s*\)\s*;\s*\k<d>\s*\.\s*DataContext\s*=\s*(?<arg>{id})\s*;");

        var sources = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "Daqifi.Avalonia"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var mentions = 0;
        var unsupported = new List<string>();

        foreach (var path in sources)
        {
            var raw = File.ReadAllText(path);
            var present = counted.Matches(raw).Count;
            if (present == 0) { continue; }

            mentions += present;
            var source = CodeOnly(raw);
            var file = Path.GetFileName(path);
            var parsed = understood.Matches(source);

            foreach (Match site in parsed)
            {
                var arg = site.Groups["arg"];
                var name = Regex.Escape(arg.Value);

                // The construction the argument refers to: a `var` whose block still encloses the use,
                // with no conditional-compilation directive between the two — under `#if false` a
                // construction is text the compiler never sees, and telling an active branch from an
                // inactive one needs the build's symbols, so that fails closed.
                var construction = Regex.Matches(source[..arg.Index], $@"\bvar\s+{name}\s*=\s*new\s+{viewModel}\s*\(")
                    .LastOrDefault(declaration => BlockStillOpen(source, declaration.Index, arg.Index)
                                                  && !ConditionalDirective.IsMatch(source[declaration.Index..arg.Index]));

                // …and nothing else in that block names it except to reach a member. Scanned on the raw
                // text to the block's end, so a write inside an interpolation hole, or in a loop body
                // after the use, is seen too.
                var others = construction is null
                    ? 0
                    : Regex.Matches(raw[..BlockEnd(source, construction.Index)], $@"(?<![\w.]){name}(?!\w)(?!\s*\??\.(?!\.))")
                        .Count(use => use.Index >= construction.Index + construction.Length && use.Index != arg.Index);

                Assert.True(
                    construction is not null && others == 0,
                    $"{file}: {dialog} is handed '{arg.Value}', which is not a `var` constructed as a "
                    + $"{viewModelType.Name} in a block enclosing that use and left alone after"
                    + (construction is null ? "" : $" (named {others} more time(s) other than to reach a member)")
                    + $". That parameter is typed object, so the compiler accepts anything, and "
                    + $"{repoRelativeViewPath} claims x:DataType=\"{declared}\" — the dialog would compile and render blank.");
            }

            if (parsed.Count < present)
            {
                unsupported.Add($"{file} ({present - parsed.Count})");
            }
        }

        Assert.True(
            mentions > 0,
            $"{dialog} is presented nowhere in Daqifi.Avalonia. If it is now presented some other way, this "
            + "guard has to follow it — the x:DataType claim is unchecked until something pins it to the "
            + "object the dialog is given.");

        Assert.True(
            unsupported.Count == 0,
            $"{dialog} is named in {mentions} place(s) that present or construct it, and not all are in a "
            + $"shape this guard can follow: {string.Join(", ", unsupported)}. An unrecognised use is NOT "
            + "covered, and silently skipping it is the failure this assertion exists to prevent. One "
            + "inside a comment, string or interpolation hole counts here and is never parsed, so it fails "
            + "too; so does a `using` alias for the dialog.");
    }

    /// <summary>The index of the <c>}</c> that closes the block holding <paramref name="from"/>, in
    /// <see cref="CodeOnly"/> text; the end of the source if it never closes.</summary>
    private static int BlockEnd(string source, int from)
    {
        var depth = 0;
        for (var i = from; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth < 0) { return i; }
        }

        return source.Length;
    }

    /// <summary>A conditional-compilation directive at the start of a line.</summary>
    private static readonly Regex ConditionalDirective =
        new(@"(?m)^[ \t]*#[ \t]*(if|elif|else|endif)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the block holding the text at <paramref name="from"/> is still open at
    /// <paramref name="to"/>: walking forward, brace depth never drops below where it started. A local
    /// declared at <paramref name="from"/> is then in scope at <paramref name="to"/>, and since C# forbids
    /// redeclaring a local's name inside its own scope, the identifier there refers to it. A declaration
    /// in another method closes its block first and fails. <paramref name="source"/> must already be
    /// <see cref="CodeOnly"/>: a raw <c>/* { */</c> after the declaration would otherwise hold the depth
    /// up across the method's real closing brace and let a same-named local in the next method through
    /// (Qodo, second shepherd round on PR #376). The one shadowing it cannot see is a lambda or
    /// local-function parameter reusing the name, which no call site in this checkout does.
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
    /// The C# source with every comment and every string or character literal overwritten by spaces
    /// (newlines kept), so the result has the same length and offsets but only code is left to match.
    /// Without this, a brace in a comment or literal throws off <see cref="BlockStillOpen"/>, and a
    /// commented-out <c>// var vm = new ViewModel(…)</c> reads as the construction a call relies on.
    /// Interpolated strings are blanked including their holes, and the lexer follows nested literals
    /// inside a hole so that a quote in there does not end the outer string early. Raw
    /// (<c>"""</c>) strings are blanked whole. Because a hole is executable code, callers use this
    /// only for what may <i>satisfy</i> a guard, and count what must be checked on the raw text.
    /// </summary>
    internal static string CodeOnly(string source)
    {
        var code = source.ToCharArray();
        var i = 0;
        while (i < source.Length)
        {
            var end = NonCodeEnd(source, i);
            if (end == i) { i++; continue; }

            for (var k = i; k < end; k++)
            {
                if (code[k] != '\n' && code[k] != '\r') { code[k] = ' '; }
            }

            i = end;
        }

        return new string(code);
    }

    /// <summary>
    /// If a comment or a string/character literal starts at <paramref name="i"/>, the index just past
    /// its end (the end of the source if it never closes); otherwise <paramref name="i"/> itself.
    /// </summary>
    private static int NonCodeEnd(string s, int i)
    {
        if (string.CompareOrdinal(s, i, "//", 0, 2) == 0)
        {
            // A lone \r ends a line in C# too; ending only at \n would erase the rest of such a file.
            var newline = s.IndexOfAny(['\r', '\n'], i);
            return newline < 0 ? s.Length : newline;
        }

        if (string.CompareOrdinal(s, i, "/*", 0, 2) == 0)
        {
            var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + 2;
        }

        if (s[i] == '\'')
        {
            var k = i + 1;
            while (k < s.Length && s[k] != '\'' && s[k] != '\n' && s[k] != '\r') { k += s[k] == '\\' ? 2 : 1; }
            return Math.Min(k + 1, s.Length);
        }

        // String prefixes: any run of $ and @ (interpolated, verbatim, both, raw-interpolated).
        var j = i;
        var interpolated = false;
        var verbatim = false;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '$') { interpolated = true; } else { verbatim = true; }
            j++;
        }

        if (j >= s.Length || s[j] != '"') { return i; }

        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"') { quotes++; }
        if (quotes >= 3 && !verbatim)
        {
            var closing = s.IndexOf(new string('"', quotes), j + quotes, StringComparison.Ordinal);
            return closing < 0 ? s.Length : closing + quotes;
        }

        var p = j + 1;
        var hole = 0;
        while (p < s.Length)
        {
            if (hole > 0)
            {
                var nested = NonCodeEnd(s, p);
                if (nested > p) { p = nested; continue; }
                if (s[p] == '{') { hole++; }
                else if (s[p] == '}') { hole--; }
                p++;
                continue;
            }

            var c = s[p];
            if (!verbatim && c == '\\') { p += 2; continue; }
            if (c == '"')
            {
                if (verbatim && p + 1 < s.Length && s[p + 1] == '"') { p += 2; continue; }
                return p + 1;
            }

            if (interpolated && c == '{')
            {
                if (p + 1 < s.Length && s[p + 1] == '{') { p += 2; continue; }
                hole = 1;
            }

            p++;
        }

        return s.Length;
    }

    /// <summary>
    /// Both names XAML resolves to Avalonia's <c>ReflectionBindingExtension</c>: the short form, and
    /// the class name itself, because a XAML type lookup tries <c>Name</c> and <c>NameExtension</c>.
    /// Measured on <c>SummaryFlyout.axaml</c> (Qodo round 2 on PR #370): the object element
    /// <c>&lt;ReflectionBindingExtension Path="…"/&gt;</c> on a member that does not exist built with
    /// zero <c>AVLN2000</c> and passed every guard in that class, while the same element spelled
    /// <c>&lt;CompiledBindingExtension/&gt;</c> is <c>AVLN2000</c> — so the suffixed name really is
    /// resolved, and matching only <c>ReflectionBinding</c> was a hole one spelling wide.
    /// </summary>
    private static readonly HashSet<string> ReflectionBindingNames =
        new(StringComparer.Ordinal) { "ReflectionBinding", "ReflectionBindingExtension" };

    /// <summary>
    /// A <c>{ReflectionBinding …}</c> markup extension anywhere in an attribute value or text node,
    /// nested or not: the opening brace, optional whitespace, an optional namespace prefix, then
    /// either name in <see cref="ReflectionBindingNames"/> ending at a word boundary.
    ///
    /// <para>
    /// The prefix is the reason this is not a substring search. Avalonia's namespace can be bound to
    /// any alias, and <c>{av:ReflectionBinding …}</c> contains no <c>{ReflectionBinding</c>. Measured on
    /// <c>SummaryFlyout.axaml</c> (Qodo round 2 on PR #370): with
    /// <c>xmlns:av="https://github.com/avaloniaui"</c> on the root, <c>{av:ReflectionBinding …}</c>
    /// on a member that does not exist built with zero <c>AVLN2000</c> and every guard in the class
    /// passed; the same prefix on <c>{av:Binding …}</c> is <c>AVLN2000</c>, so the alias really is
    /// resolved. Any prefix is flagged, not only one bound to Avalonia's namespace — a false alarm on
    /// a same-named type from some other library is the cheaper failure, and fails loudly.
    /// </para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ReflectionBindingExtensionUse =
        new(@"\{\s*(?:[A-Za-z_][\w.\-]*\s*:\s*)?ReflectionBinding(?:Extension)?\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
