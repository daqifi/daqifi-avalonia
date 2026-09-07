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

    [Theory]
    // Qodo review of #314. A ')' inside a string literal is text to NCalc and closes nothing, so
    // letting it decrement the depth counter let a crafted paste present more open parentheses to
    // the parser than the guard had counted: the eight quoted ')' below cancelled eight real '('
    // for the counter while the parser still had all twelve open. Both of NCalc 7.1.0's quoting
    // forms have to be skipped — it accepts '...' and "..." alike, and \ escapes inside each.
    [InlineData("((((((((')))))))))' + ((((")]
    [InlineData("((((((((\"))))))))\" + ((((")]
    [InlineData("(((((((('\\'))))))))' + ((((")]
    public void Closing_parentheses_inside_a_string_literal_do_not_buy_back_nesting_depth(string expression)
    {
        var channel = Scaled(expression);

        Assert.False(channel.HasValidExpression);
        Assert.Contains("NESTED PARENTHESES", channel.ScaleExpressionError, StringComparison.Ordinal);
    }

    [Theory]
    // Qodo's second round on #314, and the other direction from the case above. A quote inside
    // an NCalc [bracket-delimited] parameter name is NOT a string delimiter, so a scan that
    // treats it as one stays "inside a string" for the rest of the input and stops counting
    // parentheses altogether: this 19-character paste reported depth 0 while the parser was
    // handed twelve open parentheses and spent 78 seconds on them. The counting is therefore
    // done both ways and the larger answer wins.
    [InlineData("['x] + ((((((((((((")]
    [InlineData("[\"x] + ((((((((((((")]
    public void A_quote_in_a_bracketed_parameter_name_does_not_hide_open_parentheses(string expression)
    {
        var started = Stopwatch.StartNew();
        var channel = Scaled(expression);
        started.Stop();

        Assert.False(channel.HasValidExpression);
        Assert.Contains("NESTED PARENTHESES", channel.ScaleExpressionError, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
            $"the setter took {started.Elapsed.TotalSeconds:0.0} s, so the parser was entered");
    }

    [Fact]
    public void An_input_that_gets_past_both_counts_is_still_bounded_by_them()
    {
        // The worst input found that satisfies BOTH bounds exactly — 8 open parentheses then
        // '!' to the length cap — searched for over 17 filler alphabets at 6, 7 and 8 opens, and
        // over 4,000 random strings. Measured unguarded: 1.4 s when the cap was 256 and 3.2 s now
        // that it is 1,024, against the 48 s the same setter spent on twelve bare parentheses.
        // Those few seconds are the residual stall the caps concede, and it is the ceiling rather
        // than a typical figure: nothing that gets past the caps can escalate past it, because the
        // caps hold the contiguous run of '(' at 8 and the exponent is in that run, leaving a cost
        // that grows in proportion to the length rather than with it.
        //
        // The bound below is deliberately far above the measured 3.2 s. It is not a performance
        // assertion — it is the assertion that SOMETHING bounded the parse, and the regression it
        // guards against is 48 s. Pinning it any tighter would make it a measurement of the build
        // agent, which is the mistake this PR made once already in the production code.
        var underBothCaps = new string('(', AbstractChannel.MaxScaleExpressionDepth)
                            + new string('!', AbstractChannel.MaxScaleExpressionLength
                                              - AbstractChannel.MaxScaleExpressionDepth);

        Assert.Equal(AbstractChannel.MaxScaleExpressionLength, underBothCaps.Length);

        var started = Stopwatch.StartNew();
        var channel = Scaled(underBothCaps);
        started.Stop();

        Assert.False(channel.HasValidExpression);
        Assert.Equal(AbstractChannel.InvalidExpressionMessage, channel.ScaleExpressionError);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20),
            $"the setter took {started.Elapsed.TotalSeconds:0.0} s, so nothing bounded the parse");
    }

    [Fact]
    public void Whether_an_expression_is_accepted_depends_on_its_text_and_nothing_else()
    {
        // The regression this pins is a defect this PR shipped and then removed: the parse used to
        // run under a 250 ms wall-clock deadline, and a budget started before the parse is charged
        // for scheduling delay, GC and JIT as well as for the expression. Measured on a starved
        // 12-core machine, a cold `x / 0` — five characters — was refused in 19 of 30 runs and the
        // label told the user it was TOO COMPLEX. A guard that refuses valid input is a worse
        // defect than the freeze it insures against.
        //
        // So: every refusal reason must be a property of the text, and the same text must get the
        // same answer every time. Both bounds are pure functions of the string, which is what
        // makes this deterministic rather than merely usually-true.
        var reasons = new[]
        {
            AbstractChannel.InvalidExpressionMessage,
            $"EXPRESSION TOO LONG (LIMIT {AbstractChannel.MaxScaleExpressionLength} CHARACTERS)",
            $"TOO MANY NESTED PARENTHESES (LIMIT {AbstractChannel.MaxScaleExpressionDepth})"
        };

        // Every one of these is refused or accepted without the parser doing real work, so the
        // repetition below stays cheap. The one input that does cost the parser a second is
        // covered by An_input_that_gets_past_both_counts_is_still_bounded_by_them, which runs it
        // once rather than 64 times.
        var inputs = new[]
        {
            "x / 0", "x * 2", "x", "x * 2 + 1", "(x + 1) * 2", "x *", new string('(', 12),
            new string('(', 900) + "x" + new string(')', 900)
        };

        foreach (var input in inputs)
        {
            var first = Scaled(input);
            Assert.Contains(first.ScaleExpressionError, reasons);

            // Repeat under contention. Before the fix the verdict for a given string could differ
            // between two runs on the same machine; it cannot now.
            var verdicts = new System.Collections.Concurrent.ConcurrentBag<(bool, string)>();
            Parallel.For(0, 64,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
                _ =>
                {
                    var channel = Scaled(input);
                    verdicts.Add((channel.HasValidExpression, channel.ScaleExpressionError));
                });

            Assert.All(verdicts, verdict =>
                Assert.Equal((first.HasValidExpression, first.ScaleExpressionError), verdict));
        }
    }

    [Theory]
    // The lexical guarantee the depth cap actually provides, and the reason no wall-clock backstop
    // is needed beside it. Because the raw character reading is one of the two the guard maximises
    // over, and it only ever decrements one per ')' with a floor at zero, a run of k consecutive
    // '(' always reads as at least k — whatever precedes it, and whatever NCalc's grammar thinks
    // the surrounding text means. Every shape below tries to drive the counter to zero first and
    // then present a run past the cap; none can.
    [InlineData("(((((((((")]
    [InlineData("))))))))))(((((((((")]
    [InlineData("(((()))) + (((((((((")]
    [InlineData("['x] + (((((((((")]
    [InlineData("'))))))))' + (((((((((")]
    [InlineData("[))))))))] + (((((((((")]
    [InlineData("x + \")))))))))\" + (((((((((")]
    public void A_run_past_the_depth_cap_cannot_be_hidden_from_the_guard(string expression)
    {
        var started = Stopwatch.StartNew();
        var channel = Scaled(expression);
        started.Stop();

        Assert.False(channel.HasValidExpression);
        Assert.Contains("NESTED PARENTHESES", channel.ScaleExpressionError, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
            $"the setter took {started.Elapsed.TotalSeconds:0.0} s, so the parser was entered");
    }

    [Fact]
    public void A_calibration_is_accepted_however_busy_the_machine_is()
    {
        const string steinhartHart =
            "1 / (0.001129148 + 0.000234125 * Ln(10000 * (1023 / x - 1)) + " +
            "0.0000000876741 * Pow(Ln(10000 * (1023 / x - 1)), 3)) - 273.15";

        Assert.True(Scaled(steinhartHart).HasValidExpression);
        Assert.True(Scaled("x * 2 + 1").HasValidExpression);
        Assert.True(Scaled("x / 0").HasValidExpression);
    }

    [Theory]
    [InlineData("x * if('a' == 'a', 2, 3)")]
    [InlineData("x * if(\"a\" == \"a\", 2, 3)")]
    public void A_string_literal_is_still_ordinary_text_the_guard_lets_through(string expression)
    {
        // The control the assertions above need: skipping literals must not make the guard
        // paranoid about an expression that merely contains a quote. Nesting depth 1, and it has
        // to keep parsing and evaluating exactly as before.
        var channel = Scaled(expression);

        Assert.True(channel.HasValidExpression);
        Assert.Equal(6.0, Push(channel, 3.0));
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
        // Steinhart-Hart: 124 characters and 4 levels of nesting, against caps of 1,024 and 8.
        const string steinhartHart =
            "1 / (0.001129148 + 0.000234125 * Ln(10000 * (1023 / x - 1)) + " +
            "0.0000000876741 * Pow(Ln(10000 * (1023 / x - 1)), 3)) - 273.15";

        Assert.Equal(124, steinhartHart.Length);
        Assert.True(steinhartHart.Length < AbstractChannel.MaxScaleExpressionLength);

        var channel = Scaled(steinhartHart);

        Assert.True(channel.HasValidExpression);
        var scaled = Push(channel, 512.0);
        Assert.True(double.IsFinite(scaled));
        Assert.NotEqual(512.0, scaled);
    }

    /// <summary>
    /// The other main class of calibration a DAQ user writes, and the defect #316 reported: the
    /// length cap was sized against a thermistor conversion and thermocouple linearisation is
    /// longer, so a published NIST polynomial was refused as "too long". These are the real
    /// formulas, at the lengths they actually have.
    /// </summary>
    public static TheoryData<string, int, string> RealThermocoupleCalibrations() => new()
    {
        // The exact expression from #316: type-K direct, ITS-90 0-1372 C, degree 9 plus the
        // Gaussian term, at 7 significant figures. 267 characters, so the 256-cap refused it by
        // 11. Nesting 2, and it parses in single-digit milliseconds - nothing about it is
        // dangerous, and length was the only thing standing in its way.
        {
            "type-K direct, ITS-90, 7 significant figures",
            267,
            "-1.760041E1 + 3.892021E1*x + 1.855877E-2*Pow(x,2) - 9.945759E-5*Pow(x,3) "
            + "+ 3.184094E-7*Pow(x,4) - 5.607284E-10*Pow(x,5) + 5.607506E-13*Pow(x,6) "
            + "- 3.202072E-16*Pow(x,7) + 9.715115E-20*Pow(x,8) - 1.210472E-23*Pow(x,9) "
            + "+ 1.185976E-1*Exp(-1.183432E-4*Pow(x-1.269686E2,2))"
        },

        // The same polynomial written with the coefficients at the full 12-significant-figure
        // precision the NIST tables publish, which is what a careful user copies.
        {
            "type-K direct, ITS-90, full NIST precision",
            389,
            "-0.176004136860E-01 + 0.389212049750E-01 * x "
            + "+ 0.185587700320E-04 * Pow(x, 2) - 0.994575928740E-07 * Pow(x, 3) "
            + "+ 0.318409457190E-09 * Pow(x, 4) - 0.560728448890E-12 * Pow(x, 5) "
            + "+ 0.560750590590E-15 * Pow(x, 6) - 0.320207200030E-18 * Pow(x, 7) "
            + "+ 0.971511471520E-22 * Pow(x, 8) - 0.121047212750E-25 * Pow(x, 9) "
            + "+ 0.118597600000E+00 * Exp(-0.118343200000E-03 * Pow(x - 0.126968600000E+03, 2))"
        },

        // The realistic maximum, and the reason the new cap is 1,024 rather than 512: the NIST
        // type-K INVERSE function - millivolts to degrees, which is the direction a DAQ channel
        // actually needs - is piecewise over three sub-ranges. Linearising a full-range
        // thermocouple in one scaling box is therefore an if() chain over three polynomials.
        // 639 characters and nesting 3.
        {
            "type-K inverse, ITS-90, all three sub-ranges",
            639,
            "if(x < 0, 0.0000000E0 + 2.5173462E1 * x - 1.1662878E0 * Pow(x, 2) - 1.0833638E0 * "
            + "Pow(x, 3) - 8.9773540E-1 * Pow(x, 4) - 3.7342377E-1 * Pow(x, 5) - 8.6632643E-2 * "
            + "Pow(x, 6) - 1.0450598E-2 * Pow(x, 7) - 5.1920577E-4 * Pow(x, 8), if(x < 20.644, "
            + "0.000000E0 + 2.508355E1 * x + 7.860106E-2 * Pow(x, 2) - 2.503131E-1 * Pow(x, 3) + "
            + "8.315270E-2 * Pow(x, 4) - 1.228034E-2 * Pow(x, 5) + 9.804036E-4 * Pow(x, 6) - "
            + "4.413030E-5 * Pow(x, 7) + 1.057734E-6 * Pow(x, 8) - 1.052755E-8 * Pow(x, 9), "
            + "-1.318058E2 + 4.830222E1 * x - 1.646031E0 * Pow(x, 2) + 5.464731E-2 * Pow(x, 3) - "
            + "9.650715E-4 * Pow(x, 4) + 8.802193E-6 * Pow(x, 5) - 3.110810E-8 * Pow(x, 6)))"
        }
    };

    [Theory]
    [MemberData(nameof(RealThermocoupleCalibrations))]
    public void A_published_thermocouple_calibration_is_accepted(string name, int length, string calibration)
    {
        // The length is asserted rather than commented: it is the whole reason these are here,
        // and a stray edit that shortened one would quietly stop testing the cap.
        Assert.Equal(length, calibration.Length);
        Assert.True(length <= AbstractChannel.MaxScaleExpressionLength,
            $"{name} is {length} characters, past the {AbstractChannel.MaxScaleExpressionLength}-character cap");

        var channel = Scaled(calibration);

        Assert.True(channel.HasValidExpression, $"{name} was refused: {channel.ScaleExpressionError}");
        Assert.NotNull(channel.Expression);

        // And it has to evaluate, not merely parse: 10.0 on a type-K channel is a real reading.
        var scaled = Push(channel, 10.0);
        Assert.True(double.IsFinite(scaled));
        Assert.NotEqual(10.0, scaled);
    }

    [Fact]
    public void The_cap_leaves_room_for_the_longest_calibration_that_can_be_written()
    {
        // The bound above the largest real formula, stated as a fact rather than a comment. The
        // full-range piecewise inverse written with every coefficient at full NIST precision is
        // about 993 characters - the most extreme thing this box can be legitimately asked for -
        // so the cap has to be at least that, and 1,024 is the round number above it.
        Assert.True(AbstractChannel.MaxScaleExpressionLength >= 1024,
            "the full-range type-K inverse at full NIST precision is ~993 characters");
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
