#define PEVERIFY

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CSharp;
using Mono.Cecil;
using NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper;
using NUnit.Framework;
using Shouldly;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver
{
    [TestFixture]
    class AcceptanceTests
    {
        const string pathPrefix = @"c:\users\henriks\documents\visual studio 2017\Projects\FakeMscorlibRunner\FakeMscorlibRunner";

        [Test]
        public void CopyEverything()
        {
            var mscorlib = AssemblyDefinition.ReadAssembly(typeof(void).Assembly.Location);
            var nsubstitute = AssemblyDefinition.ReadAssembly(typeof(Substitute).Assembly.Location);
            var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("mscorlib.fake", mscorlib.Name.Version), mscorlib.MainModule.Name, ModuleKind.Dll);

            var copier = new Copier(new ProcessTypeResolver(mscorlib));
            copier.Copy(mscorlib, ref target, nsubstitute, mscorlib.MainModule.Types.Select(t => t.FullName).ToArray());

            target.Write(Path.Combine(pathPrefix, "fake.mscorlib.dll"));
        }

        static string GetMethodName([CallerMemberName] string method = null)
        {
            return $"{method}.dll";
        }

        [Test]
        public void SimplePublicClassGetsFakeFieldAndCtor()
        {
            var targetAndLib = CreateAssemblyFromCode(@"public class A {}");
            var target = targetAndLib.Item1;
            var mscorlib = targetAndLib.Item2;

            target.MainModule.Types.ShouldContain(t => t.Name == "A");

            var targetType = target.MainModule.Types.Single(t => t.Name == "A");
            targetType.FullName.ShouldBe("Fake.A");
            targetType.BaseType.Resolve().ShouldBe(target.MainModule.TypeSystem.Object.Resolve());
            targetType.Fields.Count.ShouldBe(1);

            var forwardField = targetType.Fields[0];
            forwardField.Name.ShouldBe("__fake_forward");
            forwardField.FieldType.Resolve().ShouldBe(mscorlib.MainModule.Types.Single(t => t.Name == "A").Resolve());

            WriteForPeVerify(target);
        }

        static void WriteForPeVerify(AssemblyDefinition target)
        {
#if PEVERIFY
            target.Write(Path.Combine(pathPrefix, GetMethodName()));
#endif
        }

        static AssemblyDefinition EmitAndReadAssembly(CSharpCompilation csc)
        {
#if PEVERIFY
            var memoryAssemblyPath = Path.Combine(pathPrefix, "InMemoryAssembly.dll");
            csc.Emit(memoryAssemblyPath);
            return AssemblyDefinition.ReadAssembly(memoryAssemblyPath);
#else
            var ms = new MemoryStream();
            var r = csc.Emit(ms);
            r.Success.ShouldBeTrue();
            ms.Position = 0;
            return AssemblyDefinition.ReadAssembly(ms);
#endif
        }

        static Tuple<AssemblyDefinition, AssemblyDefinition> CreateAssemblyFromCode(string program)
        {
            var st = CSharpSyntaxTree.ParseText(program);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug, allowUnsafe: true);
            var csc = CSharpCompilation.Create("InMemoryAssembly", options: options, syntaxTrees: new[] { st }, references: new[]
            {
                MetadataReference.CreateFromFile(typeof(Enum).Assembly.Location)
            });

            var mscorlib = EmitAndReadAssembly(csc);
            var nsubstitute = AssemblyDefinition.ReadAssembly(typeof(Substitute).Assembly.Location);
            var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("mscorlib.fake", mscorlib.Name.Version), mscorlib.MainModule.Name, ModuleKind.Dll);

            var copier = new Copier(new ProcessTypeResolver(mscorlib));
            copier.Copy(mscorlib, ref target, nsubstitute, mscorlib.MainModule.Types.Select(t => t.FullName).ToArray());
            return Tuple.Create(target, mscorlib);
        }
    }
}
