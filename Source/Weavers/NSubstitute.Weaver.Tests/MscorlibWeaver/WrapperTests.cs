using System;
using Mono.Cecil;
using NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper;
using NUnit.Framework;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver
{
    [TestFixture]
    class WrapperTests
    {
        [Test]
        public void TestAssemblyRead()
        {
            const string mscorlib = @"mscorlib.dll";
            const string nsubstitute = @"nsubstitute.dll";
            var copier = Substitute.For<ICopier>();
            var reader = Substitute.For<IAssemblyDefinitionReader>();
            var mscorlibDefinition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("mscorlib", new Version(999, 999)), "MAINMODULE", ModuleKind.Dll);
            reader.Read(mscorlib).Returns(mscorlibDefinition);
            var nsubstituteDefinition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("nsubstitute", new Version(998, 998)), "NSUBMAIN", ModuleKind.Dll);
            reader.Read(nsubstitute).Returns(nsubstituteDefinition);
            var wrapper = new Wrapper(copier, reader);

            var fakedDefinition = wrapper.Wrap(mscorlib, nsubstitute);

            copier.Received().Copy(mscorlibDefinition, ref fakedDefinition, nsubstituteDefinition, Arg.Any<string[]>());
        }
    }
}
