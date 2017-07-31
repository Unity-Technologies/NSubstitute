using Mono.Cecil;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    public class Wrapper
    {
        readonly ICopier m_Copier;
        readonly IAssemblyDefinitionReader m_Reader;
        static string[] s_TypesToCopy = { "System.Text.StringBuilder", "System.DateTime", "System.IO.File", "System.IO.Path", "System.Console", "System.Threading.Thread" };

        public Wrapper(ICopier copier, IAssemblyDefinitionReader reader)
        {
            m_Copier = copier;
            m_Reader = reader;
        }

        /// <summary>
        /// Creates a new version of mscorlib with injected calls to NSubstitute for supported types.
        /// </summary>
        /// <param name="mscorlibPath">Path to the mscorlib assembly being wrapped.</param>
        /// <param name="nsubstitutePath">Path to the NSubstitute assembly.</param>
        /// <returns>Copy of mscorlib with injected calls to NSubstitute.</returns>
        public AssemblyDefinition Wrap(string mscorlibPath, string nsubstitutePath)
        {
            var mscorlibDefinition = m_Reader.Read(mscorlibPath);
            var nsubstituteDefinition = m_Reader.Read(nsubstitutePath);

            var injectedMscorlibDefinition =
                AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("mscorlib.fake", mscorlibDefinition.Name.Version),
                    mscorlibDefinition.MainModule.Name, mscorlibDefinition.MainModule.Kind);

            m_Copier.Copy(mscorlibDefinition, ref injectedMscorlibDefinition, nsubstituteDefinition, s_TypesToCopy);

            return injectedMscorlibDefinition;
        }
    }
}
