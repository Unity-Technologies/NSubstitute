using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Rocks;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    public class ReferenceRewriter
    {
        const string k_FakeNamespace = "Fake";

        public TypeReference Rewrite(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeReference reference, MethodDefinition method = null, MethodDefinition methodDefinition = null, bool lookupFake = true)
        {
            if (reference == null)
                return null;

            var targetType = reference;

            if (targetType.IsGenericInstance)
                targetType = targetType.Resolve();

            TypeReference targetReferenceDefinition = null;
            if (lookupFake)
                targetReferenceDefinition = target.MainModule.Types.SingleOrDefault(t => reference.Name == t.Name && t.Namespace == (reference.Namespace == "" ? "Fake" : $"Fake.{reference.Namespace}"));

            if (targetReferenceDefinition != null)
                targetType = targetReferenceDefinition;

            if (!targetType.IsGenericParameter)
                targetType = target.MainModule.Import(targetType);

            if (reference is GenericParameter)
            {
                var idx = type.GenericParameters.IndexOf((GenericParameter)reference);
                if (idx != -1)
                    targetType = typeDefinition.GenericParameters[idx];
                else if (method != null && methodDefinition != null)
                {
                    idx = method.GenericParameters.IndexOf((GenericParameter)reference);
                    if (idx != -1)
                        targetType = methodDefinition.GenericParameters[idx];
                    else
                        throw new InvalidOperationException("Unknown generic parameter reference");
                }
                else
                    throw new InvalidOperationException("Unknown generic parameter reference");
            }

            if (reference.IsGenericInstance)
            {
                var genericInstanceType = (GenericInstanceType)reference;
                var genericParameters = genericInstanceType.GenericArguments.Select(ga => Rewrite(target, type, typeDefinition, ga)).ToArray();
                targetType = targetType.MakeGenericInstanceType(genericParameters);
            }

            return targetType;
        }

        public MethodReference Reinstantiate(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, MethodReference reference)
        {
            if (reference.IsGenericInstance)
            {
                var originalMethod = target.MainModule.Import(reference.Resolve());
                var targetMethod = new GenericInstanceMethod(originalMethod);
                var methodInstance = (GenericInstanceMethod)reference;
                foreach (var arg in methodInstance.GenericArguments)
                    targetMethod.GenericArguments.Add(Rewrite(target, type, typeDefinition, arg, method, methodDefinition));
                return targetMethod;
            }

            if (reference.HasGenericParameters)
            {
                var originalMethod = target.MainModule.Import(reference);
                var targetMethod = new GenericInstanceMethod(originalMethod);
                foreach (var arg in reference.GenericParameters)
                    targetMethod.GenericArguments.Add(Rewrite(target, type, typeDefinition, arg, method, methodDefinition));

                return targetMethod;
            }


            if (!(reference is GenericInstanceMethod))
            {
                if (reference.Module != null)
                    return target.MainModule.Import(reference);

                return reference;
            }

            return null;
        }
    }
}
