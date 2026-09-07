using System.Diagnostics;
using Daqifi.Desktop.Channel;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Channels;

/// <summary>
/// Characterisation tests for channel scaling — the user-entered expression in
/// <see cref="AbstractChannel.ScaleExpression"/> that rewrites every incoming sample before it
/// reaches the live plot, the logging database and the CSV export.
///
/// <para>
/// This path had no tests, and it is the app's only use of NCalc. That combination is the reason
/// these exist: a green build cannot detect a change in how an expression *evaluates*, so a
/// dependency bump could silently alter every scaled reading a user records. The assertions below
/// pin evaluation semantics (operators, decimal literals, built-in functions, division) rather
/// than merely checking that scaling is wired up, so they fail on a semantic drift instead of
/// passing through it.
/// </para>
///
/// <para>
/// Note the deliberate asymmetry in the production code that shapes several cases: the setter
/// validates with an <em>integer</em> parameter (<c>["x"] = 1</c>), while
/// <see cref="AbstractChannel.ActiveSample"/> evaluates with a <em>double</em>. An expression can
/// therefore validate under integer arithmetic and run under floating-point arithmetic, and
/// <see cref="Validation_uses_an_integer_parameter_while_evaluation_uses_a_double"/> pins that.
/// </para>
/// </summary>
public class ChannelScalingExpressionTests
{
    /// <summary>
    /// A minimal concrete <see cref="AbstractChannel"/>. The real <see cref="AnalogChannel"/> and
    /// <see cref="DigitalChannel"/> each need a Core channel to construct, and none of that is
    /// relevant to expression evaluation.
    /// </summary>
    private sealed class ScalingChannel : AbstractChannel
    {
        public override string Name { get; set; } = "AI0";

        public override ChannelDirection Direction { get; set; } = ChannelDirection.Input;

        public override int Index => 0;

        public override ChannelType Type => ChannelType.Analog;

        public override bool IsActive { get; set; }

        public override bool IsDigital => false;

        public override bool IsAnalog => true;
    }

    private static ScalingChannel Scaled(string expression)
    {
        var channel = new ScalingChannel { ScaleExpression = expression };
        channel.IsScalingActive = true;
        return channel;
    }

    private static double Push(AbstractChannel channel, double raw)
    {
        var sample = new DataSample { Value = raw };
        channel.ActiveSample = sample;
        return sample.Value;
    }

    // ---- Wiring -----------------------------------------------------------------------------

    [Fact]
    public void A_valid_expression_is_accepted_and_rewrites_the_sample()
    {
        var channel = Scaled("x * 2");

        Assert.True(channel.HasValidExpression);
        Assert.NotNull(channel.Expression);
        Assert.Equal(42.0, Push(channel, 21.0));
    }

    [Fact]
    public void Scaling_that_is_switched_off_leaves_the_sample_alone()
    {
        var channel = new ScalingChannel { ScaleExpression = "x * 2" };
        channel.IsScalingActive = false;

        Assert.True(channel.HasValidExpression);
        Assert.Equal(21.0, Push(channel, 21.0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_expression_clears_the_expression_rather_than_failing(string? expression)
    {
        // ScaleExpression is declared non-nullable, but the setter's first act is an
        // IsNullOrWhiteSpace check — so null is a case the production code deliberately handles
        // and is worth pinning alongside empty and whitespace.
        var channel = new ScalingChannel { ScaleExpression = expression! };

        Assert.False(channel.HasValidExpression);
        Assert.Null(channel.Expression);
    }

    [Theory]
    [InlineData("x *")]
    [InlineData("NotAFunction(x)")]
    [InlineData(")(")]
    public void A_malformed_expression_is_rejected_at_entry(string expression)
    {
        var channel = new ScalingChannel { ScaleExpression = expression };

        Assert.False(channel.HasValidExpression);
        Assert.Null(channel.Expression);
    }

    [Fact]
    public void A_rejected_expression_leaves_later_samples_unscaled()
    {
        var channel = Scaled("x *");

        Assert.Equal(7.0, Push(channel, 7.0));
    }

    // ---- Evaluation semantics — what a dependency bump could move ---------------------------

    [Theory]
    [InlineData("x + 1", 2.5, 3.5)]
    [InlineData("x - 1", 2.5, 1.5)]
    [InlineData("x * 3", 2.5, 7.5)]
    [InlineData("x / 4", 10.0, 2.5)]
    [InlineData("-x", 2.5, -2.5)]
    [InlineData("(x + 1) * 2", 3.0, 8.0)]
    public void Arithmetic_operators_evaluate_in_double_precision(string expression, double raw, double expected)
    {
        Assert.Equal(expected, Push(Scaled(expression), raw), precision: 10);
    }

    [Fact]
    public void A_decimal_literal_is_parsed_with_a_dot_regardless_of_the_ambient_culture()
    {
        // The expression is typed by the user into a text box and stored verbatim. If the parser
        // ever became culture-sensitive, "x * 1.5" would silently mean "x * 15" for a user on a
        // comma-decimal locale — a 10x error in recorded data with no error message anywhere.
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            Assert.Equal(3.0, Push(Scaled("x * 1.5"), 2.0), precision: 10);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Abs(x)", -3.0, 3.0)]
    [InlineData("Sqrt(x)", 9.0, 3.0)]
    [InlineData("Pow(x, 2)", 3.0, 9.0)]
    [InlineData("Round(x, 1)", 2.349, 2.3)]
    [InlineData("Max(x, 0)", -5.0, 0.0)]
    // Raw 5 -> 0 rather than the more natural-looking -5 -> -5: an expected value equal to the
    // raw input cannot distinguish "Min evaluated correctly" from "scaling never ran at all", so
    // that form of the case passed even with the whole scaling path stubbed out.
    [InlineData("Min(x, 0)", 5.0, 0.0)]
    public void Built_in_functions_keep_their_meaning(string expression, double raw, double expected)
    {
        Assert.Equal(expected, Push(Scaled(expression), raw), precision: 10);
    }

    [Fact]
    public void Validation_uses_an_integer_parameter_while_evaluation_uses_a_double()
    {
        // The setter validates with ["x"] = 1, so "x / 2" is checked under integer arithmetic and
        // then run under floating-point arithmetic. Both halves matter: the expression must be
        // accepted, and it must not carry integer truncation into the evaluated result.
        var channel = Scaled("x / 2");

        Assert.True(channel.HasValidExpression);
        Assert.Equal(0.5, Push(channel, 1.0), precision: 10);
    }

    // ---- The non-finite guard ---------------------------------------------------------------

    [Theory]
    [InlineData("x / 0", 1.0)]
    [InlineData("x / (x - x)", 1.0)]
    public void A_non_finite_result_keeps_the_raw_value_and_switches_scaling_off(string expression, double raw)
    {
        // A float divide-by-zero yields Infinity or NaN *without* throwing, so this is caught by
        // the finiteness check rather than the catch block. Either way Infinity/NaN must never
        // reach the plot or the exported data.
        var channel = Scaled(expression);
        Assert.True(channel.HasValidExpression);

        Assert.Equal(raw, Push(channel, raw));
        Assert.False(channel.HasValidExpression);
    }

    [Fact]
    public void Scaling_stays_off_for_every_later_sample_once_it_has_been_switched_off()
    {
        var channel = Scaled("x / 0");
        Push(channel, 1.0);

        Assert.Equal(5.0, Push(channel, 5.0));
        Assert.False(channel.HasValidExpression);
    }

    // ---- The bounds on what reaches the parser (#311) ----------------------------------------
    //
    // NCalc's grammar backtracks exponentially over a run of unclosed '(' and recurses once per
    // nesting level, and the setter's catch cannot see either failure: an exponential parse never
    // throws, and a StackOverflowException is uncatchable — the runtime fails fast and the whole
    // process goes, taking a running logging session with it. So the tests below assert on the
    // GUARD refusing the input, which is the only thing a test host can survive asserting on.

    /// <summary>The longest valid expression that still fits the length cap exactly.</summary>
    private static string ExpressionOfLength(int length)
    {
        // "x +1+1+1..." — syntactically valid at any odd/even length, and cheap to parse.
        var text = "x " + string.Concat(Enumerable.Repeat("+1", (length - 2) / 2));
        return length % 2 == 0 ? text : text + " ";
    }

    [Fact]
    public void An_expression_at_the_length_cap_is_still_accepted()
    {
        var expression = ExpressionOfLength(AbstractChannel.MaxScaleExpressionLength);
        Assert.Equal(AbstractChannel.MaxScaleExpressionLength, expression.Length);

        var channel = Scaled(expression);

        Assert.True(channel.HasValidExpression);
        Assert.NotNull(channel.Expression);
    }

    [Fact]
    public void An_expression_past_the_length_cap_is_refused_before_it_reaches_the_parser()
    {
        // Syntactically perfect and only one character over — the refusal is the length, not the
        // grammar, which is exactly what stops a 25,600-character run of '-' from overflowing.
        var expression = ExpressionOfLength(AbstractChannel.MaxScaleExpressionLength + 1);

        var channel = Scaled(expression);

        Assert.False(channel.HasValidExpression);
        Assert.Null(channel.Expression);
        Assert.Contains("TOO LONG", channel.ScaleExpressionError, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_at_the_depth_cap_is_still_accepted()
    {
        var depth = AbstractChannel.MaxScaleExpressionDepth;
        var channel = Scaled(new string('(', depth) + "x" + new string(')', depth));

        Assert.True(channel.HasValidExpression);
        Assert.Equal(21.0, Push(channel, 21.0));
    }

    [Fact]
    public void Nesting_past_the_depth_cap_is_refused_before_it_reaches_the_parser()
    {
        // Balanced and syntactically valid, so nothing but the depth cap can refuse it.
        var depth = AbstractChannel.MaxScaleExpressionDepth + 1;

        var channel = Scaled(new string('(', depth) + "x" + new string(')', depth));

        Assert.False(channel.HasValidExpression);
        Assert.Null(channel.Expression);
        Assert.Contains("NESTED PARENTHESES", channel.ScaleExpressionError, StringComparison.Ordinal);
    }

    [Fact]
    public void Unmatched_closing_parentheses_do_not_buy_back_nesting_depth()
    {
        // ')' before any '(' is ignored rather than driving the counter negative: what costs the
        // parser is how many parentheses are open when it starts backtracking, so ")))" followed
        // by a deep run must still be refused.
        var run = new string(')', 8) + new string('(', AbstractChannel.MaxScaleExpressionDepth + 1);

        var channel = Scaled(run);

        Assert.False(channel.HasValidExpression);
        Assert.Contains("NESTED PARENTHESES", channel.ScaleExpressionError, StringComparison.Ordinal);
    }

    [Fact]
    public void The_twelve_character_paste_that_wedged_the_UI_for_fifty_seconds_returns_at_once()
    {
        // Measured against this same setter before the guard: 0.07 s at 4 open parentheses,
        // 0.69 s at 8, 3.6 s at 10, 48.5 s at 12 — on the UI thread, so the window stopped
        // painting and logging could not be stopped. The bound below is ~10x the pre-fix figure's
        // safety margin in the wrong direction on purpose: anything under it proves the parser
        // was never entered, and a regression lands nowhere near it.
        var stopwatch = Stopwatch.StartNew();
        var channel = Scaled(new string('(', 12));
        stopwatch.Stop();

        Assert.False(channel.HasValidExpression);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"the guard let a 12-character run of '(' reach the parser: {stopwatch.Elapsed}");
    }

    /// <summary>
    /// The crash half. This asserts the <em>guard</em> refuses the input, not that the input
    /// crashes: a test that actually let this reach NCalc would abort the test host with an
    /// uncatchable <c>StackOverflowException</c> and there would be no result to report.
    ///
    /// <para>
    /// That the unguarded input really does abort was confirmed separately, out of process,
    /// against the built <c>Daqifi.Avalonia.dll</c> at this branch's merge base: assigning
    /// <c>new string('(', 900) + "x" + new string(')', 900)</c> to this property exited 134
    /// (SIGABRT) with <c>Stack overflow.</c> and 26,648 frames of
    /// <c>NCalc.LogicalExpressionParser</c>/<c>Parlot.Fluent</c>, and a <c>catch (Exception)</c>
    /// wrapped around the assignment did not run.
    /// </para>
    /// </summary>
    [Fact]
    public void The_paste_that_aborted_the_process_is_refused_by_the_guard_and_never_parsed()
    {
        var text = new string('(', 900) + "x" + new string(')', 900);

        var channel = Scaled(text);

        Assert.False(channel.HasValidExpression);
        Assert.Null(channel.Expression);
        // Length is checked first, so that is the reason the user is given for this one.
        Assert.Contains("TOO LONG", channel.ScaleExpressionError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_syntax_error_still_reads_exactly_as_it_did_before()
    {
        var channel = Scaled("x *");

        Assert.False(channel.HasValidExpression);
        Assert.Equal("INVALID EXPRESSION", channel.ScaleExpressionError);
    }

    [Fact]
    public void A_calibration_a_real_user_would_write_is_well_inside_both_bounds()
    {
        // Steinhart-Hart, about the most involved expression this box is asked for: 122
        // characters and 4 levels of nesting, against caps of 256 and 8.
        const string steinhartHart =
            "1 / (0.001129148 + 0.000234125 * Ln(10000 * (1023 / x - 1)) + " +
            "0.0000000876741 * Pow(Ln(10000 * (1023 / x - 1)), 3)) - 273.15";

        Assert.True(steinhartHart.Length < AbstractChannel.MaxScaleExpressionLength);

        var channel = Scaled(steinhartHart);

        Assert.True(channel.HasValidExpression);
        var scaled = Push(channel, 512.0);
        Assert.True(double.IsFinite(scaled));
        Assert.NotEqual(512.0, scaled);
    }

    [Fact]
    public void The_scaling_error_label_shows_the_reason_on_both_heads()
    {
        // Neither view declares an x:DataType, so these bindings resolve by reflection: renaming
        // the property would leave the label blank on both heads with a green build.
        const string desktop = "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/ChannelsPanePrototype.axaml";
        const string mobile = "Daqifi.Avalonia/Views/Mobile/ChannelsMobileView.axaml";
        const string binding = "Text=\"{Binding SelectedChannel.ScaleExpressionError}\"";

        BindingFacts.AssertBinds(desktop, binding);
        BindingFacts.AssertBinds(mobile, binding);
        BindingFacts.AssertExposes(typeof(AbstractChannel), nameof(AbstractChannel.ScaleExpressionError));

        // The other half: no head still carries the wording as a literal, which would pin the
        // label to "INVALID EXPRESSION" whatever the reason was. AssertBinds above is the
        // positive control that these two paths are read rather than silently missing.
        Assert.DoesNotContain("Text=\"INVALID EXPRESSION\"", BindingFacts.Source(desktop), StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"INVALID EXPRESSION\"", BindingFacts.Source(mobile), StringComparison.Ordinal);
    }
}
