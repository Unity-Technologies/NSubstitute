using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Unity.Cecil.Visitor;

namespace NSubstitute.Weaver
{
    public static class MockWeaver
    {
        public static void InjectFakes(string assemblyToPatchPath, string nsubstituteAssemblyPath)
        {
            using (var sourceAssemblyFile = File.OpenRead(assemblyToPatchPath))
            {
                var outputAssemblyPath = Path.GetFileName(assemblyToPatchPath) + ".patched";
                InjectFakes(sourceAssemblyFile, outputAssemblyPath, nsubstituteAssemblyPath, Path.GetDirectoryName(assemblyToPatchPath));
            }
        }

        public static void InjectFakes(Stream assemblyToPatchFile, string outputAssemblyPath, string nsubstituteAssemblyPath, string assemblySearchPath = null)
        {
            var readerParams = new ReaderParameters();
            if (assemblySearchPath != null)
            {
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(assemblySearchPath);
                readerParams.AssemblyResolver = resolver;
            }

            if (nsubstituteAssemblyPath == null)
                nsubstituteAssemblyPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "NSubstitute.dll");

            var assemblyToPatch = AssemblyDefinition.ReadAssembly(assemblyToPatchFile, readerParams);
            assemblyToPatch.Accept(new MockInjectorVisitor(AssemblyDefinition.ReadAssembly(nsubstituteAssemblyPath), assemblyToPatch.MainModule));
            assemblyToPatch.Write(outputAssemblyPath, new WriterParameters { WriteSymbols = true});
        }
    }
}
