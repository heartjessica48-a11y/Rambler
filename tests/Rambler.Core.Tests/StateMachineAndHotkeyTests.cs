using Rambler.Core.Dictation;
using Rambler.Core.Settings;

namespace Rambler.Core.Tests;

public class DictationStateMachineTests
{
    [Fact]
    public void Happy_path_with_cleanup_is_allowed()
    {
        var sm = new DictationStateMachine();
        sm.TransitionTo(DictationState.Listening);
        sm.TransitionTo(DictationState.Finalizing);
        sm.TransitionTo(DictationState.Cleaning);
        sm.TransitionTo(DictationState.Inserting);
        sm.TransitionTo(DictationState.Idle);
        Assert.Equal(DictationState.Idle, sm.State);
    }

    [Fact]
    public void Smart_only_path_skips_cleaning()
    {
        var sm = new DictationStateMachine();
        sm.TransitionTo(DictationState.Listening);
        sm.TransitionTo(DictationState.Finalizing);
        sm.TransitionTo(DictationState.Inserting);
        sm.TransitionTo(DictationState.Idle);
    }

    [Theory]
    [InlineData(DictationState.Idle, DictationState.Inserting)]
    [InlineData(DictationState.Idle, DictationState.Cleaning)]
    [InlineData(DictationState.Listening, DictationState.Inserting)]
    [InlineData(DictationState.Listening, DictationState.Cleaning)]
    [InlineData(DictationState.Inserting, DictationState.Listening)]
    public void Invalid_transitions_throw(DictationState from, DictationState to)
    {
        var sm = Reach(from);
        Assert.False(sm.CanTransition(to));
        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(to));
    }

    [Theory]
    [InlineData(DictationState.Listening)]
    [InlineData(DictationState.Finalizing)]
    [InlineData(DictationState.Cleaning)]
    public void Cancellation_returns_to_idle(DictationState from)
    {
        var sm = Reach(from);
        sm.TransitionTo(DictationState.Idle);
        Assert.Equal(DictationState.Idle, sm.State);
    }

    [Fact]
    public void Error_allows_new_dictation_and_pending_insert()
    {
        var sm = Reach(DictationState.Error);
        Assert.True(sm.CanStart);
        Assert.True(sm.CanTransition(DictationState.Inserting));
        Assert.True(sm.CanTransition(DictationState.Listening));
    }

    [Fact]
    public void Busy_states_cannot_start_a_new_session()
    {
        foreach (var s in new[] { DictationState.Listening, DictationState.Finalizing, DictationState.Cleaning, DictationState.Inserting })
            Assert.False(Reach(s).CanStart);
    }

    [Theory]
    [InlineData(DictationTrigger.Default, DictationMode.PromptCleanup, DictationMode.PromptCleanup)]
    [InlineData(DictationTrigger.Default, DictationMode.SmartOnly, DictationMode.SmartOnly)]
    [InlineData(DictationTrigger.SmartBypass, DictationMode.PromptCleanup, DictationMode.SmartOnly)]
    [InlineData(DictationTrigger.SmartBypass, DictationMode.SmartOnly, DictationMode.SmartOnly)]
    public void Hotkey_mode_selection(DictationTrigger trigger, DictationMode selected, DictationMode expected) =>
        Assert.Equal(expected, DictationStateMachine.ResolveMode(trigger, selected));

    private static DictationStateMachine Reach(DictationState target)
    {
        var sm = new DictationStateMachine();
        var path = target switch
        {
            DictationState.Idle => [],
            DictationState.Listening => [DictationState.Listening],
            DictationState.Finalizing => [DictationState.Listening, DictationState.Finalizing],
            DictationState.Cleaning => [DictationState.Listening, DictationState.Finalizing, DictationState.Cleaning],
            DictationState.Inserting => [DictationState.Listening, DictationState.Finalizing, DictationState.Inserting],
            DictationState.Error => new[] { DictationState.Error },
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        foreach (var s in path) sm.TransitionTo(s);
        return sm;
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Win+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Win, 0x20)]
    [InlineData("Ctrl+Win+Shift+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x20)]
    [InlineData("ctrl + alt + d", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'D')]
    [InlineData("F9", HotkeyModifiers.None, 0x78)]
    [InlineData("Win+F12", HotkeyModifiers.Win, 0x7B)]
    [InlineData("Control+Shift+1", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, '1')]
    public void Parses_valid_shortcuts(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(vk, g.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")] // needs a modifier
    [InlineData("Ctrl+Win")] // no key
    [InlineData("Ctrl+A+B")] // two keys
    [InlineData("Ctrl+Bogus")]
    [InlineData(null)]
    public void Rejects_invalid_shortcuts(string? text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Formats_in_canonical_order()
    {
        HotkeyGesture.TryParse("shift+space+win+ctrl", out var g);
        Assert.Equal("Ctrl+Win+Shift+Space", g.ToString());
    }

    [Fact]
    public void No_shortcuts_are_set_by_default()
    {
        var s = new AppSettings();
        Assert.Equal("", s.HotkeyToggle);
        Assert.Equal("", s.HotkeySmartBypass);
    }

    [Fact]
    public void Modifier_flags_match_win32()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Win+Space", out var g));
        Assert.Equal(0x2 | 0x8, (int)g.Modifiers); // MOD_CONTROL | MOD_WIN
    }
}
