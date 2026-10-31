// rst-c's own (not vendored). SC-045: stands in for another add-in's code in WindowGuardTests. Its
// namespace is deliberately not RST's, so its frames read as someone else's to WindowGuard.

namespace ThirdParty.SampleAddIn;

public sealed class SampleAddInException(string message, Exception? inner = null) : Exception(message, inner);

public static class IdleWork
{
    /// <summary>Throws, as another add-in's dispatcher work might while an RST window is open.</summary>
    public static void Run() => throw new SampleAddInException("another add-in's work failed");

    /// <summary>The add-in's exception, thrown and caught in its own code (wrapping <paramref name="inner"/>).</summary>
    public static Exception Capture(Exception? inner)
    {
        try
        {
            throw new SampleAddInException("another add-in's work failed", inner);
        }
        catch (SampleAddInException ex)
        {
            return ex;
        }
    }
}
