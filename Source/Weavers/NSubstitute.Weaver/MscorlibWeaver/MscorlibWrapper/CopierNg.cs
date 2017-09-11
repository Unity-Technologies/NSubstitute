﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    class Copier : ICopier
    {
        ReferenceRewriter rewriter = new ReferenceRewriter();

        public Copier(ProcessTypeResolver processTypeResolver)
        {
        }

        public void Copy(AssemblyDefinition source, ref AssemblyDefinition target, AssemblyDefinition nsubstitute, string[] typesToCopy)
        {
            var nonRefTarget = target;
            Dictionary<string, TypeDefinition> implementationTypes = new Dictionary<string, TypeDefinition>();
            var types = source.MainModule.Types.Where(t => !ShouldSkip(t)).Select(type => new { Definition = CreateTargetTypes(nonRefTarget, type, implementationTypes), OriginalType = type }).ToList();
            types.ForEach(t => CreateTypeFieldAndForwardConstructorDefinitionsIfNeeded(nonRefTarget, t.OriginalType, t.Definition, implementationTypes));
            types.ForEach(t => CreateForwardConstructorBodyAndFakeImplementationIfNeeded(nonRefTarget, t.OriginalType, t.Definition, implementationTypes));
            types.ForEach(t => AttachGenericTypeConstraints(nonRefTarget, t.OriginalType, t.Definition));
            types.ForEach(t => AttachInterfaces(nonRefTarget, t.OriginalType, t.Definition));
            types.ForEach(t => AttachMethods(nonRefTarget, t.OriginalType, t.Definition, implementationTypes));
            types.ForEach(t => PostprocessFakeImplementations(nonRefTarget, t.OriginalType, t.Definition, implementationTypes));
        }

        void PostprocessFakeImplementations(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes)
        {
            foreach (var prop in typeDefinition.Properties.Where(p => p.Name.Contains(k_FakeForwardPropertyNamePrefix)))
                typeDefinition.Methods.Add(prop.GetMethod);

            if (implementationTypes.TryGetValue(typeDefinition.FullName, out TypeDefinition implType))
                foreach (var prop in implType.Properties.Where(p => p.Name.Contains(k_FakeForwardPropertyNamePrefix)))
                    implType.Methods.Add(prop.GetMethod);
        }

        void AttachMethods(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes)
        {
            for (var index = 0; index < type.Methods.Count; index++)
            {
                var method = type.Methods[index];

                if (ShouldSkip(method))
                    continue;

                var returnType = target.MainModule.TypeSystem.Void; // method return type may depend on generic method parameters and need to be resolved later

                var methodDefinition = new MethodDefinition(method.Name, CreateMethodAttributes(method), returnType);
                methodDefinition.DeclaringType = typeDefinition;

                foreach (var gp in method.GenericParameters)
                    methodDefinition.GenericParameters.Add(new GenericParameter(gp.Name, methodDefinition));

                methodDefinition.ReturnType = RewriteTypeReference(target, type, typeDefinition, method, methodDefinition, method.ReturnType);

                foreach (var p in method.Parameters)
                    methodDefinition.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, RewriteTypeReference(target, type, typeDefinition, method, methodDefinition, p.ParameterType)));

                if (!NeedsFakeImplementation(type))
                    CreateWrappingMethodBody(target, type, methodDefinition, method, typeDefinition, type.Methods[index], implementationTypes);

                typeDefinition.Methods.Add(methodDefinition);
            }

            if (implementationTypes.TryGetValue(typeDefinition.FullName, out TypeDefinition implementationDefinition))
            {
                for (var index = 0; index < typeDefinition.Methods.Count; index++)
                {
                    var method = typeDefinition.Methods[index];

                    var implMethodDefinition = new MethodDefinition(method.Name, method.Attributes & ~MethodAttributes.Abstract, target.MainModule.TypeSystem.Void);
                    implMethodDefinition.DeclaringType = implementationDefinition;

                    implMethodDefinition.ReturnType = RewriteTypeReference(target, typeDefinition, implementationDefinition, method, implMethodDefinition, method.ReturnType);

                    foreach (var p in method.Parameters)
                        implMethodDefinition.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, RewriteTypeReference(target, typeDefinition, implementationDefinition, method, implMethodDefinition, p.ParameterType)));

                    CreateWrappingMethodBody(target, type, implMethodDefinition, method, implementationDefinition, type.Methods[index], implementationTypes);

                    implementationDefinition.Methods.Add(implMethodDefinition);
                }
            }
        }

        static MethodAttributes CreateMethodAttributes(MethodDefinition method)
        {
            if (method.DeclaringType.IsInterface)
                return method.Attributes;
            return method.Attributes & ~MethodAttributes.Abstract;
        }

        bool ShouldSkip(MethodDefinition method)
        {
            return method.IsConstructor;
        }

        TypeDefinition GetModuleTypeDefinition(AssemblyDefinition target, TypeReference reference)
        {
            if (reference == null)
                return null;

            if (reference.IsGenericInstance)
                reference = reference.Resolve();

            var fullName = reference.FullName;
            if (!fullName.StartsWith(k_NamespacePrefix))
                fullName = k_NamespacePrefix + "." + fullName;

            return target.MainModule.Types.SingleOrDefault(t => t.FullName == fullName);
        }

        void CreateWrappingMethodBody(AssemblyDefinition target, TypeDefinition type, MethodDefinition methodDefinition, MethodDefinition method, TypeDefinition typeDefinition, MethodDefinition originalMethod, Dictionary<string, TypeDefinition> implementationTypes)
        {
            methodDefinition.Body = new MethodBody(methodDefinition);

            if (!method.IsStatic)
            {
                methodDefinition.AddInstruction(OpCodes.Ldarg_0);

                var forwardField = typeDefinition.Fields.Single(f => f.Name == k_FakeForward);
                var loadFieldInstructionType = forwardField.FieldType.IsValueType ? OpCodes.Ldflda : OpCodes.Ldfld;
                methodDefinition.AddInstruction(loadFieldInstructionType, forwardField);
            }

            for (var i = 0; i < methodDefinition.Parameters.Count; i++)
            {
                var param = methodDefinition.Parameters[i];
                methodDefinition.AddInstruction(OpCodes.Ldarg, param);
                var paramType = GetModuleTypeDefinition(target, param.ParameterType);
                if (paramType == null)
                    continue;

                var propertyName = paramType.IsInterface ? GetFakeForwardPropertyName(paramType) : GetFakeForwardPropertyImplementationName(method.Parameters[i].ParameterType);

                var fakeForward = paramType.Properties.SingleOrDefault(p => p.Name == propertyName)?.GetMethod;
                if (fakeForward != null)
                    methodDefinition.AddInstruction(OpCodes.Callvirt, fakeForward);
            }

            var targetMethod = RewriteMethodReference(target, type, typeDefinition, method, methodDefinition, originalMethod);

            methodDefinition.AddInstruction(method.IsStatic ? OpCodes.Call : OpCodes.Callvirt, target.MainModule.Import(targetMethod));

            var returnType = methodDefinition.ReturnType;
            if (returnType.IsGenericInstance)
                returnType = returnType.Resolve();

            var targetType = GetModuleTypeDefinition(target, returnType);

            if (targetType != null)
            {
                if (NeedsFakeImplementation(targetType))
                {
                    targetType = implementationTypes[targetType.FullName];
                    if (targetType == null)
                        throw new InvalidOperationException($"{returnType.FullName} is in target and requires a fake implementation, but none was found");
                }

                var ctor = targetType.Methods.Single(m => m.IsConstructor && m.Parameters.SingleOrDefault(p => p.Name == "forward") != null);
                methodDefinition.AddInstruction(OpCodes.Newobj, ctor);
            }

            methodDefinition.AddInstruction(OpCodes.Ret);
        }

        MethodReference RewriteMethodReference(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, MethodDefinition methodReference, bool importFinalReference = true)
        {
            return rewriter.Reinstantiate(target, type, typeDefinition, method, methodDefinition, methodReference);
            //if (methodReference.IsGenericInstance)
            //{
            //    var originalMethod = target.MainModule.Import(methodReference.Resolve());
            //    var targetMethod = new GenericInstanceMethod(originalMethod);
            //    foreach (var arg in methodReference.GenericParameters)
            //        targetMethod.GenericArguments.Add(RewriteTypeReference(target, type, typeDefinition, method, methodDefinition, arg));
            //    return targetMethod;
            //}

            //if (methodReference.HasGenericParameters)
            //{
            //    var originalMethod = target.MainModule.Import(methodReference);
            //    var targetMethod = new GenericInstanceMethod(originalMethod);
            //    foreach (var arg in methodReference.GenericParameters)
            //        targetMethod.GenericArguments.Add(RewriteTypeReference(target, type, typeDefinition, method, methodDefinition, arg));

            //    return targetMethod;
            //}

            //return target.MainModule.Import(methodReference);
        }

        TypeDefinition CreateFakeImplementation(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes)
        {
            var fakeImplementation = new TypeDefinition(typeDefinition.Namespace, $"_FakeImpl_{typeDefinition.Name}", TypeAttributes.Class | TypeAttributes.Public, target.MainModule.TypeSystem.Object);
            if (typeDefinition.HasGenericParameters)
            {
                foreach (var gp in typeDefinition.GenericParameters)
                    fakeImplementation.GenericParameters.Add(new GenericParameter(gp.Name, fakeImplementation));
            }

            fakeImplementation.Interfaces.Add(typeDefinition.HasGenericParameters ? (TypeReference)typeDefinition.MakeGenericInstanceType(fakeImplementation.GenericParameters.ToArray()) : typeDefinition);

            CreateFakeFieldAndForwardConstructorDefinitions(target, type, fakeImplementation);
            CreateFakeFieldAndForwardConstructor(target, type, fakeImplementation);

            target.MainModule.Types.Add(fakeImplementation);

            return fakeImplementation;
        }

        void AttachInterfaces(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            foreach (var iface in type.Interfaces)
            {
                typeDefinition.Interfaces.Add(RewriteTypeReference(target, type, typeDefinition, null, null, iface));
            }
        }

        static bool ShouldSkip(TypeDefinition type)
        {
            if (type.Name == "<Module>")
                return true;

            if (!type.IsPublic)
                return true;

            if (type.IsEnum)
                return true;

            return false;
        }

        const string k_NamespacePrefix = "Fake";
        const string k_FakeForward = "__fake_forward";
        const string k_FakeForwardPropertyNamePrefix = "_FakeForwardProp_";
        const string k_FakeForwardPropertyGetterNamePrefix = "get__FakeForwardProp_";

        static string RewriteNamespace(string @namespace)
        {
            if (string.IsNullOrEmpty(@namespace))
                return k_NamespacePrefix;
            return $"{k_NamespacePrefix}.{@namespace}";
        }

        void AttachGenericTypeConstraints(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            if (!type.HasGenericParameters)
                return;

            for (var i = 0; i < type.GenericParameters.Count; ++i)
            {
                var originalGenericParameter = type.GenericParameters[i];
                if (!originalGenericParameter.HasConstraints)
                    continue;

                var definitionGenericParameter = typeDefinition.GenericParameters[i];
                foreach (var constraint in originalGenericParameter.Constraints)
                    definitionGenericParameter.Constraints.Add(RewriteTypeReference(target, type, typeDefinition, null, null, constraint));
            }
        }

        TypeReference MakeGenericInstanceTypeIfNecessary(AssemblyDefinition target, TypeReference typeReference, TypeDefinition type, TypeDefinition typeDefinition)
        {
            var resolvedType = typeReference;
            if (resolvedType.IsGenericInstance)
                resolvedType = resolvedType.Resolve();

            var importedTypeReference = target.MainModule.Import(resolvedType);

            if (typeReference.IsGenericInstance)
            {
                var genericInstance = (GenericInstanceType)typeReference;
                var gp = genericInstance.GenericArguments.Select(ga =>
                {
                    if (ga is GenericParameter)
                    {
                        var idx = type.GenericParameters.IndexOf((GenericParameter)ga);
                        if (idx == -1)
                            throw new InvalidOperationException($"Generic argument is unknown generic parameter");
                        return typeDefinition.GenericParameters[idx];
                    }

                    return MakeGenericInstanceTypeIfNecessary(target, ga, type, typeDefinition);
                }).ToArray();

                importedTypeReference = importedTypeReference.MakeGenericInstanceType(gp);
            }
            else if (typeReference.HasGenericParameters)
            {
                importedTypeReference = importedTypeReference.MakeGenericInstanceType(typeDefinition.GenericParameters.Cast<TypeReference>().ToArray());
            }

            return importedTypeReference;
        }

        TypeDefinition CreateTargetTypes(AssemblyDefinition target, TypeDefinition type, Dictionary<string, TypeDefinition> implementationTypes)
        {
            var typeDefinition = new TypeDefinition(RewriteNamespace(type.Namespace), type.Name, CreateTargetTypeAttributes(type));

            foreach (var gp in type.GenericParameters)
            {
                typeDefinition.GenericParameters.Add(new GenericParameter(gp.Name, typeDefinition));
            }

            typeDefinition.BaseType = RewriteTypeReference(target, type, typeDefinition, null, null, type.BaseType);

            typeDefinition.Methods.Clear();

            //if (NeedsTypeFieldAndForwardConstructor(type))
            //{
            //    CreateFakeFieldAndForwardConstructor(target, type, typeDefinition);
            //}

            target.MainModule.Types.Add(typeDefinition);

            if (NeedsFakeImplementation(type))
            {
                var propertyDefinition = new PropertyDefinition(GetFakeForwardPropertyName(type), PropertyAttributes.None, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
                propertyDefinition.HasThis = true;
                propertyDefinition.GetMethod = new MethodDefinition(GetFakeForwardPropertyGetterNamePrefix(type), MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Public, propertyDefinition.PropertyType);
                propertyDefinition.GetMethod.DeclaringType = typeDefinition;

                typeDefinition.Properties.Add(propertyDefinition);

                //implementationTypes[typeDefinition.FullName] = CreateFakeImplementation(target, type, typeDefinition);
            }

            return typeDefinition;
        }

        void CreateTypeFieldAndForwardConstructorDefinitionsIfNeeded(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes)
        {
            if (NeedsTypeFieldAndForwardConstructor(type))
            {
                CreateFakeFieldAndForwardConstructorDefinitions(target, type, typeDefinition);
            }
        }

        void CreateFakeFieldAndForwardConstructorDefinitions(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            var field = new FieldDefinition(k_FakeForward, FieldAttributes.Private, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
            typeDefinition.Fields.Add(field);

            var forwardConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.MainModule.TypeSystem.Void);
            forwardConstructor.DeclaringType = typeDefinition;
            forwardConstructor.Parameters.Add(new ParameterDefinition("forward", ParameterAttributes.None, field.FieldType));
            typeDefinition.Methods.Add(forwardConstructor);

            if (NeedsFakeForwardProperties(type))
            {
                foreach (var myType in GetTypesToCreateForwardPropertiesFor(target, type))
                {
                    if (!NeedsFakeForwardPropertyDefinition(myType.Resolve()))
                        continue;

                    var rewriteTypeReference = RewriteTypeReference(target, type, typeDefinition, null, null, myType, lookupFake: false);
                    if (rewriteTypeReference.HasGenericParameters && !rewriteTypeReference.IsGenericInstance)
                        rewriteTypeReference = rewriteTypeReference.MakeGenericInstanceType(typeDefinition.GenericParameters.ToArray());
                    var fakeForwardProp = new PropertyDefinition(GetFakeForwardPropertyImplementationName(myType), PropertyAttributes.None, rewriteTypeReference);
                    fakeForwardProp.HasThis = true;
                    typeDefinition.Properties.Add(fakeForwardProp);

                    var methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName;
                    if (myType == type.BaseType)
                        methodAttributes = methodAttributes & ~MethodAttributes.NewSlot;
                    var getFakeForwardProp = new MethodDefinition(GetFakeForwardPropertyGetterImplementationNamePrefix(myType), methodAttributes, fakeForwardProp.PropertyType);
                    getFakeForwardProp.DeclaringType = typeDefinition;

                    fakeForwardProp.GetMethod = getFakeForwardProp;
                }
            }
        }

        void CreateForwardConstructorBodyAndFakeImplementationIfNeeded(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes)
        {
            if (NeedsTypeFieldAndForwardConstructor(type))
            {
                CreateFakeFieldAndForwardConstructor(target, type, typeDefinition);
            }

            if (NeedsFakeImplementation(type))
            {
                implementationTypes[typeDefinition.FullName] = CreateFakeImplementation(target, type, typeDefinition, implementationTypes);
            }
        }

        static TypeAttributes CreateTargetTypeAttributes(TypeDefinition type)
        {
            if (type.Attributes.HasFlag(TypeAttributes.Interface))
                return type.Attributes;
            return type.Attributes & ~TypeAttributes.Abstract;
        }

        string GetFakeForwardPropertyGetterNamePrefix(TypeReference type)
        {
            return k_FakeForwardPropertyGetterNamePrefix + GetSafeNameFromTypeReferenceFullNameStrippingFake(type);
        }

        string GetFakeForwardPropertyName(TypeReference type)
        {
            return k_FakeForwardPropertyNamePrefix + GetSafeNameFromTypeReferenceFullNameStrippingFake(type);
        }

        static string GetSafeNameFromTypeReferenceFullNameStrippingFake(TypeReference type)
        {
            var fullName = type.FullName;

            if (type.IsGenericInstance)
                fullName = type.Resolve().FullName;

            if (type.Namespace == "Fake" || type.Namespace.StartsWith("Fake."))
                fullName = fullName.Substring(5);

            return fullName.Replace(".", "_dot_").Replace("+", "_plus_").Replace("`", "_tick_");
        }

        IEnumerable<TypeReference> GetTypesToCreateForwardPropertiesFor(AssemblyDefinition target, TypeDefinition type)
        {
            yield return type;
            foreach (var iface in type.Interfaces)
            {
                if (ShouldSkip(iface.Resolve()))
                    continue;

                yield return iface;
            }
            var baseType = GetModuleTypeDefinition(target, type.BaseType);
            if (baseType != null)
                yield return type.BaseType;
        }

        void CreateFakeFieldAndForwardConstructor(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            //var field = new FieldDefinition(k_FakeForward, FieldAttributes.Private, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
            //typeDefinition.Fields.Add(field);

            //var forwardConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.MainModule.TypeSystem.Void);
            //forwardConstructor.DeclaringType = typeDefinition;
            //forwardConstructor.Parameters.Add(new ParameterDefinition("forward", ParameterAttributes.None, field.FieldType));

            var field = typeDefinition.Fields.Single();
            var forwardConstructor = typeDefinition.Methods.Single();

            var baseConstructorMethod = target.MainModule.Import(target.MainModule.TypeSystem.Object.Resolve().GetConstructors().Single(c => c.IsPublic));
            if (typeDefinition.BaseType != null)
            {
                var moduleBaseType = GetModuleTypeDefinition(target, typeDefinition.BaseType);
                if (moduleBaseType != null)
                    baseConstructorMethod = moduleBaseType.GetConstructors().Single(c => c.IsPublic && c.Parameters.Count == 1);
                //var external = target.MainModule.Types.SingleOrDefault(t => t.FullName == (typeDefinition.BaseType.IsGenericInstance ? typeDefinition.BaseType.Resolve() : typeDefinition).FullName);
                //var candidate = typeDefinition.BaseType.Resolve().GetConstructors().SingleOrDefault(c => c.IsPublic && c.Parameters.Count == 1); // TODO: needs to be relaxed
                //if (candidate == null)
                //    candidate = typeDefinition.BaseType.Resolve().GetConstructors().SingleOrD__efault(c => c.IsPublic && c.Parameters.Count == 0);
                //if (candidate != null)
                //    baseConstructorMethod = external != null ? candidate : target.MainModule.Import(candidate);
            }

            forwardConstructor.AddInstruction(OpCodes.Ldarg_0);

            if (baseConstructorMethod.Parameters.Count == 1)
                forwardConstructor.AddInstruction(OpCodes.Ldarg_1);

            forwardConstructor.AddInstruction(OpCodes.Call, baseConstructorMethod)
                .AddInstruction(OpCodes.Ldarg_0)
                .AddInstruction(OpCodes.Ldarg_1)
                .AddInstruction(OpCodes.Stfld, field)
                .AddInstruction(OpCodes.Ret);

            //typeDefinition.Methods.Add(forwardConstructor);

            if (NeedsFakeForwardProperties(type))
            {
                var i = -1;
                foreach (var myType in GetTypesToCreateForwardPropertiesFor(target, type))
                {
                    if (!NeedsFakeForwardPropertyDefinition(myType.Resolve()))
                        continue;

                    ++i;

                    var fakeForwardProp = typeDefinition.Properties[i];

                    var getFakeForwardProp = fakeForwardProp.GetMethod;
                    getFakeForwardProp.AddInstruction(OpCodes.Ldarg_0)
                        .AddInstruction(OpCodes.Ldfld, field)
                        .AddInstruction(OpCodes.Ret);

                    if (typeDefinition.Name != myType.Name) // HACK: verify same types with and without Fake namespace correctly
                    {
                        var baseMethod = GetModuleTypeDefinition(target, myType.Resolve()).Properties.Single(p => p.Name == GetFakeForwardPropertyImplementationName(myType.Resolve()) || p.Name == GetFakeForwardPropertyName(myType.Resolve()));
                        var reference = RewriteMethodReference(target, myType.Resolve(), typeDefinition, baseMethod.GetMethod, getFakeForwardProp, baseMethod.GetMethod, false);
                        if (myType.IsGenericInstance)
                            reference = reference.MakeHostInstanceGeneric(((GenericInstanceType)myType).GenericArguments.ToArray());
                        else if (myType.HasGenericParameters)
                            reference = reference.MakeHostInstanceGeneric(myType.GenericParameters.ToArray());
//                        var reference = new MethodReference(.Name, baseMethod);
                        //getFakeForwardProp.Overrides.Add(myType.IsGenericInstance ? new MethodReference(baseMethod.GetMethod.Name, RewriteTypeReference(target, myType.Resolve(), typeDefinition, null, null, myType), myType) : baseMethod.GetMethod);
                        getFakeForwardProp.Overrides.Add(reference);
                    }

                    fakeForwardProp.GetMethod = getFakeForwardProp;
                }
            }
        }

        string GetFakeForwardPropertyImplementationName(TypeReference type)
        {
            var idx = type.FullName.LastIndexOf("`");
            if (idx == -1)
                return $"Fake.{type.FullName}.{GetFakeForwardPropertyName(type)}";
            var baseName = type.FullName;
            if (Regex.Match(type.FullName, @"`\d+<").Success)
            {
                baseName = Regex.Replace(baseName, @"`\d+<", "<");
            }
            else
            {
                // TODO: consider nested classes A.B<>.C<>
                baseName = $"{type.FullName.Substring(0, idx)}<{string.Join(",", type.GenericParameters.Select(gp => gp.Name))}>";
            }
            return $"Fake.{baseName}.{GetFakeForwardPropertyName(type)}";
        }

        string GetFakeForwardPropertyGetterImplementationNamePrefix(TypeReference type)
        {
            return $"Fake.{Regex.Replace(type.FullName, @"`\d<", "<")}.{GetFakeForwardPropertyGetterNamePrefix(type)}";
        }

        bool NeedsFakeForwardPropertyDefinition(TypeDefinition type)
        {
            return type.IsInterface || type.IsClass && type.IsAbstract;
        }

        bool NeedsFakeForwardProperties(TypeDefinition type)
        {
            if (ShouldSkip(type))
                return false;

            return type.IsInterface || type.IsClass && type.IsAbstract || type.Interfaces.Any(i => NeedsFakeForwardProperties(i.Resolve()));
        }

        static bool NeedsTypeFieldAndForwardConstructor(TypeDefinition type)
        {
            return !type.IsInterface;
        }

        static bool NeedsFakeImplementation(TypeDefinition type)
        {
            return type.IsInterface;
        }

        TypeReference RewriteTypeReference(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, TypeReference typeReference, bool lookupFake = true)
        {
            return rewriter.Rewrite(target, type, typeDefinition, typeReference, method, methodDefinition, lookupFake);

            //if (typeReference == null)
            //    return null;

            //var resolvedType = typeReference;

            //if (resolvedType.IsGenericInstance)
            //    resolvedType = typeReference.Resolve();

            //var importedType = resolvedType;
            //if (!resolvedType.IsGenericParameter)
            //    importedType = target.MainModule.Import(resolvedType);
            //else
            //{
            //    var idx = type.GenericParameters.IndexOf((GenericParameter)importedType);
            //    if (idx != -1)
            //        importedType = typeDefinition.GenericParameters[idx];
            //    else if (method != null)
            //    {
            //        idx = method.GenericParameters.IndexOf((GenericParameter)importedType);
            //        if (idx != -1)
            //            importedType = methodDefinition.GenericParameters[idx];
            //        else
            //            throw new InvalidOperationException("Unknown generic parameter reference");
            //    }
            //    else
            //        throw new InvalidOperationException("Unknown generic parameter reference");
            //}

            //var resultType = importedType;
            //if (!resolvedType.IsGenericParameter)
            //{
            //    var rewrittenType = target.MainModule.Types.FirstOrDefault(t => t.FullName == (string.IsNullOrEmpty(typeReference.Namespace) ? "Fake." + typeReference.Name : "Fake." + typeReference.FullName));
            //    if (rewrittenType != null && lookupFake)
            //        resultType = rewrittenType;
            //}

            //if (typeReference.IsGenericInstance)
            //{
            //    var genericInstanceType = (GenericInstanceType)typeReference;
            //    var genericParameters = genericInstanceType.GenericArguments.Select(ga => RewriteTypeReference(target, type, typeDefinition, method, methodDefinition, ga)).ToArray();
            //    resultType = resultType.MakeGenericInstanceType(genericParameters);
            //}

            //return resultType;
        }
    }

    static class CecilExtensions
    {
        public static TypeReference MakeGenericType(this TypeReference type, params TypeReference[] arguments)
        {
            if (type.GenericParameters.Count != arguments.Length)
                throw new ArgumentException();

            var instance = new GenericInstanceType(type);
            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return instance;
        }

        public static MethodReference MakeHostInstanceGeneric(this MethodReference method, params TypeReference[] arguments) 
        {
            var reference = new MethodReference(method.Name, method.ReturnType, method.DeclaringType.MakeGenericType(arguments))
            {
                HasThis = method.HasThis,
                ExplicitThis = method.ExplicitThis,
                CallingConvention = method.CallingConvention
            };

            foreach (var parameter in method.Parameters)
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

            foreach (var gp in method.GenericParameters)
                reference.GenericParameters.Add(new GenericParameter(gp.Name, reference));

            return reference;
        }

        public static MethodDefinition AddInstruction(this MethodDefinition target, OpCode opCode)
        {
            target.Body.Instructions.Add(Instruction.Create(opCode));
            return target;
        }

        public static MethodDefinition AddInstruction(this MethodDefinition target, OpCode opCode, MethodReference methodReference)
        {
            target.Body.Instructions.Add(Instruction.Create(opCode, methodReference));
            return target;
        }

        public static MethodDefinition AddInstruction(this MethodDefinition target, OpCode opCode, ParameterDefinition parameterDefinition)
        {
            target.Body.Instructions.Add(Instruction.Create(opCode, parameterDefinition));
            return target;
        }

        public static MethodDefinition AddInstruction(this MethodDefinition target, OpCode opCode, FieldReference fieldReference)
        {
            target.Body.Instructions.Add(Instruction.Create(opCode, fieldReference));
            return target;
        }
    }
}
