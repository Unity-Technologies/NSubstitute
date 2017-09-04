using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Unity.Cecil.Visitor;

namespace NSubstitute.Weaver
{
    class MockInjectorVisitor : Visitor
    {
        readonly MethodDefinition m_HookForInstance;
        readonly Stack<List<MethodDefinition>> m_InjectedMethods = new Stack<List<MethodDefinition>>();
        readonly IList<MethodDefinition> m_Processed = new List<MethodDefinition>();
        readonly MethodReference m_CompilerGeneratedAttrCtor;
        readonly TypeReference m_ObjectType;
        readonly MethodReference m_GetTypeFromHandleMethod;
        readonly TypeReference m_TypeType;
        readonly TypeDefinition m_MulticastDelegate;

        public MockInjectorVisitor(AssemblyDefinition fakeFramework, ModuleDefinition module)
        {
            var msCorlibReference = module.AssemblyReferences.Single(ar => ar.Name == "mscorlib");

            m_HookForInstance = fakeFramework.MainModule.Types.Single(t => t.Name == "CastlePatchedInterceptorRegistry").Methods.Single(m => m.Name == "CallMockMethodOrImpl");

            var compilerServicesType = new TypeReference("System.Runtime.CompilerServices", "CompilerGeneratedAttribute", module, msCorlibReference);
            m_CompilerGeneratedAttrCtor = module.Import(compilerServicesType.Resolve().GetConstructors().Single(ctor => !ctor.IsStatic && !ctor.HasParameters));

            m_ObjectType = new TypeReference("System", "Object", module, msCorlibReference);
            m_TypeType = new TypeReference("System", "Type", module, msCorlibReference);
            m_MulticastDelegate = new TypeReference("System", "MulticastDelegate", module, msCorlibReference).Resolve();

            var resolvedSystemType = m_TypeType.Resolve();
            m_GetTypeFromHandleMethod = module.Import(resolvedSystemType.Methods.Single(m => m.Name == "GetTypeFromHandle" && m.HasParameters && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.RuntimeTypeHandle"));
        }

        protected override void Visit(TypeDefinition typeDefinition, Context context)
        {
            if (typeDefinition.Name == "<Module>")
                return;

            if (typeDefinition.HasCustomAttributes && typeDefinition.CustomAttributes.Any(ca =>
                    ca.AttributeType.FullName == m_CompilerGeneratedAttrCtor.DeclaringType.FullName
                    || ca.AttributeType.Name == "TestFixtureAttribute"))
                return;

            if (typeDefinition.BaseType != null && typeDefinition.BaseType.Resolve() == m_MulticastDelegate)
                return;

            if (typeDefinition.IsInterface)
                return;

            try
            {
                m_Processed.Clear();
                m_InjectedMethods.Push(new List<MethodDefinition>());

                // this will decompose the type and call Visit() on all its elements (fields, methods, etc.)
                base.Visit(typeDefinition, context);

                var injectedMethods = m_InjectedMethods.Pop();
                foreach (var method in injectedMethods)
                    typeDefinition.Methods.Add(method);

                InjectMockingFields(typeDefinition, injectedMethods);
                EnsureTypeHasEmptyDefaultCtor(typeDefinition, injectedMethods);
            }
            catch (Exception e)
            {
                throw new Exception($"Internal error during mock injection into type {typeDefinition.FullName}", e);
            }
        }

        static void EnsureTypeHasEmptyDefaultCtor(TypeDefinition typeDefinition, List<MethodDefinition> injectedMethods)
        {
            if (typeDefinition.IsValueType)
                return;
            if (injectedMethods.Count == 0)
                return;

            MethodDefinition ctor;
            var ctors = typeDefinition.GetConstructors().Where(candidate => !candidate.IsStatic && (candidate.Parameters.Count == 0 || candidate.Parameters.All(p => p.HasDefault)));
            if (!ctors.Any())
            {
                ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeDefinition.Module.TypeSystem.Void)
                {
                    IsManaged = true,
                    DeclaringType = typeDefinition,
                    HasThis = true,
                };

                typeDefinition.Methods.Add(ctor);
            }
            else
            {
                ctor = ctors.First();
                ctor.IsPublic = true;
            }

            var body = ctor.Body;
            var ilProcessor = body.GetILProcessor();
            body.Instructions.Clear();


            ilProcessor.Append(ilProcessor.Create(OpCodes.Ldarg_0));
            var baseDefaultCtor = (MethodReference)typeDefinition.BaseType.Resolve().GetConstructors().SingleOrDefault(candidate => !candidate.IsStatic && candidate.Parameters.Count == 0);

            if (typeDefinition.BaseType.IsGenericInstance)
            {
                baseDefaultCtor = new MethodReference(baseDefaultCtor.Name, baseDefaultCtor.ReturnType, typeDefinition.BaseType) { HasThis =  baseDefaultCtor.HasThis };
                ilProcessor.Append(ilProcessor.Create(OpCodes.Call, baseDefaultCtor));
            }
            else
            {
                ilProcessor.Append(ilProcessor.Create(OpCodes.Call, baseDefaultCtor.Module != typeDefinition.Module ? typeDefinition.Module.Import(baseDefaultCtor) : baseDefaultCtor));
            }

            ilProcessor.Append(ilProcessor.Create(OpCodes.Ret));
        }

        static void InjectMockingFields(TypeDefinition targetType, List<MethodDefinition> injectedMethods)
        {
            if (injectedMethods.Count == 0)
                return;

            AddField(targetType, "__mockInterceptor", FieldAttributes.Private | FieldAttributes.NotSerialized, targetType.Module.TypeSystem.Object);
            AddField(targetType, "__mockStaticInterceptor", FieldAttributes.Static | FieldAttributes.Private | FieldAttributes.NotSerialized, targetType.Module.TypeSystem.Object);
        }

        static void AddField(TypeDefinition targetType, string fieldName, FieldAttributes fieldAttributes, TypeReference fieldType)
        {
            if (targetType.Fields.Any(f => f.Name == fieldName))
                throw new InvalidOperationException($"Field '{fieldName}' already exist in type {targetType.FullName}");

            targetType.Fields.Add(new FieldDefinition(fieldName, fieldAttributes, fieldType));
        }

        protected override void Visit(MethodDefinition methodDefinition, Context context)
        {
            if (methodDefinition.IsCompilerControlled)
                return;

            if (m_Processed.Contains(methodDefinition))
                return;

            if (methodDefinition.IsConstructor)
                return;

            if (methodDefinition.IsAbstract)
                return;

            m_Processed.Add(methodDefinition);

            var mockedMethodName = MangleNameForMockedMethod(methodDefinition);
            CreateMockMethod(methodDefinition, mockedMethodName);

            ReplaceBodyWithProxyCall(methodDefinition);
        }

        void CreateMockMethod(MethodDefinition methodToCopy, string mockedMethodName)
        {
            var methodCopy = new MethodDefinition(mockedMethodName, methodToCopy.Attributes, methodToCopy.ReturnType);
            m_InjectedMethods.Peek().Add(methodCopy);

            methodCopy.Attributes = methodToCopy.Attributes;
            methodCopy.ImplAttributes = methodToCopy.ImplAttributes;
            methodCopy.HasThis = methodToCopy.HasThis;
            methodCopy.DeclaringType = methodToCopy.DeclaringType;
            methodCopy.IsPrivate = true;

            methodCopy.CustomAttributes.Add(new CustomAttribute(m_CompilerGeneratedAttrCtor));

            if (methodCopy.Body == null)
                methodCopy.Body = new MethodBody(methodCopy);

            if (methodToCopy.HasBody)
                methodCopy.Body.InitLocals = methodToCopy.Body.InitLocals;

            foreach (var genericParameter in methodToCopy.GenericParameters)
            {
                methodCopy.GenericParameters.Add(new GenericParameter(genericParameter.Name, methodCopy));
            }

            foreach (var parameter in methodToCopy.Parameters)
            {
                var cloneParameter = new ParameterDefinition(parameter.Name, parameter.Attributes, MapPotentialGenericParameterTypeReference(parameter.ParameterType, methodCopy));
                if (parameter.HasConstant)
                {
                    cloneParameter.HasConstant = parameter.HasConstant;
                    cloneParameter.Constant = parameter.Constant;
                }

                methodCopy.Parameters.Add(cloneParameter);
            }

            if (methodToCopy.ReturnType.IsGenericParameter)
                methodCopy.ReturnType = MapPotentialGenericParameterTypeReference(methodCopy.ReturnType, methodCopy);

            if (!methodToCopy.HasBody) return;

            // Generic Parameters
            foreach (var variable in methodToCopy.Body.Variables)
            {
                methodCopy.Body.Variables.Add(variable);
            }

            foreach (var instruction in methodToCopy.Body.Instructions)
            {
                var param = instruction.Operand as ParameterDefinition;
                if (param != null)
                {
                    instruction.Operand = methodCopy.Parameters.Single(p => p.Name == param.Name);
                }

                methodCopy.Body.Instructions.Add(instruction);
            }

            foreach (var exceptionHandler in methodToCopy.Body.ExceptionHandlers)
                methodCopy.Body.ExceptionHandlers.Add(exceptionHandler);
        }

        static string MangleNameForMockedMethod(MethodDefinition method)
        {
            return "__mock_" + method.Name;
        }

        void ReplaceBodyWithProxyCall(MethodDefinition method)
        {
            if (!method.HasBody)
                method.Body = new MethodBody(method);

            method.ImplAttributes &= ~MethodImplAttributes.InternalCall;
            method.Body.Variables.Clear();
            var il = method.Body.GetILProcessor();
            il.Body.Instructions.Clear();
            il.Body.ExceptionHandlers.Clear();

            var paramArr = new VariableDefinition("__paramarr", m_ObjectType.MakeArrayType());
            il.Body.InitLocals = true;
            il.Body.Variables.Add(paramArr);

            var module = method.DeclaringType.Module;
            if (method.HasThis)
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                if (method.DeclaringType.IsValueType)
                {
                    il.Append(il.Create(OpCodes.Ldobj, method.DeclaringType));
                    il.Append(il.Create(OpCodes.Box, method.DeclaringType));
                }
            }
            else
            {
                il.Append(il.Create(OpCodes.Ldnull));
            }

            var genericParamsCount = method.HasGenericParameters
                ? method.GenericParameters.Count
                : 0;

            il.Append(il.Create(OpCodes.Ldc_I4, genericParamsCount));
            il.Append(il.Create(OpCodes.Newarr, m_TypeType));
            for (int index = 0; index < genericParamsCount; index++)
            {
                var parameter = method.GenericParameters[index];
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Ldc_I4, index));
                il.Append(il.Create(OpCodes.Ldtoken, parameter));
                il.Append(il.Create(OpCodes.Call, m_GetTypeFromHandleMethod));
                il.Append(il.Create(OpCodes.Stelem_Ref));
            }

            var refOrOutParameters = method.Parameters.Select((p, i) => new { Item1 = p, Item2 = i }).Where(p => p.Item1.ParameterType.IsByReference).ToList();

            // create object array...
            il.Append(il.Create(OpCodes.Ldc_I4, method.Parameters.Count));
            il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
            if (refOrOutParameters.Count > 0)
            {
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Stloc, paramArr));
            }
            var offset = method.HasThis ? 1 : 0;
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                il.Append(il.Create(OpCodes.Dup)); // load array ref
                il.Append(il.Create(OpCodes.Ldc_I4, i)); // element index
                il.Append(il.Create(OpCodes.Ldarg, i + offset)); // element value
                var param = method.Parameters[i];
                if (param.ParameterType.IsByReference)
                    il.Append(GetLdindFromType(param.ParameterType.GetElementType()));
                if (param.ParameterType.GetElementType().IsValueType || param.ParameterType.GetElementType().IsGenericParameter)
                    il.Append(il.Create(OpCodes.Box, param.ParameterType.GetElementType()));
                il.Append(il.Create(OpCodes.Stelem_Ref));
            }

            var runner = module.Import(m_HookForInstance);

            il.Append(il.Create(OpCodes.Call, runner));

            // unbox
            if (method.ReturnType.MetadataType == MetadataType.Void)
                il.Append(il.Create(OpCodes.Pop));
            else if (method.ReturnType.IsValueType || method.ReturnType.IsGenericParameter)
                il.Append(il.Create(OpCodes.Unbox_Any, method.ReturnType));
            else
                il.Append(il.Create(OpCodes.Castclass, method.ReturnType));


            foreach (var param in refOrOutParameters)
            {
                il.Append(il.Create(OpCodes.Ldarg, param.Item1));

                il.Append(il.Create(OpCodes.Ldloc, paramArr));
                il.Append(il.Create(OpCodes.Ldc_I4, param.Item2));
                il.Append(il.Create(OpCodes.Ldelem_Ref));
                if (param.Item1.ParameterType.GetElementType().IsValueType || param.Item1.ParameterType.GetElementType().IsGenericParameter)
                    il.Append(il.Create(OpCodes.Unbox_Any, param.Item1.ParameterType.GetElementType()));
                else
                    il.Append(il.Create(OpCodes.Castclass, param.Item1.ParameterType.GetElementType()));

                il.Append(GetStindFromType(param.Item1.ParameterType.GetElementType()));
            }

            il.Append(il.Create(OpCodes.Ret));
        }

        static Instruction GetStindFromType(TypeReference type)
        {
            if (type.MetadataType == MetadataType.SByte)
                return Instruction.Create(OpCodes.Stind_I1);

            if (type.MetadataType == MetadataType.Int16)
                return Instruction.Create(OpCodes.Stind_I2);

            if (type.MetadataType == MetadataType.Int32)
                return Instruction.Create(OpCodes.Stind_I4);

            if (type.MetadataType == MetadataType.Int64)
                return Instruction.Create(OpCodes.Stind_I8);

            if (type.MetadataType == MetadataType.UInt64)
                return Instruction.Create(OpCodes.Stind_I8); // U8 is alias for I8 and not part of Cecil

            if (type.MetadataType == MetadataType.Single)
                return Instruction.Create(OpCodes.Stind_R4);

            if (type.MetadataType == MetadataType.Double)
                return Instruction.Create(OpCodes.Stind_R8);

            if (type.MetadataType == MetadataType.IntPtr)
                return Instruction.Create(OpCodes.Stind_I); // assume this is native int

            if (type.IsValueType || type.IsGenericParameter)
                return Instruction.Create(OpCodes.Stobj, type);

            return Instruction.Create(OpCodes.Stind_Ref);
        }

        static Instruction GetLdindFromType(TypeReference type)
        {
            if (type.MetadataType == MetadataType.SByte)
                return Instruction.Create(OpCodes.Ldind_I1);

            if (type.MetadataType == MetadataType.Int16)
                return Instruction.Create(OpCodes.Ldind_I2);

            if (type.MetadataType == MetadataType.Int32)
                return Instruction.Create(OpCodes.Ldind_I4);

            if (type.MetadataType == MetadataType.Int64)
                return Instruction.Create(OpCodes.Ldind_I8);

            if (type.MetadataType == MetadataType.Byte)
                return Instruction.Create(OpCodes.Ldind_U1);

            if (type.MetadataType == MetadataType.UInt16)
                return Instruction.Create(OpCodes.Ldind_U2);

            if (type.MetadataType == MetadataType.UInt32)
                return Instruction.Create(OpCodes.Ldind_U4);

            if (type.MetadataType == MetadataType.UInt64)
                return Instruction.Create(OpCodes.Ldind_I8); // U8 is alias for I8 and not part of Cecil

            if (type.MetadataType == MetadataType.Single)
                return Instruction.Create(OpCodes.Ldind_R4);

            if (type.MetadataType == MetadataType.Double)
                return Instruction.Create(OpCodes.Ldind_R8);

            if (type.MetadataType == MetadataType.IntPtr)
                return Instruction.Create(OpCodes.Ldind_I); // assume this is native int

            if (type.IsValueType || type.IsGenericParameter)
                return Instruction.Create(OpCodes.Ldobj, type);

            return Instruction.Create(OpCodes.Ldind_Ref);
        }

        static TypeReference MapPotentialGenericParameterTypeReference(TypeReference type, MethodDefinition targetMethod)
        {
            if (!type.IsGenericParameter)
                return type;

            var genericParameter = targetMethod.GenericParameters.SingleOrDefault(p => p.Name == type.Name);
            if (genericParameter != null)
                return genericParameter;

            return targetMethod.DeclaringType.HasGenericParameters
                ? targetMethod.DeclaringType.GenericParameters.Single(p => p.Name == type.Name)
                : null;
        }
    }
}
