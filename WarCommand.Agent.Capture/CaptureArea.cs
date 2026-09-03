namespace WarCommand.Agent.Capture;

/// <summary>A screen rectangle to copy. Public so the Win32 shapes stay internal to this assembly.</summary>
public readonly record struct CaptureArea(int Left, int Top, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
