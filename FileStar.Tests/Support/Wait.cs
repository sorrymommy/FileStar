namespace FileStar.Tests.Support;

public static class Wait
{
    /// <summary>Lets the WPF thread work until <paramref name="condition"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public static async Task Until(Func<bool> condition, string what, double seconds = 10)
    {
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.Now > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await WpfThread.Settle(20);
        }
    }
}
