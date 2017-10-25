using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Unity.Cecil.Visitor;

namespace NSubstitute.Weaver
{
    class ProcessTypeResolver
    {
        readonly AssemblyDefinition m_Assembly;

        public ProcessTypeResolver(AssemblyDefinition assembly)
        {
            m_Assembly = assembly;
        }

        public IEnumerable<TypeDefinition> Resolve(IEnumerable<string> typesToCopy)
        {
            var toCopy = new HashSet<string>(typesToCopy);

            var types = new List<TypeDefinition>(m_Assembly.MainModule.Types.Where(t => toCopy.Contains(t.FullName)));
            types.Sort((lhs, rhs) =>
                {
                    var lhsChain = TypeReferenceExtensions.InheritanceChainLength(lhs);
                    var rhsChain = TypeReferenceExtensions.InheritanceChainLength(rhs);
                    return lhsChain.CompareTo(rhsChain);
                });
            return types;
        }
    }
}
