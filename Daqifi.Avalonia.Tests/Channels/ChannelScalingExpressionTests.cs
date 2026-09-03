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
    // Min feeds 5 rather than -5 so the expected value differs from the raw input: a case whose
    // expectation equals its input still passes when scaling silently stops being applied at all,
    // which is the one drift these tests exist to catch.
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
}
