using SDemo.Hub;
using Xunit;

namespace SDemo.Hub.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+C", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43u, "Ctrl+Alt+C")]
    [InlineData("shift + win + f5", HotkeyModifiers.Shift | HotkeyModifiers.Win, 0x74u, "Shift+Win+F5")]
    [InlineData("Control+Shift+9", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x39u, "Ctrl+Shift+9")]
    [InlineData("F12", HotkeyModifiers.None, 0x7Bu, "F12")]
    public void Parse_Normalizes(string text, HotkeyModifiers modifiers, uint vk, string normalized)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out var error));
        Assert.Null(error);
        Assert.NotNull(gesture);
        Assert.Equal(modifiers, gesture!.Modifiers);
        Assert.Equal(vk, gesture.VirtualKey);
        Assert.Equal(normalized, gesture.Normalized);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Hyper+C")]
    [InlineData("")]
    [InlineData("Ctrl+F25")]
    public void Parse_RejectsInvalid(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture, out var error));
        Assert.Null(gesture);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
