using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Rocks;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    public class ReferenceRewriter
    {
        const string k_FakeNamespace = "Fake";
        Predicate<TypeReference> m_ShouldSkip = tr => false;
        const string m_ToNamespacePrefix = k_FakeNamespace;
        const string m_FromNamespacePrefix = "";

        public ReferenceRewriter(Predicate<TypeReference> shouldSkip = null)
        {
            if (shouldSkip != null)
                m_ShouldSkip = shouldSkip;
        }

        // assumes generic parameter has same names in original and target
        public TypeReference Rewrite(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeReference reference, MethodDefinition method = null, MethodDefinition methodDefinition = null, Dictionary<MethodDefinition, MethodDefinition> methodMap = null, bool lookupFake = true, TypeReference selfFakeHolder = null)
        {
            return Rewrite(target, reference, methodMap, lookupFake, selfFakeHolder: selfFakeHolder);
        }

        public TypeReference Rewrite(AssemblyDefinition targetAssembly, TypeReference reference, Dictionary<MethodDefinition, MethodDefinition> methodMap, bool lookupFake = true, bool useFakeForGenericParameters = false, TypeReference selfFakeHolder = null)
        {
            if (reference == null)
                return null;

            if (reference.IsGenericParameter)
                return RewriteGenericParameter(targetAssembly, reference, methodMap, lookupFake, useFakeForGenericParameters, selfFakeHolder);

            if (reference.IsArray)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakeArrayType(((ArrayType)reference).Rank);

            if (reference.IsByReference)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakeByReferenceType();

            if (reference.IsPointer)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakePointerType();

            if (reference.IsPinned)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakePinnedType();

            if (reference.IsOptionalModifier)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakeOptionalModifierType(((OptionalModifierType)reference).ModifierType);

            if (reference.IsSentinel)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakeSentinelType();

            if (reference.IsRequiredModifier)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake, selfFakeHolder: selfFakeHolder).MakeRequiredModifierType(((RequiredModifierType)reference).ModifierType);

            if (!m_ShouldSkip(reference))
            {
                TypeReference typeRef;
                if (reference.IsNested)
                {
                    typeRef = new TypeReference("", reference.Name, targetAssembly.MainModule, targetAssembly.MainModule);
                    var rewrittenDeclaringType = Rewrite(targetAssembly, reference.DeclaringType, methodMap, lookupFake, selfFakeHolder: selfFakeHolder);
                    typeRef.DeclaringType = rewrittenDeclaringType;
                }
                else
                {
                    if (lookupFake && !(reference.Namespace.StartsWith("Fake.") || reference.Namespace == "Fake"))
                    {
                        var newNameSpace = RewriteNamespace(reference.Namespace);
                        typeRef = new TypeReference(newNameSpace, RewriteGenericTypeName(reference), targetAssembly.MainModule, targetAssembly.MainModule);
                    }
                    else
                    {
                        typeRef = new TypeReference(reference.Namespace, reference.Name, reference.Module, reference.Scope);
                        typeRef = targetAssembly.MainModule.Import(typeRef);
                    }
                }

                var origInstanceType = reference as GenericInstanceType;
                if (origInstanceType != null)
                {
                    var targetInstanceType = new GenericInstanceType(typeRef);
                    if (origInstanceType.HasGenericArguments)
                    {
                        foreach (var genericArgument in origInstanceType.GenericArguments)
                        {
                            var rewrittenArgument = Rewrite(targetAssembly, genericArgument, methodMap, lookupFake, selfFakeHolder: selfFakeHolder);
                            if (!rewrittenArgument.IsGenericParameter && !IsFaked(rewrittenArgument) && selfFakeHolder != null)
                                rewrittenArgument = selfFakeHolder.MakeGenericInstanceType(rewrittenArgument);
                            targetInstanceType.GenericArguments.Add(rewrittenArgument);
                        }
                        if (targetInstanceType.Namespace == k_FakeNamespace || (targetInstanceType.Namespace?.StartsWith($"{k_FakeNamespace}.") ?? false))
                        {
                            foreach (var genericArgument in origInstanceType.GenericArguments)
                            {
                                var rewrittenArgument = Rewrite(targetAssembly, genericArgument, methodMap, lookupFake: false, useFakeForGenericParameters: true, selfFakeHolder: selfFakeHolder);
                                targetInstanceType.GenericArguments.Add(rewrittenArgument);
                            }
                        }
                    }
                    typeRef = targetInstanceType;
                }

                if (reference.HasGenericParameters)
                {
                    foreach (var genericParameter in reference.GenericParameters)
                    {
                        typeRef.GenericParameters.Add(new GenericParameter(genericParameter.Name, typeRef));
                    }
                }

                return typeRef;
            }

            var importedTypeReference = targetAssembly.MainModule.Import(reference.Resolve());
            if (reference.IsGenericInstance)
                return importedTypeReference.MakeGenericInstanceType(((GenericInstanceType)reference).GenericArguments.Select(a => Rewrite(targetAssembly, a, methodMap, lookupFake, selfFakeHolder: selfFakeHolder)).ToArray());

            return importedTypeReference;
        }

        private bool IsFaked(TypeReference reference)
        {
            return reference.IsNested
                ? IsFaked(reference.DeclaringType)
                : reference.Namespace == k_FakeNamespace ||
                  reference.Namespace.StartsWith($"{k_FakeNamespace}.");
        }

        private TypeReference RewriteGenericParameter(AssemblyDefinition targetAssembly, TypeReference reference, Dictionary<MethodDefinition, MethodDefinition> methodMap, bool lookupFake, bool useFakeForGenericParameters, TypeReference selfFakeHolder)
        {
            TypeReference rewrittenDeclaringType;
            var genericParameter = ((GenericParameter) reference);
            if (reference.DeclaringType == null)
            {
                rewrittenDeclaringType =
                    Rewrite(targetAssembly, genericParameter.DeclaringMethod.DeclaringType, methodMap, lookupFake);
                var originalMethod = genericParameter.DeclaringMethod.Resolve();

                var rewrittenDeclaringTypeDefinition = rewrittenDeclaringType.Resolve();

                //var originalMethodTableIndex = originalMethod.DeclaringType.Methods.IndexOf(originalMethod);
                //for (var i = 0; i < originalMethodTableIndex; ++i)
                //    if (originalMethod.DeclaringType.Methods[i].IsConstructor)
                //        --originalMethodTableIndex;
                //if (HasForwardingConstructorInMethodCollection(genericParameter, rewrittenDeclaringTypeDefinition))
                //    ++originalMethodTableIndex;

                //var targetMethod = rewrittenDeclaringTypeDefinition.Methods[originalMethodTableIndex];
                var targetMethod = methodMap[originalMethod]; // TODO: Pray

                if (genericParameter.Position < 0 || genericParameter.Position >= targetMethod.GenericParameters.Count)
                    throw new InvalidOperationException("Unknown generic method type parameter");

                return targetMethod.GenericParameters[genericParameter.Position];
            }
            else
            {
                rewrittenDeclaringType = Rewrite(targetAssembly, reference.DeclaringType, methodMap, lookupFake || useFakeForGenericParameters);
                rewrittenDeclaringType = rewrittenDeclaringType.Resolve();

                if (genericParameter.Position < 0 || genericParameter.Position >= rewrittenDeclaringType.GenericParameters.Count)
                    throw new InvalidOperationException("Unknown generic type parameter");

                if (!lookupFake && useFakeForGenericParameters)
                    return rewrittenDeclaringType.GenericParameters[
                        genericParameter.Position + rewrittenDeclaringType.GenericParameters.Count / 2];

                return rewrittenDeclaringType.GenericParameters[genericParameter.Position];
            }
        }

        public TypeReference ImportRecursively(AssemblyDefinition target, TypeReference reference)
        {
            if (reference == null)
                return null;


            if (reference.IsGenericParameter)
                return reference;

            if (reference.IsArray)
                return ImportRecursively(target, reference.GetElementType()).MakeArrayType(((ArrayType)reference).Rank);
 
            if (reference.IsByReference)
                return ImportRecursively(target, reference.GetElementType()).MakeByReferenceType();

            if (reference.IsPointer)
                return ImportRecursively(target, reference.GetElementType()).MakePointerType();

            if (reference.IsPinned)
                return ImportRecursively(target, reference.GetElementType()).MakePinnedType();

            if (reference.IsOptionalModifier)
                return ImportRecursively(target, reference.GetElementType()).MakeOptionalModifierType(((OptionalModifierType)reference).ModifierType);

            if (reference.IsSentinel)
                return ImportRecursively(target, reference.GetElementType()).MakeSentinelType();

            if (reference.IsRequiredModifier)
                return ImportRecursively(target, reference.GetElementType()).MakeRequiredModifierType(((RequiredModifierType)reference).ModifierType);

            if (reference is GenericInstanceType genericInstance)
            {
                return target.MainModule.Import(genericInstance.Resolve()).MakeGenericInstanceType(genericInstance
                    .GenericArguments.Select(ga => ImportRecursively(target, ga)).ToArray());
            }

            return target.MainModule.Import(reference);
        }

        public TypeReference ReplaceGenericParameter(TypeReference reference, GenericParameter[] original,
            GenericParameter[] target)
        {
            if (reference == null)
                return null;

            if (reference.IsGenericParameter)
            {
                for (var i = 0; i < original.Length; ++i)
                    if (reference == original[i])
                        return target[i];
                return reference;
            }

            if (reference.IsArray)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakeArrayType(((ArrayType)reference).Rank);

            if (reference.IsByReference)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakeByReferenceType();

            if (reference.IsPointer)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakePointerType();

            if (reference.IsPinned)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakePinnedType();

            if (reference.IsOptionalModifier)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakeOptionalModifierType(((OptionalModifierType)reference).ModifierType);

            if (reference.IsSentinel)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakeSentinelType();

            if (reference.IsRequiredModifier)
                return ReplaceGenericParameter(reference.GetElementType(), original, target).MakeRequiredModifierType(((RequiredModifierType)reference).ModifierType);

            if (reference is GenericInstanceType genericInstance)
            {
                return genericInstance.Resolve().MakeGenericInstanceType(genericInstance.GenericArguments
                    .Select(ga => ReplaceGenericParameter(ga, original, target)).ToArray());
            }

            if (reference.HasGenericParameters)
            {
                throw new NotImplementedException();
            }

            return reference;

        }

        public TypeReference AddTickToName(AssemblyDefinition target, TypeReference reference)
        {
            if (reference == null)
                return null;


            if (reference.IsGenericParameter)
                return reference;

            if (reference.IsArray)
                return AddTickToName(target, reference.GetElementType()).MakeArrayType(((ArrayType)reference).Rank);

            if (reference.IsByReference)
                return AddTickToName(target, reference.GetElementType()).MakeByReferenceType();

            if (reference.IsPointer)
                return AddTickToName(target, reference.GetElementType()).MakePointerType();

            if (reference.IsPinned)
                return AddTickToName(target, reference.GetElementType()).MakePinnedType();

            if (reference.IsOptionalModifier)
                return AddTickToName(target, reference.GetElementType()).MakeOptionalModifierType(((OptionalModifierType)reference).ModifierType);

            if (reference.IsSentinel)
                return AddTickToName(target, reference.GetElementType()).MakeSentinelType();

            if (reference.IsRequiredModifier)
                return AddTickToName(target, reference.GetElementType()).MakeRequiredModifierType(((RequiredModifierType)reference).ModifierType);

            if (reference is GenericInstanceType genericInstance)
            {
                genericInstance.Name = $"{genericInstance.Name}`{reference.GenericParameters.Count}";
                return genericInstance;
            }

            if (reference.HasGenericParameters)
            {
                reference.Name = $"{reference.Name}`{reference.GenericParameters.Count}";
                return reference;
            }

            return reference;
        }

        static string RewriteGenericTypeName(TypeReference type)
        {
            var name = type.Name;
            if (name.Contains("`"))
            {
                var parts = name.Split('`');
                name = $"{parts[0]}`{int.Parse(parts[1]) * 2}";
            }
            return name;
        }

        static bool HasForwardingConstructorInMethodCollection(GenericParameter genericParameter, TypeDefinition rewrittenDeclaringTypeDefinition)
        {
            // TODO: nested types
            return !genericParameter.DeclaringMethod.DeclaringType.Resolve().IsInterface && (rewrittenDeclaringTypeDefinition.Namespace.StartsWith("Fake.") || rewrittenDeclaringTypeDefinition.Namespace == "Fake");
        }

        public string RewriteNamespace(string referenceNamespace)
        {
            if (referenceNamespace == m_ToNamespacePrefix || referenceNamespace.StartsWith(m_ToNamespacePrefix + "."))
                return referenceNamespace;

            if (referenceNamespace == m_FromNamespacePrefix)
                return m_ToNamespacePrefix;

            if (referenceNamespace.StartsWith(m_FromNamespacePrefix + "."))
                return m_ToNamespacePrefix + "." + referenceNamespace.Substring(m_FromNamespacePrefix.Length + 1);

            if (m_FromNamespacePrefix == "")
                return m_ToNamespacePrefix + "." + referenceNamespace;

            //return referenceNamespace;
            //if (!(referenceNamespace == m_FromNamespacePrefix || referenceNamespace.StartsWith(m_FromNamespacePrefix + ".") || m_FromNamespacePrefix == ""))
            //    return referenceNamespace;
            ////if (!referenceNamespace.StartsWith(m_FromNamespacePrefix))
            ////    return referenceNamespace;
            ////if (m_FromNamespacePrefix.Length > 0 && referenceNamespace.Length > m_FromNamespacePrefix.Length && referenceNamespace.ToCharArray()[m_FromNamespacePrefix.Length] != '.')
            ////{
            ////    return referenceNamespace;
            ////}
            ////if (referenceNamespace == m_FromNamespacePrefix)
            ////{
            ////    return m_ToNamespacePrefix;
            ////}
            //string prefixes = referenceNamespace.Substring(m_FromNamespacePrefix.Length);
            //return m_ToNamespacePrefix + "." + prefixes;


            //var toWithDot = m_ToNamespacePrefix + ".";
            //return ((referenceNamespace.StartsWith(toWithDot) ? "" : toWithDot) + referenceNamespace).TrimEnd('.');
        }


        public MethodReference Rewrite(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, MethodReference methodReference, Dictionary<MethodDefinition, MethodDefinition> methodMap)
        {
            var methodDeclaringType = methodReference.DeclaringType;
            var rewrittenMethodDeclaringType = Rewrite(target, type, typeDefinition, methodDeclaringType, method, methodDefinition);
            if (rewrittenMethodDeclaringType.Namespace != k_FakeNamespace && !rewrittenMethodDeclaringType.Namespace.StartsWith($"{k_FakeNamespace}."))
                return Reinstantiate(target, type, typeDefinition, method, methodDefinition, methodReference, methodMap);

            var rewrittenMethodDeclaringTypeDefinition = rewrittenMethodDeclaringType.Resolve();
            MethodDefinition candidate;
            if (!methodMap.TryGetValue(methodReference.Resolve(), out candidate))
                throw new InvalidOperationException($"Unknown method attempted to be rewritten '{methodReference}'");

            return candidate;
        }

        public MethodReference Reinstantiate(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, MethodReference reference, Dictionary<MethodDefinition, MethodDefinition> methodMap)
        {
            if (reference.IsGenericInstance)
            {
                var originalMethod = target.MainModule.Import(reference.Resolve());
                var targetMethod = new GenericInstanceMethod(originalMethod);
                var methodInstance = (GenericInstanceMethod)reference;
                foreach (var arg in methodInstance.GenericArguments)
                    targetMethod.GenericArguments.Add(Rewrite(target, type, typeDefinition, arg, method, methodDefinition, methodMap, false));
                return targetMethod;
            }

            if (reference.HasGenericParameters)
            {
                var originalMethod = target.MainModule.Import(reference);
                var targetMethod = new GenericInstanceMethod(originalMethod);
                foreach (var arg in reference.GenericParameters)
                    targetMethod.GenericArguments.Add(Rewrite(target, type, typeDefinition, arg, method, methodDefinition, methodMap, false));

                return targetMethod;
            }

            if (!(reference is GenericInstanceMethod))
            {
                MethodReference importedReference;

                if (reference.Module != null)
                    importedReference = target.MainModule.Import(reference);
                else
                    importedReference = reference;

                //if (importedReference.DeclaringType.HasGenericParameters && !importedReference.DeclaringType.IsGenericInstance)
                //    importedReference = importedReference.MakeHostInstanceGeneric(target, importedReference.DeclaringType.GenericParameters.ToArray());

                return importedReference;
            }

            return null;
        }
    }
}
