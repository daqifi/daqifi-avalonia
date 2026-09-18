using System.Text.RegularExpressions;
using System.Xml.Linq;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>DuplicateDeviceDialog.axaml</c>'s 3 bindings the XAML compiler's business
/// rather than nobody's (issue #327), and the two things compiled bindings still cannot see in this
/// dialog.
///
/// <para>
/// The dialog declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, and
/// <c>Daqifi.Avalonia.csproj</c> sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
/// all three bindings resolved by reflection at run time. Measured on <c>origin/main</c> in a
/// <b>single</b> build carrying two defects at once: a bogus CLR property on the message
/// <c>TextBlock</c> was <c>AVLN2000 … ZzzBogusProperty on type … TextBlock</c> naming this file and
/// its line — which proves the file was compiled — while <c>{Binding MessageQQQ}</c>, a member that
/// does not exist, on the same element in the same compilation produced no diagnostic at all. The
/// same one-build pair on this branch is two <c>AVLN2000</c>s, the second naming
/// <c>Daqifi.Desktop.ViewModels.DuplicateDeviceDialogViewModel</c>; and a typo in each of the two
/// <c>RadioButton</c> labels, each applied alone, is one <c>AVLN2000</c> against that type. Member
/// names are therefore not asserted here: the compiler owns all three.
/// </para>
///
/// <para>
/// The view contains no <c>DataTemplate</c>, so it has exactly one binding scope.
/// </para>
/// </summary>
public class DuplicateDeviceDialogBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/DuplicateDeviceDialog.axaml";

    /// <summary>The XAML language namespace, where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Delete
    /// <c>x:CompileBindings</c> and the three bindings go back on reflection with every head and every
    /// other test still green — <c>x:DataType</c> alone changes nothing while
    /// <c>AvaloniaUseCompiledBindingsByDefault</c> is <c>false</c> (#326). Parsed, not searched: the
    /// comment in the view spells both attributes out.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DuplicateDeviceDialogViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_dialog_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Gap 2, the one compiled bindings cannot close: <c>x:DataType</c> is a <i>claim</i>, checked
    /// against itself rather than against the object the dialog will meet. The dialog is handed its
    /// <c>DataContext</c> by assignment — <c>Window.DataContext</c> is typed <c>object</c> — so the
    /// compiler would accept any object there, and a type sharing these three member names would
    /// compile and render a dialog with no message and two unlabelled choices.
    ///
    /// <para>
    /// So this reads every C# file in the app project for places that construct the dialog — by its
    /// bare or qualified name, with any <c>using</c> alias failing outright — and requires each one to
    /// assign its <c>DataContext</c> a local whose in-scope declaration constructs a
    /// <see cref="DuplicateDeviceDialogViewModel"/>. Read off the source rather than by running it:
    /// the one call site today, <c>ConnectionDialogViewModel.HandleDuplicateDevice</c>, needs a live
    /// desktop lifetime to reach the assignment. A construction written in any shape this test cannot
    /// follow <b>fails</b> rather than being skipped, so the count below cannot report coverage it
    /// does not have.
    /// </para>
    /// </summary>
    [Fact]
    public void The_declared_scope_is_the_view_model_the_dialog_is_actually_handed()
    {
        var root = Root();

        var prefix = root.Attribute(XNamespace.Xmlns + "vm")?.Value;
        Assert.True(
            prefix == $"using:{typeof(DuplicateDeviceDialogViewModel).Namespace}"
            || prefix == $"clr-namespace:{typeof(DuplicateDeviceDialogViewModel).Namespace}",
            $"{View}: xmlns:vm is {prefix ?? "absent"}, so the x:DataType below does not name "
            + $"{typeof(DuplicateDeviceDialogViewModel).FullName}.");
        Assert.Equal($"vm:{nameof(DuplicateDeviceDialogViewModel)}", root.Attribute(Xaml + "DataType")?.Value);

        var sources = Directory
            .EnumerateFiles(
                Path.Combine(BindingFacts.RepoRoot(), "Daqifi.Avalonia"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // A type name as C# lets you write it: bare, namespace-qualified, or global::-qualified. Only
        // matching the bare name let `new Daqifi.Desktop.View.DuplicateDeviceDialog()` walk past the
        // count while the existing bare site kept it positive (Qodo round 1).
        const string dialogType = @"(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*DuplicateDeviceDialog";
        const string identifier = @"[A-Za-z_][A-Za-z0-9_]*";

        // Every way C# 12 can construct the dialog: `new T(`/`new T {` and the target-typed
        // `T x = new(`, which a search for the type name after `new` cannot see.
        var construction = new Regex(
            // `\??` because `DuplicateDeviceDialog? d = new()` is the same construction (Qodo round 2).
            $@"\bnew\s+{dialogType}\s*[({{]|(?<![\w.]){dialogType}\s*\??\s+{identifier}\s*=\s*new\s*\(");
        // A `using` alias can name the dialog without either spelling above; this guard does not
        // follow aliases, so one FAILS rather than hiding a construction written through it.
        var alias = new Regex($@"\busing\s+{identifier}\s*=\s*{dialogType}\s*;");
        // The one shape this guard understands: a `var` local, then its DataContext assigned a local.
        var understoodShape = new Regex(
            $@"\bvar\s+(?<dialog>{identifier})\s*=\s*new\s+{dialogType}\s*\(\s*\)\s*;"
            + $@"\s*(?<dialog2>{identifier})\.DataContext\s*=\s*(?<vm>{identifier})\s*;");

        var constructions = 0;
        var understood = 0;
        var unsupported = new List<string>();

        foreach (var path in sources)
        {
            // Code only: comments and string/char literals blanked, so neither a brace inside a
            // string nor a construction quoted in a comment is read as code.
            var source = CodeOnly(File.ReadAllText(path));

            var aliases = alias.Matches(source).Count;
            if (aliases > 0)
            {
                unsupported.Add($"{Path.GetFileName(path)} ({aliases} using alias(es) for the dialog)");
            }

            var present = construction.Matches(source).Count;
            if (present == 0) { continue; }

            constructions += present;
            var parsed = understoodShape.Matches(source)
                .Where(site => site.Groups["dialog"].Value == site.Groups["dialog2"].Value)
                .ToList();
            understood += parsed.Count;

            foreach (var site in parsed)
            {
                var local = site.Groups["vm"].Value;
                var problem = LocalIsFreshViewModel(source, local, site.Groups["dialog2"].Index);

                Assert.True(
                    problem is null,
                    $"{Path.GetFileName(path)}: DuplicateDeviceDialog.DataContext is assigned '{local}', and "
                    + $"{problem}. DataContext is typed object, so the compiler accepts anything, and {View} "
                    + $"claims x:DataType=\"vm:{nameof(DuplicateDeviceDialogViewModel)}\" — the dialog would "
                    + "compile and render blank.");
            }

            if (parsed.Count < present)
            {
                unsupported.Add($"{Path.GetFileName(path)} ({present - parsed.Count})");
            }
        }

        Assert.True(
            constructions > 0,
            "DuplicateDeviceDialog is constructed nowhere in Daqifi.Avalonia. If it is now created some "
            + "other way, this guard has to follow it — the x:DataType claim is unchecked until something "
            + "pins it to the object the dialog is given.");

        Assert.True(
            unsupported.Count == 0,
            $"DuplicateDeviceDialog is constructed in {constructions} place(s), {understood} of them in "
            + "the `var d = new DuplicateDeviceDialog(); d.DataContext = local;` shape this guard can "
            + $"follow; not followed: {string.Join(", ", unsupported)}. An unrecognised construction is NOT covered, and "
            + "skipping it silently is the failure this assertion exists to prevent — teach this test the "
            + "shape, or write it as a local.");
    }

    /// <summary>
    /// Resolves <paramref name="local"/> as used at <paramref name="assignmentAt"/> to the declaration
    /// that is actually in scope there, and returns why it is not a freshly constructed
    /// <see cref="DuplicateDeviceDialogViewModel"/> — or null when it is.
    ///
    /// <para>
    /// Deliberately scoped rather than file-wide. Matching the name anywhere in the file let
    /// <c>object duplicateDialogViewModel = this;</c> in one method borrow the correct declaration of
    /// the same name in another, and pass (Qodo round 1). So: take the <b>nearest</b> preceding
    /// declaration whose braces have not closed by the assignment — C# forbids the same name being
    /// redeclared in a nested scope, so that is the one the compiler binds to — then require that
    /// nothing else in the file writes that name: no reassignment, no <c>ref</c>/<c>out</c>, no second
    /// declaration. File-wide rather than between declaration and use, because an initialized field
    /// also matches as a declaration here and any method can reassign a field.
    /// </para>
    ///
    /// <para>
    /// <c>this</c>, a parameter, a property or a pattern variable is not followed and so reads as "not
    /// declared in scope", which fails. Braces are counted over <see cref="CodeOnly"/> text: counted raw, a lone
    /// <c>"{"</c> inside an inner block cancelled that block's closing brace, so a closed scope read
    /// as open and its declaration was borrowed by an assignment outside it (Qodo round 2 — the
    /// earlier claim here that a stray brace could only fail was wrong).
    /// </para>
    /// </summary>
    private static string? LocalIsFreshViewModel(string source, string local, int assignmentAt)
    {
        const string identifier = @"[A-Za-z_][A-Za-z0-9_]*";
        const string viewModelType = @"(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*" + nameof(DuplicateDeviceDialogViewModel);
        var name = Regex.Escape(local);

        var declarations = Regex.Matches(
                source,
                $@"(?<![\w.])(?<type>(?:global::)?{identifier}(?:\.{identifier})*(?:<[^;=()]*>)?\??)\s+{name}\s*=(?!=)\s*(?<init>[^;]*);")
            .Where(declaration => declaration.Index < assignmentAt && StillInScope(source, declaration.Index, assignmentAt))
            .ToList();

        if (declarations.Count == 0)
        {
            return "no declaration of it with an initializer is in scope there — `this`, parameters, "
                   + "properties and pattern variables are not followed by this guard";
        }

        var nearest = declarations[^1];
        var type = nearest.Groups["type"].Value;
        var init = nearest.Groups["init"].Value.Trim();
        if ((type != "var" && !Regex.IsMatch(type, $"^{viewModelType}$"))
            || !Regex.IsMatch(init, $@"^new\s+{viewModelType}\s*\("))
        {
            return $"the declaration in scope is `{type} {local} = {init}`, not a new "
                   + nameof(DuplicateDeviceDialogViewModel);
        }

        // Anywhere in the file, not just between the declaration and the use: an initialized FIELD
        // also reads as an in-scope declaration here, and a field can be reassigned by any method,
        // before or after this one in the text. A same-named local elsewhere trips this too; that is
        // the failing direction, and renaming one of them clears it.
        var writes = Writes(source, local)
            .Count(at => at < nearest.Index || at >= nearest.Index + nearest.Length);
        if (writes > 0)
        {
            return $"the name is written {writes} more time(s) in the file — reassigned, passed by ref/out, "
                   + "or declared again — so its declaration alone does not say what the dialog receives";
        }

        return null;
    }

    /// <summary>
    /// Offsets at which <paramref name="local"/> is written: the target of <c>=</c> or any compound
    /// assignment, an element of a tuple — nested or not — on the left of <c>=</c> (Qodo round 3:
    /// <c>(local, other) = (null!, 1)</c> was not seen), or an argument passed by <c>ref</c>/<c>out</c>.
    /// A declaration is a write too, which is how a second declaration of the name is caught.
    /// </summary>
    private static IEnumerable<int> Writes(string code, string local)
    {
        foreach (Match use in Regex.Matches(code, $@"(?<![\w.]){Regex.Escape(local)}(?!\w)"))
        {
            if (Regex.IsMatch(code[Math.Max(0, use.Index - 40)..use.Index], @"\b(?:ref|out)\s+(?:var\s+)?$"))
            {
                yield return use.Index;
                continue;
            }

            // Walk right over what may follow a tuple element: more elements, commas and closing
            // parentheses. After a ')', only ',', ')' or the '=' may come next — so `if (a == x) y = 1`
            // is not read as a tuple write of x.
            var i = use.Index + use.Length;
            var closed = 0;
            var depth = 0;
            var afterClose = false;
            while (i < code.Length)
            {
                var c = code[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (afterClose && c is not (',' or ')' or '=')) { break; }
                if (c == ')')
                {
                    if (depth > 0) { depth--; } else { closed++; }
                    afterClose = true;
                    i++;
                }
                else if (c == '(') { depth++; afterClose = false; i++; }
                else if (c == ',') { afterClose = false; i++; }
                else if ((char.IsLetterOrDigit(c) || c == '_') && !afterClose) { i++; }
                else { break; }
            }

            var rest = code[i..Math.Min(code.Length, i + 4)];
            var assigns = Regex.IsMatch(rest, @"^(?:=(?![=>])|(?:\?\?|<<|>>|[-+*/%&|^])=)");
            if (assigns && (closed > 0 || i == use.Index + use.Length || code[(use.Index + use.Length)..i].Trim().Length == 0))
            {
                yield return use.Index;
            }
        }
    }

    /// <summary>
    /// The source with every comment and string/char literal overwritten by spaces (newlines kept),
    /// so offsets are unchanged and only code is left to match against. Interpolation holes stay
    /// code — they are code — in regular, verbatim and raw (<c>"""</c>) literals alike, and string
    /// literals nested inside them are blanked in turn.
    /// </summary>
    private static string CodeOnly(string source)
    {
        var text = source.ToCharArray();
        var i = 0;

        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < text.Length; k++)
            {
                if (text[k] != '\n') { text[k] = ' '; }
            }
        }

        char At(int k) => k < source.Length ? source[k] : '\0';

        // Scans code from i. With stopAtClose, returns at the '}' that closes an interpolation hole.
        void Code(bool stopAtClose)
        {
            var depth = 0;
            while (i < source.Length)
            {
                var c = source[i];
                if (c == '/' && At(i + 1) == '/')
                {
                    var end = source.IndexOf('\n', i);
                    end = end < 0 ? source.Length : end;
                    Blank(i, end);
                    i = end;
                }
                else if (c == '/' && At(i + 1) == '*')
                {
                    var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? source.Length : end + 2;
                    Blank(i, end);
                    i = end;
                }
                else if (c == '\'')
                {
                    var start = i++;
                    while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                    {
                        i += source[i] == '\\' ? 2 : 1;
                    }
                    i++;
                    Blank(start, i);
                }
                else if (c == '"' || ((c == '$' || c == '@') && (At(i + 1) == '"' || At(i + 2) == '"')))
                {
                    var start = i;
                    var interpolated = false;
                    var verbatim = false;
                    while (At(i) is '$' or '@')
                    {
                        interpolated |= source[i] == '$';
                        verbatim |= source[i] == '@';
                        i++;
                    }

                    if (At(i) != '"')
                    {
                        // `@identifier` or similar, not a literal prefix: it is code.
                        i = start + 1;
                    }
                    else if (At(i) == '"' && At(i + 1) == '"' && At(i + 2) == '"')
                    {
                        var quotes = 0;
                        while (At(i) == '"') { quotes++; i++; }
                        Blank(start, i);
                        RawLiteral(quotes, dollars: i - quotes - start);
                    }
                    else
                    {
                        Blank(start, ++i);
                        Literal(interpolated, verbatim);
                    }
                }
                else if (c == '{')
                {
                    depth++;
                    i++;
                }
                else if (c == '}')
                {
                    if (stopAtClose && depth == 0) { return; }
                    depth--;
                    i++;
                }
                else
                {
                    i++;
                }
            }
        }

        // Scans a regular or verbatim literal body from just after its opening quote.
        void Literal(bool interpolated, bool verbatim)
        {
            while (i < source.Length)
            {
                var c = source[i];
                if (!verbatim && c == '\\') { Blank(i, i + 2); i += 2; }
                else if (verbatim && c == '"' && At(i + 1) == '"') { Blank(i, i + 2); i += 2; }
                else if (c == '"') { Blank(i, ++i); return; }
                else if (!verbatim && c == '\n') { return; }
                else if (interpolated && c == '{' && At(i + 1) == '{') { Blank(i, i + 2); i += 2; }
                else if (interpolated && c == '{')
                {
                    Blank(i, ++i);
                    Code(stopAtClose: true);
                    Blank(i, ++i);
                }
                else { Blank(i, ++i); }
            }
        }

        // Scans a raw literal body from just after its opening fence of `quotes` quotes. With n
        // leading '$', a run of n or more '{' opens a hole (the last n of them) and n '}' close it;
        // anything shorter is content. Holes stay code, as in the other literals — blanking them
        // whole hid `$"""{new DuplicateDeviceDialog { … }}"""` from the construction count (Qodo round 3).
        void RawLiteral(int quotes, int dollars)
        {
            while (i < source.Length)
            {
                var run = 0;
                while (At(i + run) == '"') { run++; }
                if (run >= quotes) { Blank(i, i + run); i += run; return; }

                if (dollars > 0 && source[i] == '{')
                {
                    var braces = 0;
                    while (At(i + braces) == '{') { braces++; }
                    if (braces >= dollars)
                    {
                        Blank(i, i + braces);
                        i += braces;
                        Code(stopAtClose: true);
                        Blank(i, i + dollars);
                        i += dollars;
                        continue;
                    }

                    Blank(i, i + braces);
                    i += braces;
                    continue;
                }

                Blank(i, i + Math.Max(run, 1));
                i += Math.Max(run, 1);
            }
        }

        Code(stopAtClose: false);
        return new string(text);
    }

    /// <summary>
    /// Whether a declaration at <paramref name="from"/> is still in scope at <paramref name="to"/>:
    /// no brace between them closes the block the declaration sits in.
    /// </summary>
    private static bool StillInScope(string source, int from, int to)
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
    /// Gap 3: compiled bindings type-check the <i>path</i>, never which control it feeds. The two
    /// choices are told apart only by name: <c>BtnOk_Click</c> reads <c>SwitchToNewRadio.IsChecked</c>
    /// and treats anything else as keep-existing. Swap the two <c>Content</c> bindings — or the two
    /// <c>x:Name</c>s — and everything still compiles, while the button labelled "Switch to USB" keeps
    /// the existing connection and the one labelled "Keep WiFi (recommended)" drops it. That is the
    /// opposite of what the user chose, on a dialog whose whole job is that one choice.
    ///
    /// <para>
    /// The recommended option is the one pre-checked, so pressing OK without reading keeps the
    /// connection the user already has. Pinned alongside, because moving <c>IsChecked</c> to the other
    /// button is the same swap made one attribute over.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("KeepExistingRadio", nameof(DuplicateDeviceDialogViewModel.KeepExistingText), "True")]
    [InlineData("SwitchToNewRadio", nameof(DuplicateDeviceDialogViewModel.SwitchToNewText), null)]
    public void Each_choice_is_labelled_with_what_OK_will_do_with_it(
        string radioName, string labelMember, string? isChecked)
    {
        var radios = Root().Descendants().Where(element => element.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(2, radios.Count);

        var radio = radios.Single(element => element.Attribute(Xaml + "Name")?.Value == radioName);

        Assert.Equal($"{{Binding {labelMember}}}", radio.Attribute("Content")?.Value);
        Assert.Equal(isChecked, radio.Attribute("IsChecked")?.Value);
    }

    /// <summary>
    /// Gap 4: the escape hatches that would put markup back on reflection with the root declaration
    /// still in place. See <see cref="BindingFacts.AssertNoEscapeHatch"/>.
    /// </summary>
    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
