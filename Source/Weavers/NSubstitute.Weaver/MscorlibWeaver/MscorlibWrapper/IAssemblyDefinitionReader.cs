using Mono.Cecil;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    public interface IAssemblyDefinitionReader
    {
        AssemblyDefinition Read(string path);
    }
}
