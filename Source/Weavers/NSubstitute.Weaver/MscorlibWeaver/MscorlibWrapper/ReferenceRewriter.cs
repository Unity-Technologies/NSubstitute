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
        public TypeReference Rewrite(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeReference reference, MethodDefinition method = null, MethodDefinition methodDefinition = null, Dictionary<MethodDefinition, MethodDefinition> methodMap = null, bool lookupFake = true)
        {
//            return OldRewrite(target, type, typeDefinition, reference, method, methodDefinition, lookupFake);
            return Rewrite(target, reference, methodMap, lookupFake);
        }

        public TypeReference OldRewrite(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeReference reference, MethodDefinition method = null, MethodDefinition methodDefinition = null, bool lookupFake = true)
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

            var import = !targetType.IsGenericParameter;
            if (targetType.IsArray)
            {
                targetType = Rewrite(target, type, typeDefinition, targetType.GetElementType(), method, methodDefinition, null, lookupFake).MakeArrayType();
                import = false;
            }
            if (targetType.IsByReference)
            {
                targetType = Rewrite(target, type, typeDefinition, targetType.GetElementType(), method, methodDefinition, null, lookupFake).MakeByReferenceType();
                import = false;
            }

            if (import)
                targetType = target.MainModule.Import(targetType);

            if (reference is GenericParameter)
            {
                var genericParameter = (GenericParameter)reference;
                if (genericParameter.DeclaringMethod == null)
                {
                    var targetGenericParameterType = typeDefinition.GenericParameters.FirstOrDefault(gp => gp.Name == genericParameter.Name);
                    if (targetGenericParameterType == null)
                        throw new InvalidOperationException("Unknown generic parameter reference");
                    return targetGenericParameterType;
                }
                if (method == null || methodDefinition == null)
                    throw new InvalidOperationException("Unknown generic parameter reference");
                var targetMethodGenericParameterType = methodDefinition.GenericParameters.FirstOrDefault(gp => gp.Name == genericParameter.Name);
                if (targetMethodGenericParameterType == null)
                    throw new InvalidOperationException("Unknown generic parameter reference");
                return targetMethodGenericParameterType;
            }

            if (reference.IsGenericInstance)
            {
                var genericInstanceType = (GenericInstanceType)reference;
                var genericParameters = genericInstanceType.GenericArguments.Select(ga => Rewrite(target, type, typeDefinition, ga, method, methodDefinition, null, lookupFake)).ToArray();
                targetType = targetType.MakeGenericInstanceType(genericParameters);
            }

            return targetType;
        }

        public TypeReference Rewrite(AssemblyDefinition targetAssembly, TypeReference reference, Dictionary<MethodDefinition, MethodDefinition> methodMap, bool lookupFake = true)
        {
            if (reference == null)
                return null;

            if (reference.IsGenericParameter)
            {
                TypeReference rewrittenDeclareingType;
                var genericParameter = ((GenericParameter)reference);
                if (reference.DeclaringType == null)
                {
                    rewrittenDeclareingType = Rewrite(targetAssembly, genericParameter.DeclaringMethod.DeclaringType, methodMap, lookupFake);
                    var originalMethod = genericParameter.DeclaringMethod.Resolve();

                    var rewrittenDeclaringTypeDefinition = rewrittenDeclareingType.Resolve();

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
                    rewrittenDeclareingType = Rewrite(targetAssembly, reference.DeclaringType, methodMap, lookupFake);
                    rewrittenDeclareingType = rewrittenDeclareingType.Resolve();

                    if (genericParameter.Position < 0 || genericParameter.Position >= rewrittenDeclareingType.GenericParameters.Count)
                        throw new InvalidOperationException("Unknown generic type parameter");

                    return rewrittenDeclareingType.GenericParameters[genericParameter.Position];
                }
            }

            if (reference.IsArray)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakeArrayType(((ArrayType)reference).Rank);

            if (reference.IsByReference)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakeByReferenceType();

            if (reference.IsPointer)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakePointerType();

            if (reference.IsPinned)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakePinnedType();

            if (reference.IsOptionalModifier)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakeOptionalModifierType(((OptionalModifierType)reference).ModifierType);

            if (reference.IsSentinel)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakeSentinelType();

            if (reference.IsRequiredModifier)
                return Rewrite(targetAssembly, reference.GetElementType(), methodMap, lookupFake).MakeRequiredModifierType(((RequiredModifierType)reference).ModifierType);

            if (!m_ShouldSkip(reference))
            {
                TypeReference typeRef;
                if (reference.IsNested)
                {
                    typeRef = new TypeReference("", reference.Name, targetAssembly.MainModule, targetAssembly.MainModule);
                    var rewrittenDeclaringType = Rewrite(targetAssembly, reference.DeclaringType, methodMap, lookupFake);
                    typeRef.DeclaringType = rewrittenDeclaringType;
                }
                else
                {
                    if (lookupFake)
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
                            var rewrittenArgument = Rewrite(targetAssembly, genericArgument, methodMap, lookupFake);
                            targetInstanceType.GenericArguments.Add(rewrittenArgument);
                        }
                        if (targetInstanceType.Namespace == k_FakeNamespace || (targetInstanceType.Namespace?.StartsWith($"{k_FakeNamespace}.") ?? false))
                        {
                            foreach (var genericArgument in origInstanceType.GenericArguments)
                            {
                                var rewrittenArgument = Rewrite(targetAssembly, genericArgument, methodMap, lookupFake: false);
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
                    foreach (var genericParameter in reference.GenericParameters)
                        typeRef.GenericParameters.Add(new GenericParameter($"__{genericParameter.Name}", typeRef));
                }

                return typeRef;
            }

            var importedTypeReference = targetAssembly.MainModule.Import(reference.Resolve());
            if (reference.IsGenericInstance)
                return importedTypeReference.MakeGenericInstanceType(((GenericInstanceType)reference).GenericArguments.Select(a => Rewrite(targetAssembly, a, methodMap, lookupFake)).ToArray());

            return importedTypeReference;
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
