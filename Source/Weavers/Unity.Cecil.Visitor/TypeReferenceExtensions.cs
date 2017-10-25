using Mono.Cecil;

namespace Unity.Cecil.Visitor
{
    public static class TypeReferenceExtensions
    {
        public static int InheritanceChainLength(TypeReference type)
        {
            if (type.DeclaringType == null)
                return 0;
            var baseType = type.Resolve().BaseType;
            if (baseType == null)
                return 1;
            return 1 + InheritanceChainLength(baseType);
        }
    }
}
