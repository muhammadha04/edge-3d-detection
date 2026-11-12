// Offline test runner: compiles the real pipeline sources with mono against UnityShim.cs.
using System;

public static class Program
{
    public static int Main(string[] args)
    {
        QuestDetect3D.Log.Verbose = Array.IndexOf(args, "-v") >= 0;
        var report = QuestDetect3D.Tests.PipelineTests.RunAll();
        Console.Write(report.ToString());
        return report.Failed == 0 ? 0 : 1;
    }
}
