using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Unity.Cecil.Visitor;

namespace NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper
{
    class Copier2 : ICopier
    {
        readonly ProcessTypeResolver m_ProcessTypeResolver;
        const string k_PrefixName = "Fake.";
        const string k_PrefixNameRaw = "Fake";
        const string k_FakeForward = "__fake_forward";
        static MethodDefinition s_FakeForwardConstructor;
        static MethodDefinition s_FakeCloneMethod;
        static string[] s_SecurityAttributes = { "System.Security.SecurityCriticalAttribute", "System.Security.SuppressUnmanagedCodeSecurityAttribute" };
        Dictionary<string, MethodDefinition> m_ForwardConstructors = new Dictionary<string, MethodDefinition>();

        public Copier2(ProcessTypeResolver processTypeResolver)
        {
            m_ProcessTypeResolver = processTypeResolver;
        }

        public virtual void Copy(AssemblyDefinition source, ref AssemblyDefinition target, AssemblyDefinition nsubstitute, string[] typesToCopy)
        {
            var typeDefinitions = m_ProcessTypeResolver.Resolve(typesToCopy).ToList();

            // Copy in two passes to be able to move references within individual classes to other faked types.
            // Note that this functionality is not currently in use, but could prove useful for a later stage,
            // and rather than extracting the logic now, it is better to design it for this purpose up front.

            var reservedNames = new List<string> { "<PrivateImplementationDetails>", "<Module>", "System.Void", "System.Object", "System.Boolean", "System.String", "System.Int32" };

            foreach (var type in typeDefinitions.Where(t => reservedNames.IndexOf(t.FullName) == -1))
            {
                CreateTypeShim(target, type);
            }

            foreach (var type in typeDefinitions.Where(t => t.IsInterface).OrderBy(t => InheritanceHierarchySize(t.BaseType)))
                CopyType(target, type);

            foreach (var type in typeDefinitions.Where(t => t.IsEnum).OrderBy(t => InheritanceHierarchySize(t.BaseType)))
                CopyType(target, type);

            foreach (var type in typeDefinitions.Where(t => !t.IsInterface && !t.IsEnum && reservedNames.IndexOf(t.FullName) == -1).OrderBy(t => InheritanceHierarchySize(t.BaseType)))
                CopyType(target, type);

            foreach (var type in typeDefinitions.Where(t => reservedNames.IndexOf(t.FullName) == -1))
            {
                var typeDefinition = target.MainModule.Types.SingleOrDefault(t => t.FullName == k_PrefixName + type.FullName);
                if (typeDefinition == null)
                    continue;
                CopyTypeMembers(target, type, typeDefinition);
            }

            //var visitor = new MockInjectorVisitor(nsubstitute, target.MainModule);
            //target.Accept(visitor);
        }

        static int InheritanceHierarchySize(TypeReference typeDefinition)
        {
            var c = 0;
            while (typeDefinition != null && typeDefinition.FullName != "System.Object")
            {
                ++c;
                typeDefinition = typeDefinition.Resolve().BaseType;
            }
            return c;
        }

        void CopyTypeMembers(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            CopyCustomAttributes(target, type, typeDefinition);

            if (type.IsEnum)
            {
                CopyEnumValues(target, type, typeDefinition);
                return;
            }

            CopyMethods(target, type, typeDefinition);
            CopyProperties(target, type, typeDefinition);
            CreateEmptyCtorIfNotExists(target, typeDefinition);
        }

        void CopyEnumValues(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            foreach (var field in type.Fields.Where(f => f.Name != "value__"))
            {
                var fieldDefinition = new FieldDefinition(field.Name, field.Attributes, RecursivelyInstantiateAndImportTypeRefence(target, field.FieldType, type, typeDefinition));
                if (field.HasConstant)
                {
                    fieldDefinition.Constant = field.Constant; // hope this works
                }
                typeDefinition.Fields.Add(fieldDefinition);
            }
        }

        void CreateEmptyCtorIfNotExists(AssemblyDefinition target, TypeDefinition typeDefinition)
        {
            if (typeDefinition.IsInterface)
                return;

            if (typeDefinition.Methods.Any(m => m.IsConstructor && m.Parameters.Count == 0))
                return;

            var methodAttributes = MethodAttributes.Public| MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            if (typeDefinition.IsAbstract)
            {
                methodAttributes &= ~MethodAttributes.Public;
                methodAttributes |= MethodAttributes.Family;
            }
            var methodDefinition = new MethodDefinition(".ctor", methodAttributes, target.MainModule.TypeSystem.Void);
            typeDefinition.Methods.Add(methodDefinition);

            AddBaseTypeCtorCall(target, typeDefinition, methodDefinition);
            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }

        void CopyProperties(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            foreach (var property in type.Properties)
            {
                if (!property.IsPublic())
                    continue;

                var propertyDefinition = new PropertyDefinition(property.Name, property.Attributes, ResolveType(target, type, typeDefinition, null, null, property.PropertyType));
                if (property.GetMethod != null && property.GetMethod.IsPublic)
                    propertyDefinition.GetMethod = ResolveMethod(target, type, typeDefinition, property.GetMethod);
                if (property.SetMethod != null && property.SetMethod.IsPublic)
                    propertyDefinition.SetMethod = ResolveMethod(target, type, typeDefinition, property.SetMethod);

                typeDefinition.Properties.Add(propertyDefinition);
            }
        }

        MethodDefinition ResolveMethod(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition originalMethod)
        {
            var candidates = typeDefinition.Methods.Where(m => m.Name == originalMethod.Name).ToList();
            if (candidates.Count == 1)
                return candidates[0];

            foreach (var candidate in candidates)
            {
                if (candidate.Parameters.Count != originalMethod.Parameters.Count)
                    continue;

                var parameters = originalMethod.Parameters.Select(p => ResolveType(target, type, typeDefinition, originalMethod, candidate, p.ParameterType)).ToArray();
                if (candidate.Parameters.Zip(parameters, Tuple.Create).All(pp => pp.Item1.ParameterType.FullName == pp.Item2.FullName))
                    return candidate;
            }

            throw new NotImplementedException();
        }

        void CreateFakeFieldForward(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            var field = new FieldDefinition(k_FakeForward, FieldAttributes.Assembly, target.MainModule.Import(type));
            typeDefinition.Fields.Add(field);
        }

        static TypeReference RecursivelyInstantiateAndImportTypeRefence(AssemblyDefinition target, TypeReference instanceType, TypeDefinition originalType, TypeDefinition typeDefinition)
        {
            if (instanceType.IsGenericInstance)
            {
                var genericInstanceType = (GenericInstanceType)instanceType;
                var genericType = target.MainModule.Import(instanceType.Resolve());
                var fakedGenericType = target.MainModule.Types.SingleOrDefault(t => t.FullName == k_PrefixName + genericType.FullName);
                genericType = fakedGenericType ?? genericType;
                var result = genericType.MakeGenericInstanceType(genericInstanceType.GenericArguments.Select(a => RecursivelyInstantiateAndImportTypeRefence(target, a, originalType, typeDefinition)).ToArray());
                return target.MainModule.Import(result);
            }

            if (instanceType.IsArray)
            {
                var element = RecursivelyInstantiateAndImportTypeRefence(target, instanceType.GetElementType(), originalType, typeDefinition);
                if (element.IsGenericParameter)
                    return element.MakeArrayType();
                return target.MainModule.Import(element.MakeArrayType());
            }

            if (!(instanceType is GenericParameter))
            {
                var fakedType = target.MainModule.Types.SingleOrDefault(t => k_PrefixName + instanceType.FullName == t.FullName);
                if (fakedType != null)
                    return target.MainModule.Import(fakedType);

                return target.MainModule.Import(instanceType);
            }

            return typeDefinition.GenericParameters[originalType.GenericParameters.IndexOf((GenericParameter)instanceType)];
        }

        void CreateTypeShim(AssemblyDefinition target, TypeDefinition type)
        {
            if (!type.IsPublic)
                return;

            if (type.BaseType?.FullName == "System.MulticastDelegate" || type.BaseType?.FullName == "System.Delegate")
                return; // skip delegates

            if (type.IsEnum)
                return; // skip enums

            var typeDefinition = new TypeDefinition(type.Namespace == "" ? k_PrefixNameRaw : k_PrefixName + type.Namespace, type.Name, type.Attributes & ~TypeAttributes.HasSecurity);

            if (type.HasGenericParameters)
            {
                foreach (var gp in type.GenericParameters)
                    typeDefinition.GenericParameters.Add(new GenericParameter(gp.Name, typeDefinition));
            }

            typeDefinition.BaseType = type.BaseType == null ? null : RecursivelyInstantiateAndImportTypeRefence(target, type.BaseType, type, typeDefinition);

            if (type.IsInterface || type.IsAbstract)
            {
                var fakeForwardTypeImpl = new TypeDefinition(typeDefinition.Namespace, $"FakeForward{typeDefinition.Name}", TypeAttributes.Public | TypeAttributes.Class);

                foreach (var gp in type.GenericParameters)
                    fakeForwardTypeImpl.GenericParameters.Add(new GenericParameter(gp.Name, fakeForwardTypeImpl));

                if (type.IsInterface)
                    fakeForwardTypeImpl.Interfaces.Add(typeDefinition.HasGenericParameters ? (TypeReference)typeDefinition.MakeGenericInstanceType(fakeForwardTypeImpl.GenericParameters.ToArray()) : typeDefinition);
                else if (type.IsAbstract)
                    fakeForwardTypeImpl.BaseType = type.BaseType == null ? null : RecursivelyInstantiateAndImportTypeRefence(target, type, type, fakeForwardTypeImpl);
                CreateFakeFieldForward(target, type, fakeForwardTypeImpl);

                var forwardConstructor = CreateFakeForwardConstructor(target, type, fakeForwardTypeImpl);
                m_ForwardConstructors[typeDefinition.FullName] = forwardConstructor;
                target.MainModule.Types.Add(fakeForwardTypeImpl);
            }
            else
            {
                CreateFakeFieldForward(target, type, typeDefinition);
                var forwardConstructor = CreateFakeForwardConstructor(target, type, typeDefinition);
                if (type.IsAbstract)
                {
                    forwardConstructor.Attributes &= ~MethodAttributes.Public;
                    forwardConstructor.Attributes |= MethodAttributes.FamORAssem;
                }
                m_ForwardConstructors[typeDefinition.FullName] = forwardConstructor;
            }

            target.MainModule.Types.Add(typeDefinition);
        }


        public TypeDefinition CopyType(AssemblyDefinition target, TypeDefinition type)
        {
            if (!type.IsPublic)
                return null;

            //var typeDefinition = new TypeDefinition(type.Namespace == "" ? k_PrefixNameRaw : k_PrefixName + type.Namespace, type.Name, type.Attributes & ~TypeAttributes.HasSecurity);
            var typeDefinition = target.MainModule.Types.SingleOrDefault(t => t.FullName == $"{(type.Namespace == "" ? k_PrefixNameRaw : k_PrefixName + type.Namespace)}.{type.Name}");
            if (typeDefinition == null)
                return null;

            if (type.BaseType != null)
            {
                typeDefinition.BaseType = RecursivelyInstantiateAndImportTypeRefence(target, type.BaseType, type, typeDefinition);
            }
            else if (type.IsClass)
            {
                typeDefinition.BaseType = target.MainModule.TypeSystem.Object;
            }

            //target.MainModule.Types.Add(typeDefinition);

            return typeDefinition;
        }

        TypeReference Import(AssemblyDefinition target, TypeReference type)
        {
            if (type.IsGenericParameter)
                return type;

            return target.MainModule.Import(type);
        }

        TypeReference ResolveType(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, TypeReference resolveType)
        {
            var typeDef = k_PrefixName + resolveType.FullName == typeDefinition.FullName;
            if (typeDef)
                return typeDefinition;

            if (resolveType.IsGenericParameter)
            {
                var genericParameter = (GenericParameter)resolveType;
                if (genericParameter.Owner == method)
                    return methodDefinition.GenericParameters[genericParameter.Position];

                if (genericParameter.Owner == type)
                    return typeDefinition.GenericParameters[genericParameter.Position];

                if (genericParameter.Owner == methodDefinition)
                    return genericParameter;

                if (genericParameter.Owner == typeDefinition)
                    return genericParameter;

                throw new InvalidOperationException();
            }

            if (resolveType.IsArray)
            {
                var elementType = Import(target, ResolveType(target, type, typeDefinition, method, methodDefinition, resolveType.GetElementType()));
                return elementType.MakeArrayType(((ArrayType)resolveType).Rank);
            }

            if (resolveType.IsByReference)
            {
                var elementType = Import(target, ResolveType(target, type, typeDefinition, method, methodDefinition, resolveType.GetElementType()));
                return elementType.MakeByReferenceType();
            }

            if (resolveType.IsGenericInstance)
            {
                var genericInstanceType = (GenericInstanceType)resolveType;
                var genericType = target.MainModule.Import(genericInstanceType.Resolve());
                var fakedGenericType = target.MainModule.Types.SingleOrDefault(t => t.FullName == k_PrefixName + genericType.FullName);
                genericType = fakedGenericType ?? genericType;
                var result = genericType.MakeGenericInstanceType(genericInstanceType.GenericArguments.Select(a => ResolveType(target, type, typeDefinition, method, methodDefinition, a)).ToArray());
                return target.MainModule.Import(result);
            }

            var fakedType = target.MainModule.Types.SingleOrDefault(t => k_PrefixName + resolveType.FullName == t.FullName);
            if (fakedType != null)
                return target.MainModule.Import(fakedType);

            return target.MainModule.Import(resolveType);
        }

        void CopyMethods(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            if (!type.IsInterface)
            {
                if (!type.IsAbstract)
                {
                    s_FakeForwardConstructor = m_ForwardConstructors[typeDefinition.FullName];
                    s_FakeCloneMethod = null;
                }
                else
                {
                    s_FakeForwardConstructor = null;
                    s_FakeCloneMethod = CreateFakeCloneMethod(target, type, typeDefinition);
                }
            }

            foreach (var method in type.Methods)
            {
                if (!method.IsPublic)
                    continue;

                if (method.Name == ".ctor")
                {
                    var methodDefinition = new MethodDefinition(method.Name, method.Attributes & ~MethodAttributes.HasSecurity, typeDefinition);

                    if (method.HasGenericParameters)
                    {
                        foreach (var gp in method.GenericParameters)
                            methodDefinition.GenericParameters.Add(new GenericParameter(gp.Name, methodDefinition));
                    }

                    methodDefinition.ReturnType = ResolveType(target, type, typeDefinition, method, methodDefinition, method.ReturnType);

                    foreach (var parameter in method.Parameters)
                    {
                        //var parameterDefinition = new ParameterDefinition(parameter.Name, parameter.Attributes,
                        //        ResolveType(target, typeDefinition, parameter.ParameterType));
                        CopyParameter(target, method, typeDefinition, parameter, methodDefinition);
                    }

                    foreach (var customAttribute in method.CustomAttributes)
                    {
                        var attribute = CopyCustomAttribute(target, customAttribute);
                        if (attribute != null)
                            methodDefinition.CustomAttributes.Add(attribute);
                    }

                    FillConstructor(target, type, method, typeDefinition, methodDefinition);

                    typeDefinition.Methods.Add(methodDefinition);
                }
                else
                {
                    CreateMainImplementationForwardingMethod(target, method, typeDefinition);
                }
            }
        }

        MethodDefinition CreateMainImplementationForwardingMethod(AssemblyDefinition target, MethodDefinition method, TypeDefinition typeDefinition)
        {
            var implMethod = new MethodDefinition(method.Name /* + "__Impl"*/, method.Attributes & ~MethodAttributes.HasSecurity & ~MethodAttributes.PInvokeImpl, method.ReturnType);
            implMethod.DeclaringType = typeDefinition;

            foreach (var gp in method.GenericParameters)
                implMethod.GenericParameters.Add(new GenericParameter(gp.Name, implMethod));

            implMethod.ReturnType = ResolveType(target, method.DeclaringType, typeDefinition, method, implMethod, method.ReturnType);

            typeDefinition.Methods.Add(implMethod);

            #region Check and call local delegate field

            foreach (var param in method.Parameters)
            {
                CopyParameter(target, method, typeDefinition, param, implMethod);
            }

            foreach (var customAttribute in method.CustomAttributes)
            {
                var attribute = CopyCustomAttribute(target, customAttribute);
                if (attribute != null)
                    implMethod.CustomAttributes.Add(attribute);
            }

            if (typeDefinition.IsInterface)
                return implMethod;

            implMethod.Body = new MethodBody(implMethod);

            implMethod.Body.Variables.Add(new VariableDefinition(target.MainModule.Import(method.ReturnType)));
            implMethod.Body.InitLocals = true;

            if (implMethod.ReturnType.FullName == typeDefinition.FullName && s_FakeForwardConstructor == null)
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));

            if (!method.IsStatic)
            {
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); // this
                if (typeDefinition.IsValueType)
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldflda,
                            typeDefinition.Fields.Single(f => f.Name == k_FakeForward))); // this.__fake_forward
                else
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld,
                            FakeForwardField(typeDefinition)));
            }

            foreach (var param in implMethod.Parameters)
            {
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, param));
                if (param.ParameterType.FullName == typeDefinition.FullName)
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, FakeForwardField(typeDefinition)));
            }
            if (method.IsVirtual && !typeDefinition.IsValueType)
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, target.MainModule.Import(method)));
            else
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target.MainModule.Import(method))); // this.__fake__forward.<Method>(arguments)

            if (method.ReturnType != method.Module.TypeSystem.Void)
            {
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc_0));
                implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc_0));

                if (implMethod.ReturnType.IsArray)
                {
                    var branchNotNull = Instruction.Create(OpCodes.Ldloc_0);
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, branchNotNull));
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    implMethod.Body.Instructions.Add(branchNotNull);
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldlen));
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I4));
                    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, implMethod.ReturnType));
                }
                else
                {
                    if (implMethod.ReturnType.FullName != method.ReturnType.FullName)
                    {
                        var lookup = implMethod.ReturnType.FullName;
                        if (implMethod.ReturnType.IsGenericInstance)
                        {
                            lookup = implMethod.ReturnType.Resolve().FullName;
                        }
                        var ctor = m_ForwardConstructors[lookup];
                        if (implMethod.ReturnType.IsGenericInstance)
                        {
                            var instance = (GenericInstanceType)implMethod.ReturnType;
                            
                        }

                        implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, ctor));
                    }
                }
            }

            //var fakeReturnType = target.MainModule.Types.FirstOrDefault(t => t.FullName == implMethod.ReturnType.FullName);
            //if (fakeReturnType != null)
            //{
            //    var forwardConstructor = fakeReturnType.GetConstructors().SingleOrDefault(c => c.Parameters.Count == 1 && c.Parameters[0].ParameterType.FullName == fakeReturnType.FullName.Substring(5));

            //    implMethod.Body.Instructions.Add(forwardConstructor != null
            //        ? Instruction.Create(OpCodes.Newobj, forwardConstructor)
            //        : Instruction.Create(OpCodes.Callvirt, s_FakeCloneMethod));
            //    implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            //    return implMethod;
            //}

            implMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            #endregion

            return implMethod;
        }

        void CopyParameter(AssemblyDefinition target, MethodDefinition method, TypeDefinition typeDefinition, ParameterDefinition parameter, MethodDefinition methodDefinition)
        {
            var parameterDefinition = new ParameterDefinition(parameter.Name, parameter.Attributes, ResolveType(target, method.DeclaringType, typeDefinition, method, methodDefinition, parameter.ParameterType));
            if (parameter.HasFieldMarshal)
                parameterDefinition.HasFieldMarshal = true;
            if (parameter.HasMarshalInfo)
                parameterDefinition.MarshalInfo = parameter.MarshalInfo;
            if (parameter.HasConstant)
            {
                parameterDefinition.Constant = parameter.Constant;
                parameterDefinition.HasConstant = true;
            }
            methodDefinition.Parameters.Add(parameterDefinition);
        }

        // TODO: Finish the fake clone method, which is needed for inheritance hierarchies
        // When dealing with abstract or super types that create a modified version of a type, it is necessary for the wrapper to be able to
        // construct a new instance of the fake wrapped around the new type, but since we don't have access to enough information at the call-site,
        // we introduce a new clone method explicitly for this purpose.
        // For a typical use case, see TextWriter.Synchronized.
        MethodDefinition CreateFakeCloneMethod(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            var methodDefinition = new MethodDefinition("__FakeClone", MethodAttributes.Family | MethodAttributes.Virtual, typeDefinition);
            methodDefinition.Parameters.Add(new ParameterDefinition("fake", ParameterAttributes.None, target.MainModule.Import(type)));

            //if (typeDefinition.IsAbstract)
            methodDefinition.Attributes |= MethodAttributes.Abstract;

            typeDefinition.Methods.Add(methodDefinition);

            return methodDefinition;
        }

        FieldDefinition CreateFakeField(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method)
        {
            if (method.Name == ".ctor")
                return null;

            if (method.Parameters.Any(p => p.ParameterType.IsByReference)) // TODO: Figure out byref
                return null;

            TypeReference baseType;
            if (method.ReturnType.FullName == "System.Void")
            {
                if (method.Parameters.Count == 0)
                    baseType = type.Module.GetType("System.Action");
                else
                    baseType = type.Module.GetType($"System.Action`{method.Parameters.Count}");
            }
            else
                baseType = type.Module.GetType($"System.Func`{method.Parameters.Count + 1}");
            baseType = target.MainModule.Import(baseType);

            if (method.ReturnType.FullName == "System.Void")
            {
                if (method.Parameters.Count > 0)
                    baseType = baseType.MakeGenericInstanceType(
                            method.Parameters.Select<ParameterDefinition, TypeReference>(p => ResolveType(target, typeDefinition, p.ParameterType)).ToArray());
            }
            else
                baseType = baseType.MakeGenericInstanceType(
                        method.Parameters.Select<ParameterDefinition, TypeReference>(p => ResolveType(target, typeDefinition, p.ParameterType))
                        .Concat(new[] {ResolveType(target, typeDefinition, method.ReturnType)})
                        .ToArray());

            var attributes = FieldAttributes.Public;
            if (method.IsStatic)
                attributes |= FieldAttributes.Static;

            var disambiguator = string.Join("_", method.Parameters.Select(p => p.ParameterType.Name).ToArray());
            var fieldName = method.Name + "Fake" + (disambiguator == "" ? "" : "_" + disambiguator);
            if (method.IsStatic)
                fieldName += "_static";
            var fieldDefinition = new FieldDefinition(fieldName, attributes, baseType);
            typeDefinition.Fields.Add(fieldDefinition);
            return fieldDefinition;
        }

        MethodDefinition CreateFakeForwardConstructor(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            var method = new MethodDefinition(".ctor", MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName, target.MainModule.Import(type.Module.TypeSystem.Void));
            var parameterDefinition = new ParameterDefinition("forward", ParameterAttributes.In, target.MainModule.Import(type));
            method.Parameters.Add(parameterDefinition);

            method.Body = new MethodBody(method);
            AddBaseTypeCtorCall(target, typeDefinition, method);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); // this
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, parameterDefinition)); // forward
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, FakeForwardField(typeDefinition))); // this.__fake_forward = forward
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            typeDefinition.Methods.Add(method);
            return method;
        }

        FieldDefinition FakeForwardField(TypeDefinition typeDefinition)
        {
            return typeDefinition.Fields.Single(f => f.Name == k_FakeForward);
        }

        void CreateMainFakeMethodContents(AssemblyDefinition target, TypeDefinition type, MethodDefinition method, TypeDefinition typeDefinition, MethodDefinition methodDefinition, FieldDefinition fakeField, MethodDefinition implMethod)
        {
            methodDefinition.Body = new MethodBody(methodDefinition);

            var nop = Instruction.Create(OpCodes.Nop);
            var ret = Instruction.Create(OpCodes.Ret);

            if (fakeField != null)
            {
                AddFakeFieldCallback(target, method, methodDefinition, fakeField, nop, ret);
            }

            methodDefinition.Body.Instructions.Add(nop);

            if (!methodDefinition.IsStatic)
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));

            foreach (var param in methodDefinition.Parameters)
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, param));
            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Call, implMethod));

            methodDefinition.Body.Instructions.Add(ret);
        }

        void AddFakeFieldCallback(AssemblyDefinition target, MethodDefinition method,
            MethodDefinition methodDefinition, FieldDefinition fakeField, Instruction nop, Instruction ret)
        {
            if (method.IsStatic)
            {
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, fakeField));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Cgt_Un));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse_S, nop));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, fakeField));
                foreach (var param in methodDefinition.Parameters)
                    methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, param));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt,
                        (MethodReference)ResolveGenericInvoke(target, fakeField)));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Br_S, ret));
            }
            else
            {
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fakeField));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Cgt_Un));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse_S, nop));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fakeField));
                foreach (var param in methodDefinition.Parameters)
                    methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, param));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt,
                        (MethodReference)ResolveGenericInvoke(target, fakeField)));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Br_S, ret));
            }
        }

        MethodReference ResolveGenericInvoke(AssemblyDefinition target, FieldDefinition fakeField)
        {
            if (!fakeField.FieldType.IsGenericInstance)
                return target.MainModule.Import(fakeField.FieldType.Resolve().Methods.Single(m => m.Name == "Invoke"));

            var genericType = (GenericInstanceType)fakeField.FieldType;
            var openType = genericType.Resolve();
            var openInvoke = target.MainModule.Import(openType.Methods.Single(m => m.IsPublic && m.Name == "Invoke"));

            var realInvoke = new MethodReference(openInvoke.Name, openInvoke.ReturnType,
                    openInvoke.DeclaringType.MakeGenericInstanceType(genericType.GenericArguments.ToArray()))
            {
                HasThis = openInvoke.HasThis,
                ExplicitThis = openInvoke.ExplicitThis,
                CallingConvention = openInvoke.CallingConvention
            };

            foreach (var parameter in openInvoke.Parameters)
                realInvoke.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            foreach (var genericParameter in openInvoke.GenericParameters)
                realInvoke.GenericParameters.Add(new GenericParameter(genericParameter.Name, realInvoke));

            return realInvoke;
        }

        void FillConstructor(AssemblyDefinition target, TypeDefinition type, MethodDefinition method, TypeDefinition typeDefinition, MethodDefinition methodDefinition)
        {
            methodDefinition.Body = new MethodBody(methodDefinition);

            AddBaseTypeCtorCall(target, typeDefinition, methodDefinition);

            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));

            foreach (var param in methodDefinition.Parameters)
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, param));

            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, target.MainModule.Import(method)));
            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, typeDefinition.Fields.Single(f => f.Name == k_FakeForward)));
            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }

        void AddBaseTypeCtorCall(AssemblyDefinition target, TypeDefinition typeDefinition,
            MethodDefinition methodDefinition)
        {
            if (typeDefinition.BaseType != null && !typeDefinition.IsValueType)
            {
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                var baseTypeCtor =
                    target.MainModule.Import(
                        typeDefinition.BaseType.Resolve()
                        .Methods.Single(m => m.IsConstructor && m.Parameters.Count == 0 && !m.IsStatic));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseTypeCtor));
            }
            else if (typeDefinition.BaseType == null)
            {
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target.MainModule.Import(target.MainModule.TypeSystem.Object.Resolve().GetConstructors().Single(c => c.IsPublic && c.Parameters.Count == 0))));
            }
        }

        TypeReference ResolveType(AssemblyDefinition target, TypeDefinition typeDefinition, TypeReference type)
        {
            //var typeDef = target.MainModule.Types.FirstOrDefault(t => t.FullName == prefixName + type.FullName);
            var typeDef = k_PrefixName + type.FullName == typeDefinition.FullName;
            if (typeDef)
                return typeDefinition;

            return target.MainModule.Import(type);
        }

        CustomAttribute CopyCustomAttribute(AssemblyDefinition target, CustomAttribute customAttribute)
        {
            if (s_SecurityAttributes.Contains(customAttribute.AttributeType.FullName))
                return null;

            var attributeDefinition = customAttribute.AttributeType.Resolve();

            if (!attributeDefinition.IsPublic)
                return null;

            var customAttributeConstructor = customAttribute.Constructor.Resolve();

            var targetAttributeType = target.MainModule.GetType(attributeDefinition.FullName);
            if (targetAttributeType != null)
            {
                customAttributeConstructor = targetAttributeType.Methods.Single(m => m.FullName == customAttributeConstructor.FullName);
            }

            var attribute = new CustomAttribute(target.MainModule.Import(customAttributeConstructor));
            foreach (var arg in customAttribute.ConstructorArguments)
            {
                var targetArgType = target.MainModule.GetType(arg.Type.FullName);
                var attributeArgType = targetArgType ?? arg.Type;

                attribute.ConstructorArguments.Add(new CustomAttributeArgument(
                    target.MainModule.Import(attributeArgType.Resolve()), arg.Value));
            }

            return attribute;
        }

        internal void CopyCustomAttributes(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition)
        {
            foreach (var customAttribute in type.CustomAttributes)
            {
                var attribute = CopyCustomAttribute(target, customAttribute);
                if (attribute == null)
                    continue;

                typeDefinition.CustomAttributes.Add(attribute);
            }
        }
    }
}
