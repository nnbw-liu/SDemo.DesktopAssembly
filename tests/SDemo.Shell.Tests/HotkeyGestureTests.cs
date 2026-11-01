using SDemo.Shell.Contracts;

namespace SDemo.Shell.Tests;

/// <summary>热键解析（DA-10 / DA-70）：规范文本、修饰键、非法输入、键名集合。</summary>
public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", "Ctrl+Alt+T")]
    [InlineData("Alt+Ctrl+T", "Ctrl+Alt+T")]
    [InlineData("ctrl+shift+F5", "Ctrl+Shift+F5")]
    [InlineData("Win+Space", "Win+Space")]
    [InlineData("Shift+Ctrl+Alt+Win+A", "Ctrl+Alt+Shift+Win+A")]
    public void Parse_NormalizesModifiersAndKey(string text, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(canonical, gesture!.ToCanonicalString());
    }

    [Fact]
    public void Parse_ExposesVirtualKey()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+T", out var gesture));
        Assert.Equal(0x54, gesture!.VirtualKey);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal("T", gesture.KeyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt+Unknown")]
    [InlineData("Ctrl+Ctrl+T")]
    [InlineData("Hyper+T")]
    public void Parse_RejectsInvalid(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
    }

    [Fact]
    public void Parse_SupportsNamedKeysAndFunctionKeys()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+PageDown", out var pageDown));
        Assert.Equal("PageDown", pageDown!.KeyName);

        Assert.True(HotkeyGesture.TryParse("Ctrl+F12", out var f12));
        Assert.Equal(0x7B, f12!.VirtualKey);

        Assert.False(HotkeyGesture.TryParse("Ctrl+F25", out _));
    }

    [Fact]
    public void FormatModifiers_UsesStableOrdering()
    {
        Assert.Equal(
            "Ctrl+Alt+",
            HotkeyGesture.FormatModifiers(HotkeyModifiers.Control | HotkeyModifiers.Alt));
        Assert.Equal(string.Empty, HotkeyGesture.FormatModifiers(HotkeyModifiers.None));
    }
}
