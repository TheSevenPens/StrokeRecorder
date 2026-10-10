namespace StrokeRecorder.Tests;

/// <summary>
/// That a tablet or driver name keeps the spaces inside it and loses the ones around it.
/// </summary>
public class TypedText
{
    [Theory]
    [InlineData("Wacom PTK-870", "Wacom PTK-870")]
    [InlineData("  Wacom PTK-870", "Wacom PTK-870")]
    [InlineData("Wacom PTK-870   ", "Wacom PTK-870")]
    [InlineData("\t Wacom PTK-870 \r\n", "Wacom PTK-870")]
    [InlineData("Wacom  PTK-870", "Wacom  PTK-870")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void Spaces_inside_a_name_are_kept_and_the_ends_are_stripped(string typed, string kept)
    {
        Assert.Equal(kept, Typed.Clean(typed));
    }

    [Fact]
    public void Nothing_typed_is_empty_and_not_a_failure()
    {
        Assert.Equal("", Typed.Clean(null));
    }
}
