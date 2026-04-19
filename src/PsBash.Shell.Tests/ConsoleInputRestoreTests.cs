using Xunit;

namespace PsBash.Shell.Tests;

public class ConsoleInputRestoreTests
{
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    [Fact]
    public void ComputeRestoredInputMode_ClearsVirtualTerminalInput_WhenChildLeftItEnabled()
    {
        // Simulates the console state after an external TUI (e.g. agnt) exits via
        // Ctrl+C without restoring. LineEditor's Console.ReadKey-based dispatch
        // requires VT input OFF, otherwise arrows/backspace arrive as raw ESC sequences.
        uint childLeftMode = ENABLE_PROCESSED_INPUT | ENABLE_VIRTUAL_TERMINAL_INPUT;

        uint restored = InteractiveShell.ComputeRestoredInputMode(childLeftMode);

        Assert.Equal(0u, restored & ENABLE_VIRTUAL_TERMINAL_INPUT);
    }

    [Fact]
    public void ComputeRestoredInputMode_SetsCookedInputFlags()
    {
        uint restored = InteractiveShell.ComputeRestoredInputMode(0u);

        Assert.Equal(ENABLE_PROCESSED_INPUT, restored & ENABLE_PROCESSED_INPUT);
        Assert.Equal(ENABLE_LINE_INPUT, restored & ENABLE_LINE_INPUT);
        Assert.Equal(ENABLE_ECHO_INPUT, restored & ENABLE_ECHO_INPUT);
    }

    [Fact]
    public void ComputeRestoredInputMode_PreservesUnrelatedFlags()
    {
        const uint ENABLE_WINDOW_INPUT = 0x0008;
        const uint ENABLE_MOUSE_INPUT = 0x0010;
        uint input = ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_VIRTUAL_TERMINAL_INPUT;

        uint restored = InteractiveShell.ComputeRestoredInputMode(input);

        Assert.Equal(ENABLE_WINDOW_INPUT, restored & ENABLE_WINDOW_INPUT);
        Assert.Equal(ENABLE_MOUSE_INPUT, restored & ENABLE_MOUSE_INPUT);
    }
}
