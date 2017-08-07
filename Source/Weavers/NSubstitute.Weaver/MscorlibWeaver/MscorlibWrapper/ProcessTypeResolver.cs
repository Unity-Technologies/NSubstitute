using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace NSubstitute.Weaver
{
    class ProcessTypeResolver
    {
        readonly AssemblyDefinition m_Assembly;

        public ProcessTypeResolver(AssemblyDefinition assembly)
        {
            m_Assembly = assembly;
        }

        static int InheritanceChainLength(TypeReference type)
        {
            var baseType = type.Resolve().BaseType;
            if (baseType == null)
                return 1;
            return 1 + InheritanceChainLength(baseType);
        }

        public IEnumerable<TypeDefinition> Resolve(IEnumerable<string> typesToCopy)
        {
            var toCopy = new HashSet<string>(typesToCopy);

            var types = new List<TypeDefinition>(m_Assembly.MainModule.Types.Where(t => toCopy.Contains(t.FullName)));
            types.Sort((lhs, rhs) =>
                {
                    var lhsChain = InheritanceChainLength(lhs);
                    var rhsChain = InheritanceChainLength(rhs);
                    return lhsChain.CompareTo(rhsChain);
                });
            return types;
        }
    }
}
