using Mono.Cecil;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    public class AssemblyDefinitionReader : IAssemblyDefinitionReader
    {
        public AssemblyDefinition Read(string path)
        {
            return AssemblyDefinition.ReadAssembly(path);
        }
    }
}
