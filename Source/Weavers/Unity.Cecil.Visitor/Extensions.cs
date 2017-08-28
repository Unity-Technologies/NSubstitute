using Mono.Cecil;

namespace Unity.Cecil.Visitor
{
    public static class Extensions
    {
        public static void Accept(this AssemblyDefinition assemblyDefinition, Visitor visitor)
        {
            assemblyDefinition.DoAccept(visitor);
        }

        public static void Accept(this ModuleDefinition moduleDefinition, Visitor visitor)
        {
            moduleDefinition.DoAccept(visitor);
        }

        public static void Accept(this TypeDefinition typeDefinition, Visitor visitor)
        {
            typeDefinition.DoAccept(visitor);
        }

        public static void Accept(this GenericInstanceType genericInstanceType, Visitor visitor)
        {
            genericInstanceType.DoAccept(visitor);
        }

        public static void Accept(this PointerType pointerType, Visitor visitor)
        {
            pointerType.DoAccept(visitor);
        }

        public static void Accept(this ArrayType arrayType, Visitor visitor)
        {
            arrayType.DoAccept(visitor);
        }

        public static void Accept(this FieldDefinition fieldDefinition, Visitor visitor)
        {
            fieldDefinition.DoAccept(visitor);
        }

        public static void Accept(this MethodDefinition methodDefinition, Visitor visitor)
        {
            methodDefinition.DoAccept(visitor);
        }

        public static void Accept(this PropertyDefinition propertyDefinition, Visitor visitor)
        {
            propertyDefinition.DoAccept(visitor);
        }

        private static void DoAccept<T>(this T definition, Visitor visitor) where T : class
        {
            if (visitor == null || definition == null)
                return;

            visitor.Visit(definition, Context.None);
        }
    }
}
