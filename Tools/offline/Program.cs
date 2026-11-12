// Offline test runner: compiles the real pipeline + annotation sources with mono against UnityShim.cs.
using System;

public static class Program
{
    public static int Main(string[] args)
    {
        QuestDetect3D.Log.Verbose = Array.IndexOf(args, "-v") >= 0;
        if (Array.IndexOf(args, "--write-fixture") >= 0)
        {
            var path = QuestDetect3D.Tests.AnnotationLogicTests.FixturePath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, QuestDetect3D.Tests.AnnotationLogicTests.FixtureJson());
            Console.WriteLine("wrote " + path);
        }

        var pipeline = QuestDetect3D.Tests.PipelineTests.RunAll();
        Console.Write(pipeline.ToString());
        var annotation = QuestDetect3D.Tests.AnnotationTests.RunAll();
        Console.Write(annotation.ToString());
        return pipeline.Failed + annotation.Failed == 0 ? 0 : 1;
    }
}
