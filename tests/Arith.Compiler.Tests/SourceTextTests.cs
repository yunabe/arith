using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

public sealed class SourceTextTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(2, 1, 3)]  // The "\n" itself still belongs to line 1.
    [InlineData(3, 2, 1)]  // First character after "ab\n".
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 4)]  // Interior of "\r\n" still belongs to line 2.
    [InlineData(7, 3, 1)]  // First character after "cd\r\n".
    [InlineData(9, 3, 3)]
    [InlineData(10, 4, 1)] // First character after "ef\r" (a lone "\r" ends a line).
    [InlineData(12, 4, 3)] // End of text is a valid position.
    public void GetLinePosition_MixedNewlines_MapsToOneBasedLineAndColumn(
        int position, int expectedLine, int expectedColumn)
    {
        SourceText text = SourceText.From("ab\ncd\r\nef\rgh");

        LinePosition actual = text.GetLinePosition(position);

        Assert.Equal(new LinePosition(expectedLine, expectedColumn), actual);
    }

    [Fact]
    public void GetLinePosition_EmptyText_ReturnsLineOneColumnOne()
    {
        SourceText text = SourceText.From("");

        Assert.Equal(new LinePosition(1, 1), text.GetLinePosition(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void GetLinePosition_PositionOutsideText_Throws(int position)
    {
        SourceText text = SourceText.From("abc");

        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLinePosition(position));
    }

    [Fact]
    public void ToString_WithSpan_ReturnsThatSlice()
    {
        SourceText text = SourceText.From("let x = 10;");

        Assert.Equal("x", text.ToString(new TextSpan(4, 1)));
        Assert.Equal("10", text.ToString(TextSpan.FromBounds(8, 10)));
    }
}
