using Sable.Core;
using Xunit;

namespace Sable.Tests;

public class NumericExpressionTests
{
    [Theory]
    [InlineData("42", 0, 42)]
    [InlineData("512/2", 0, 256)]
    [InlineData("30+12*2", 0, 54)]
    [InlineData("10-4-3", 0, 3)]
    [InlineData("2*3+4", 0, 10)]
    [InlineData("-5", 0, -5)]
    [InlineData("1.5*4", 0, 6)]
    [InlineData("1,5*4", 0, 6)]   // comma decimal tolerated
    public void Absolute_Expressions(string text, double current, double expected)
    {
        Assert.True(NumericExpression.TryEval(text, current, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData("+10", 50, 60)]
    [InlineData("- 5", 50, 45)]
    [InlineData("*2", 50, 100)]
    [InlineData("/2", 50, 25)]
    public void Relative_LeadingOperator(string text, double current, double expected)
    {
        Assert.True(NumericExpression.TryEval(text, current, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData("50%", 80, 40)]      // percent of the current value
    [InlineData("+10%", 200, 220)]
    public void Percentages_OfCurrent(string text, double current, double expected)
    {
        Assert.True(NumericExpression.TryEval(text, current, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1+")]
    [InlineData("5/0")]
    [InlineData("1..2")]
    public void Invalid_Inputs_Rejected(string text)
    {
        Assert.False(NumericExpression.TryEval(text, 10, out _));
    }

    [Fact]
    public void NegativeFive_IsAbsolute_NotRelative()
    {
        // "-5" parses as the relative form (current - 5) per the leading-operator rule…
        NumericExpression.TryEval("-5", 0, out var v);
        Assert.Equal(-5, v, 6);   // …which equals -5 when current is 0
    }
}
