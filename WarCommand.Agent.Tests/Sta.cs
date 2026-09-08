namespace WarCommand.Agent.Tests;

/// <summary>
/// Runs a body on an STA thread, which is what any test touching a WPF or WinForms type needs.
/// </summary>
/// <remarks>
/// The thread is a BACKGROUND one, and that is the whole point of this type existing. Nine copies
/// of this helper each started a foreground thread and joined it with a timeout. A foreground thread
/// keeps the process alive after Main returns, so any test that threw before closing its window left
/// one running with a live WPF window on it, and the test host never exited: the run printed its
/// results and then hung.
/// <para>
/// It hung the release workflow for an hour and fifty minutes with no output past "Test", and the CI
/// run before it was canceled at three hours. It reproduces locally as a full run that reports pass
/// counts and never returns, which is easy to mistake for a machine quirk because a filtered run
/// that happens to skip the failing test exits cleanly.
/// </para>
/// </remarks>
internal static class Sta
{
    /// <summary>How long a UI body may take before the test fails rather than waits.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Runs <paramref name="body"/> on a background STA thread and rethrows what it threw.</summary>
    public static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failure = ex;
            }
        })
        {
            // Never a foreground thread. A UI body that hangs must cost this test its 60 seconds
            // and nothing else; it must not be able to hold the whole run open behind it.
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(Limit))
        {
            throw new TimeoutException(
                $"The STA body did not finish within {Limit.TotalSeconds:F0}s. It is still running on an abandoned thread.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("The STA body threw.", failure);
        }
    }
}
