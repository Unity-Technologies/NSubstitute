using Mono.Cecil;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    public interface ICopier
    {
        void Copy(AssemblyDefinition source, ref AssemblyDefinition target, AssemblyDefinition nsubstitute, string[] typesToCopy);
    }
}