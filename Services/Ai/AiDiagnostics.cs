using System.Diagnostics;

namespace SuperDev.Services.Ai;

internal static class AiDiagnostics
{
    public static void Fail(string stage, string message)
    {
        Trace.TraceWarning("Super Dev AI failed at {0}: {1}", stage, message);
    }
}
