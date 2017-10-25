using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
        readonly Predicate<TypeReference> m_SkipType;
        ReferenceRewriter rewriter;

        public Copier(ProcessTypeResolver processTypeResolver, Predicate<TypeReference> skipType = null)
        {
            m_SkipType = skipType;
            rewriter = new ReferenceRewriter(skipType);
        }

        public void Copy(AssemblyDefinition source, ref AssemblyDefinition target, AssemblyDefinition nsubstitute, string[] typesToCopy)
        {
            var nonRefTarget = target;
            var implementationTypes = new Dictionary<string, TypeDefinition>();
            var methodMap = new Dictionary<MethodDefinition, MethodDefinition>();
            var fakeHolder = CreateFakeHolder(nonRefTarget);
            var selfFakeHolder = CreateSelfFakeHolder(nonRefTarget, fakeHolder);
            var types = source.MainModule.Types.Where(t => !ShouldSkip(t)).Select(type => new { Definition = CreateTargetTypes(nonRefTarget, type, implementationTypes, fakeHolder), OriginalType = type }).ToList();
            types.ForEach(t => AttachBaseTypeAndFakeForwardProperties(nonRefTarget, t.OriginalType, t.Definition, implementationTypes, fakeHolder));
            types.ForEach(t => CreateTypeFieldAndForwardConstructorDefinitionsIfNeeded(nonRefTarget, t.OriginalType, t.Definition, implementationTypes, fakeHolder));
            types.ForEach(t => CreateForwardConstructorBodyAndFakeImplementationIfNeeded(nonRefTarget, t.OriginalType, t.Definition, methodMap, implementationTypes, fakeHolder));
            types.ForEach(t => AttachGenericTypeConstraints(nonRefTarget, t.OriginalType, t.Definition));
            types.ForEach(t => AttachInterfaces(nonRefTarget, t.OriginalType, t.Definition, selfFakeHolder));
            types.ForEach(t => AttachFakeHolderImplementations(nonRefTarget, t.OriginalType, t.Definition, implementationTypes, fakeHolder));
            types.ForEach(t => AttachMethods(nonRefTarget, t.OriginalType, t.Definition, implementationTypes, methodMap));
            types.ForEach(t => AttachMethodOverridesForExplicitInterfaceImplementationsThatHaveBeenFaked(nonRefTarget, t.OriginalType, t.Definition, methodMap, implementationTypes));
            types.ForEach(t => PostprocessFakeImplementations(nonRefTarget, t.OriginalType, t.Definition, implementationTypes));
        }

        private TypeDefinition CreateSelfFakeHolder(AssemblyDefinition target, TypeDefinition fakeHolder)
        {
            var selfFakeHolder = new TypeDefinition(k_NamespacePrefix, "SelfFakeHolder`1", TypeAttributes.Public | TypeAttributes.Class, target.MainModule.TypeSystem.Object);
            var gp = new GenericParameter("T", selfFakeHolder);
            selfFakeHolder.GenericParameters.Add(gp);

            selfFakeHolder.Interfaces.Add(fakeHolder.MakeGenericInstanceType(gp));

            var forwardField = new FieldDefinition("forward", FieldAttributes.Private, gp);
            var forwardFieldReference = new FieldReference(forwardField.Name, gp, selfFakeHolder.MakeGenericInstanceType(gp));
            selfFakeHolder.Fields.Add(forwardField);

            var forwardProperty = new PropertyDefinition("Forward", PropertyAttributes.None, gp);
            var forwardMethod = new MethodDefinition("get_Forward", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName, gp);
            forwardMethod.Body = new MethodBody(forwardMethod);

            forwardMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            forwardMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, forwardFieldReference));
            forwardMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            forwardProperty.GetMethod = forwardMethod;
            selfFakeHolder.Properties.Add(forwardProperty);
            selfFakeHolder.Methods.Add(forwardMethod);

            var forwardConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.MainModule.TypeSystem.Void);
            forwardConstructor.DeclaringType = selfFakeHolder;
            forwardConstructor.Parameters.Add(new ParameterDefinition("forward", ParameterAttributes.None, gp));
            forwardConstructor.Body = new MethodBody(forwardConstructor);

            var baseConstructorMethod = target.MainModule.Import(target.MainModule.TypeSystem.Object.Resolve().GetConstructors().Single(c => c.IsPublic));
            forwardConstructor
                .AddInstruction(OpCodes.Ldarg_0)
                .AddInstruction(OpCodes.Call, baseConstructorMethod)
                .AddInstruction(OpCodes.Ldarg_0)
                .AddInstruction(OpCodes.Ldarg_1)
                .AddInstruction(OpCodes.Stfld, forwardFieldReference)
                .AddInstruction(OpCodes.Ret);

            selfFakeHolder.Methods.Add(forwardConstructor);

            target.MainModule.Types.Add(selfFakeHolder);
            return selfFakeHolder;
        }

        class Node<T>
        {
            Node<T> parent;
            T element;
            List<Node<T>> children = new List<Node<T>>();

            public Node(Node<T> parent, T element)
            {
                this.parent = parent;
                this.element = element;
            }

            public T Element => element;
            public IEnumerable<Node<T>> Children => children;
            public Node<T> Parent => parent;

            public Node<T> Add(T element)
            {
                var item = new Node<T>(this, element);
                children.Add(item);
                return item;
            }
        }

        class PreorderVisitor<T>
        {
            readonly Action<Node<T>> m_Callback;

            public PreorderVisitor(Action<Node<T>> callback)
            {
                m_Callback = callback;
            }

            public void Visit(Node<T> node)
            {
                m_Callback(node);
                foreach (var child in node.Children)
                    Visit(child);
            }
        }

        public static IEnumerable<TypeReference> GetAllInterfaces(AssemblyDefinition target, TypeReference reference)
        {
            var node = new Node<Tuple<TypeReference, TypeDefinition>>(null, Tuple.Create(reference, reference.Resolve()));
            var q = new Queue<Node<Tuple<TypeReference, TypeDefinition>>>(new[] { node });

            while (q.Count > 0)
            {
                var e = q.Dequeue();

                foreach (var iface in e.Element.Item2.Interfaces)
                {
                    var n = e.Add(Tuple.Create(iface, iface.Resolve()));
                    q.Enqueue(n);
                }
            }

            var rv = new List<TypeReference>();
            var v = new PreorderVisitor<Tuple<TypeReference, TypeDefinition>>(e =>
            {
                if (e.Element.Item1 is GenericInstanceType genericInstance)
                {
                    TypeReference Rewrite(TypeReference rref)
                    {
                        if (rref is GenericParameter gp)
                        {
                            var ce = e;
                            var idx = -1;
                            while (ce != null && idx == -1)
                            {
                                idx = ce.Element.Item2.GenericParameters.IndexOf(gp);
                                if (idx == -1)
                                {
                                    ce = ce.Parent;
                                }
                            }
                            if (ce == null)
                            {
                                idx = reference.Resolve().GenericParameters.IndexOf(gp);
                                if (idx == -1)
                                    throw new InvalidOperationException("I'm missing my generic parameter in my call hierarchy and now I'm sad");

                                if (reference is GenericInstanceType git3)
                                    return git3.GenericArguments[idx];

                                return reference.GenericParameters[idx];
                            }

                            if (ce.Element.Item1 is GenericInstanceType git2)
                            {
                                if (git2.GenericArguments[idx].IsGenericParameter)
                                    return Rewrite(git2.GenericArguments[idx]);
                                return git2.GenericArguments[idx];
                            }

                            return ce.Element.Item1.GenericParameters[idx];
                        }

                        if (rref is GenericInstanceType git)
                        {
                            var arrrgs = git.GenericArguments.Select(Rewrite).ToArray();
                            return rref.Resolve().MakeGenericInstanceType(arrrgs);
                        }

                        return rref;
                    }

                    var args = genericInstance.GenericArguments.Select(Rewrite).ToArray();
                    rv.Add(e.Element.Item2.MakeGenericInstanceType(args));
                    return;
                }

                if (e.Element.Item1.HasGenericParameters)
                {
                    throw new NotImplementedException();
                }

                rv.Add(e.Element.Item1);
            });
            foreach (var child in node.Children)
                v.Visit(child);

            return rv;
        }

        void AttachFakeHolderImplementations(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes, TypeDefinition fakeHolder)
        {
            if (implementationTypes.TryGetValue(typeDefinition.FullName, out var implementationType))
            {
                AttachFakeHolderImplementations(target, type, implementationType, implementationTypes, fakeHolder);
            }

            var field = typeDefinition.Fields.SingleOrDefault(f => f.Name == k_FakeForward);
            if (field == null)
                return;

            FieldReference fieldReference = field;
            if (type.HasGenericParameters)
                fieldReference = new FieldReference(fieldReference.Name, fieldReference.FieldType, typeDefinition.MakeGenericInstanceType(typeDefinition.GenericParameters.ToArray()));


            foreach (var iface in GetAllInterfaces(target, typeDefinition).ToList().Where(iface => iface.IsGenericInstance && iface.Resolve().FullName == "Fake.__FakeHolder`1")) // TODO: remove ToList
            {
                if (!typeDefinition.Interfaces.Any(t => t.FullName == iface.FullName))
                {
                    typeDefinition.Interfaces.Add(rewriter.ImportRecursively(target, iface));
                }

                var name = new StringBuilder();
                name.Append(iface.Namespace);
                name.Append(".");
                name.Append(iface.Name.Replace("__FakeHolder`1", "__FakeHolder"));
                name.Append("<");
                var genericArgumentType = ((GenericInstanceType)iface).GenericArguments[0];
                name.Append("global::"); // TODO: consider types from different assemblies
                name.Append(genericArgumentType.FullName);
                name.Append(">");

                if (typeDefinition.Properties.Any(p => p.Name == $"{name}.Forward"))
                    continue;

                var reinstantiate = rewriter.Rewrite(target, type, typeDefinition, genericArgumentType, lookupFake: false);
                var fakeHolderProperty = new PropertyDefinition($"{name}.Forward", PropertyAttributes.None, reinstantiate);
                fakeHolderProperty.HasThis = true;
                typeDefinition.Properties.Add(fakeHolderProperty);

                var method = new MethodDefinition($"{name}.get_Forward", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName, fakeHolderProperty.PropertyType);
                var fakeHolderGetForward = fakeHolder.Methods[0].MakeHostInstanceGeneric(target, false, reinstantiate);
                fakeHolderGetForward.ReturnType = fakeHolder.GenericParameters[0];
                method.Overrides.Add(fakeHolderGetForward);
                method.DeclaringType = typeDefinition;

                method.Body = new MethodBody(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fieldReference));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

                fakeHolderProperty.GetMethod = method;
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            { // TODO: non-public nested types
                AttachFakeHolderImplementations(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], implementationTypes, fakeHolder);
            }
        }

        TypeDefinition CreateFakeHolder(AssemblyDefinition target)
        {
            var type = new TypeDefinition(k_NamespacePrefix, "__FakeHolder`1", TypeAttributes.Interface | TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.AutoClass | TypeAttributes.AnsiClass);
            var genericParameter = new GenericParameter("T", type);
            type.GenericParameters.Add(genericParameter);

            var forwardMethod = new MethodDefinition("get_Forward", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Virtual | MethodAttributes.NewSlot, genericParameter);
            type.Methods.Add(forwardMethod);

            var forwardProperty = new PropertyDefinition("Forward", PropertyAttributes.None, genericParameter);
            forwardProperty.GetMethod = forwardMethod;
            type.Properties.Add(forwardProperty);

            target.MainModule.Types.Add(type);

            return type;
        }

        void AttachBaseTypeAndFakeForwardProperties(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes, TypeDefinition fakeHolder)
        {
            typeDefinition.BaseType = rewriter.Rewrite(target, type, typeDefinition, type.BaseType, null, null, null, true);

            typeDefinition.Methods.Clear();

            if (NeedsFakeImplementation(type))
            {
                //var propertyDefinition = new PropertyDefinition(GetFakeForwardPropertyName(type), PropertyAttributes.None, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
                //propertyDefinition.HasThis = true;
                //propertyDefinition.GetMethod = new MethodDefinition(GetFakeForwardPropertyGetterNamePrefix(type), MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Public, propertyDefinition.PropertyType);
                //propertyDefinition.GetMethod.DeclaringType = typeDefinition;

                //typeDefinition.Properties.Add(propertyDefinition);
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                AttachBaseTypeAndFakeForwardProperties(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], implementationTypes, fakeHolder);
            }
        }

        void AttachMethodOverridesForExplicitInterfaceImplementationsThatHaveBeenFaked(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<MethodDefinition, MethodDefinition> methodMap, Dictionary<string, TypeDefinition> implementationTypes)
        {
            var typeDefinitionMethodIndex = NeedsTypeFieldAndForwardConstructor(type) ? 1 : 0;
            for (var index = 0; index < type.Methods.Count; index++)
            {
                var method = type.Methods[index];

                if (ShouldSkip(method))
                    continue;

                var methodDefinition = typeDefinition.Methods[typeDefinitionMethodIndex++];

                foreach (var methodOverride in method.Overrides)
                {
                    if (ShouldSkip(methodOverride.DeclaringType.Resolve()))
                        continue;

                    var overrideMethod = rewriter.Rewrite(target, type, typeDefinition, method, methodDefinition, methodOverride, methodMap);
                    methodDefinition.Overrides.Add(overrideMethod);
                }
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                AttachMethodOverridesForExplicitInterfaceImplementationsThatHaveBeenFaked(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], methodMap, implementationTypes);
            }
        }

        void PostprocessFakeImplementations(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes)
        {
            foreach (var prop in typeDefinition.Properties.Where(p => /*p.Name.Contains(k_FakeForwardPropertyNamePrefix) ||*/ p.Name.Contains("__FakeHolder")))
                typeDefinition.Methods.Add(prop.GetMethod);

            if (implementationTypes.TryGetValue(typeDefinition.FullName, out TypeDefinition implType))
                foreach (var prop in implType.Properties.Where(p => /*p.Name.Contains(k_FakeForwardPropertyNamePrefix) ||*/ p.Name.Contains("__FakeHolder")))
                    implType.Methods.Add(prop.GetMethod);

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                PostprocessFakeImplementations(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], implementationTypes);
            }
        }

        void AttachMethods(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes, Dictionary<MethodDefinition, MethodDefinition> methodMap)
        {
            for (var index = 0; index < type.Methods.Count; index++)
            {
                var method = type.Methods[index];

                if (ShouldSkip(method))
                    continue;

                var returnType = target.MainModule.TypeSystem.Void; // method return type may depend on generic method parameters and need to be resolved later

                var methodDefinition = new MethodDefinition(GetFakedMethodName(target, type, method), CreateMethodAttributes(method), returnType);
                methodDefinition.DeclaringType = typeDefinition;
                typeDefinition.Methods.Add(methodDefinition);
                methodMap[method] = methodDefinition;

                foreach (var gp in method.GenericParameters)
                    methodDefinition.GenericParameters.Add(new GenericParameter(gp.Name, methodDefinition));

                methodDefinition.ReturnType = rewriter.Rewrite(target, type, typeDefinition, method.ReturnType, method, methodDefinition, methodMap, true);

                foreach (var p in method.Parameters)
                    methodDefinition.Parameters.Add(new ParameterDefinition(p.Name, CreateParameterAttributes(p.Attributes), rewriter.Rewrite(target, type, typeDefinition, p.ParameterType, method, methodDefinition, methodMap, true)));

                if (!NeedsFakeImplementation(type))
                    CreateWrappingMethodBody(target, type, methodDefinition, method, typeDefinition, type.Methods[index], methodMap, implementationTypes);
            }

            if (implementationTypes.TryGetValue(typeDefinition.FullName, out TypeDefinition implementationDefinition))
            {
                for (var index = 0; index < typeDefinition.Methods.Count; index++)
                {
                    var method = typeDefinition.Methods[index];

                    var implMethodDefinition = new MethodDefinition(method.Name, method.Attributes & ~MethodAttributes.Abstract, target.MainModule.TypeSystem.Void);
                    implMethodDefinition.DeclaringType = implementationDefinition;

                    methodMap[method] = implMethodDefinition;

                    foreach (var gp in method.GenericParameters)
                        implMethodDefinition.GenericParameters.Add(new GenericParameter(gp.Name, implMethodDefinition));

                    implMethodDefinition.ReturnType = rewriter.Rewrite(target, typeDefinition, implementationDefinition, method.ReturnType, method, implMethodDefinition, methodMap, true);

                    foreach (var p in method.Parameters)
                        implMethodDefinition.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, rewriter.Rewrite(target, typeDefinition, implementationDefinition, p.ParameterType, method, implMethodDefinition, methodMap, true)));

                    CreateWrappingMethodBody(target, type, implMethodDefinition, method, implementationDefinition, type.Methods[index], methodMap, implementationTypes);

                    implementationDefinition.Methods.Add(implMethodDefinition);
                }
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                AttachMethods(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], implementationTypes, methodMap);
            }
        }

        static string GetFakedMethodName(AssemblyDefinition target, TypeDefinition type, MethodDefinition method)
        {
            var matchingInterface = type.Interfaces.FirstOrDefault(i => method.Name.StartsWith($"{i.FullName}."));
            //var matchingInterface = type.Interfaces.FirstOrDefault(i => method.Overrides[0].DeclaringType.FullName.Equals(i.FullName)); // TODO: Hack?
            if (matchingInterface == null)
                return method.Name;

            var fakedNamespace = RewriteNamespace(matchingInterface.Namespace);
            var newName = $"{fakedNamespace}.{matchingInterface.Name}";
            var fakedInterface = target.MainModule.Types.FirstOrDefault(t => t.FullName == newName);
            if (fakedInterface == null)
                return method.Name;

            return fakedInterface.FullName + method.Name.Substring(matchingInterface.FullName.Length);
        }

        static ParameterAttributes CreateParameterAttributes(ParameterAttributes parameterAttributes)
        {
            return parameterAttributes & ~ParameterAttributes.HasFieldMarshal;
        }

        static MethodAttributes CreateMethodAttributes(MethodDefinition method)
        {
            if (method.DeclaringType.IsInterface)
                return method.Attributes;
            return method.Attributes & ~MethodAttributes.Abstract & ~MethodAttributes.HasSecurity & ~MethodAttributes.PInvokeImpl;
        }

        bool ShouldSkip(MethodDefinition method)
        {
            if (!method.IsPublic)
            {
                if (method.Overrides.Any(o => !ShouldSkip(o.Resolve())))
                    return false;

                return true;
            }

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

            if (!fullName.Contains("/"))
                return target.MainModule.Types.SingleOrDefault(t => t.FullName == fullName);

            var parts = fullName.Split('/');
            var baseType = target.MainModule.Types.SingleOrDefault(t => t.FullName == parts[0]);
            if (baseType == null)
                return null;

            for (var i = 1; i < parts.Length; ++i)
            {
                baseType = baseType.NestedTypes.SingleOrDefault(t => t.Name == parts[i]);
                if (baseType == null)
                    return null;
            }

            return baseType;
        }

        void CreateWrappingMethodBody(AssemblyDefinition target, TypeDefinition type, MethodDefinition methodDefinition, MethodDefinition method, TypeDefinition typeDefinition, MethodDefinition originalMethod, Dictionary<MethodDefinition, MethodDefinition> methodMap, Dictionary<string, TypeDefinition> implementationTypes)
        {
            methodDefinition.Body = new MethodBody(methodDefinition);

            if (!method.IsStatic)
            {
                methodDefinition.AddInstruction(OpCodes.Ldarg_0);

                var forwardField = typeDefinition.Fields.Single(f => f.Name == k_FakeForward);

                var fieldType = forwardField.FieldType;
                TypeReference fieldDeclaringTypeReference = typeDefinition;
                if (fieldDeclaringTypeReference.HasGenericParameters)
                    fieldDeclaringTypeReference = fieldDeclaringTypeReference.MakeGenericInstanceType(fieldDeclaringTypeReference.GenericParameters.ToArray());

                var forwardFieldReference = new FieldReference(forwardField.Name, fieldType, fieldDeclaringTypeReference);

                var loadFieldInstructionType = forwardField.FieldType.IsValueType ? OpCodes.Ldflda : OpCodes.Ldfld;
                methodDefinition.AddInstruction(loadFieldInstructionType, forwardFieldReference);
            }

            for (var i = 0; i < methodDefinition.Parameters.Count; i++)
            {
                var param = methodDefinition.Parameters[i];
                var paramType = GetModuleTypeDefinition(target, param.ParameterType);
                if (paramType == null)
                {
                    if (param.ParameterType is GenericParameter gp)
                    {
                        var fakeConstraintType =
                            gp.Constraints.FirstOrDefault(c => c.Resolve().FullName == "Fake.__FakeHolder`1");
                        if (fakeConstraintType == null)
                        {
                            methodDefinition.AddInstruction(OpCodes.Ldarg, param);
                            continue;
                        }

                        var fakeConstraintArgument = ((GenericInstanceType) fakeConstraintType).GenericArguments[0];
                        var fakeConstraintForward = fakeConstraintType.Resolve().Methods.Single()
                            .MakeHostInstanceGeneric(target, false, fakeConstraintArgument);
                        fakeConstraintForward.ReturnType = fakeConstraintType.Resolve().GenericParameters[0];

                        methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarga_S, param));
                        methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Constrained, gp));
                        methodDefinition.AddInstruction(OpCodes.Callvirt, fakeConstraintForward);
                        continue;
                    }

                    methodDefinition.AddInstruction(OpCodes.Ldarg, param);
                    continue;
                }

                methodDefinition.AddInstruction(OpCodes.Ldarg, param);

                var fakeForwardName = $"Fake.__FakeHolder`1<{(paramType.FullName.StartsWith("Fake.") ? paramType.FullName.Substring(5) : paramType.FullName)}>";
                var fakeForwardType = paramType.Interfaces.FirstOrDefault(zz => zz.FullName == fakeForwardName);
                var fakeForwardArgument = (fakeForwardType as GenericInstanceType)?.GenericArguments[0];
                var fakeForward = fakeForwardType?.Resolve().Methods.Single().MakeHostInstanceGeneric(target, false, fakeForwardArgument);
                if (fakeForward != null)
                    fakeForward.ReturnType = fakeForwardType.Resolve().GenericParameters[0];

                if (IsFakedType(param.ParameterType) && fakeForward != null)
                {
                    methodDefinition.AddInstruction(OpCodes.Callvirt, fakeForward);
                }
                else if (paramType == typeDefinition) // TODO: Generics
                {
                    var forwardField = typeDefinition.Fields.Single(f => f.Name == k_FakeForward);

                    var fieldType = forwardField.FieldType;
                    TypeReference fieldDeclaringTypeReference = typeDefinition;
                    if (fieldDeclaringTypeReference.HasGenericParameters)
                        fieldDeclaringTypeReference = fieldDeclaringTypeReference.MakeGenericInstanceType(fieldDeclaringTypeReference.GenericParameters.ToArray());

                    var forwardFieldReference = new FieldReference(forwardField.Name, fieldType, fieldDeclaringTypeReference);

                    methodDefinition.AddInstruction(OpCodes.Ldfld, forwardFieldReference);
                }
            }

            var targetMethod = RewriteMethodReference(target, type, typeDefinition, method, methodDefinition, originalMethod, methodMap);

            if (originalMethod.DeclaringType.Resolve() == type && type.Name != typeDefinition.Name && targetMethod.DeclaringType.HasGenericParameters && !targetMethod.DeclaringType.IsGenericInstance)
                targetMethod = targetMethod.MakeHostInstanceGeneric(target, false, typeDefinition.GenericParameters.Skip(type.GenericParameters.Count).ToArray());

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

        private static bool IsFakedType(TypeReference reference)
        {
            if (reference.IsNested)
                return IsFakedType(reference.DeclaringType);

            return reference.Namespace == k_NamespacePrefix || reference.Namespace.StartsWith($"{k_NamespacePrefix}.");
        }

        MethodReference RewriteMethodReference(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, MethodDefinition method, MethodDefinition methodDefinition, MethodDefinition methodReference, Dictionary<MethodDefinition, MethodDefinition> methodMap, bool importFinalReference = true)
        {
            var reference = (MethodReference)methodReference;
            if (methodReference.HasOverrides && !methodReference.IsPublic)
            {
                reference = methodReference.Overrides[0];
                return RewriteMethodReference(target, type, typeDefinition, method, methodDefinition, reference.Resolve(), methodMap);
            }

            return rewriter.Reinstantiate(target, type, typeDefinition, method, methodDefinition, methodReference, methodMap); // TODO reference
        }

        TypeDefinition CreateFakeImplementation(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<MethodDefinition, MethodDefinition> methodMap, Dictionary<string, TypeDefinition> implementationTypes, TypeDefinition fakeHolder)
        {
            var fakeImplementation = new TypeDefinition(typeDefinition.Namespace, $"_FakeImpl_{typeDefinition.Name}", TypeAttributes.Class | TypeAttributes.Public, target.MainModule.TypeSystem.Object);
            if (typeDefinition.HasGenericParameters)
            {
                foreach (var gp in typeDefinition.GenericParameters)
                    fakeImplementation.GenericParameters.Add(new GenericParameter(gp.Name, fakeImplementation));
            }

            for (var i = 0; i < type.GenericParameters.Count; ++i)
            {
                fakeImplementation.GenericParameters[i].Constraints.Add(fakeHolder.MakeGenericInstanceType(fakeImplementation.GenericParameters[type.GenericParameters.Count + i]));
            }


            fakeImplementation.Interfaces.Add(typeDefinition.HasGenericParameters ? (TypeReference)typeDefinition.MakeGenericInstanceType(fakeImplementation.GenericParameters.ToArray()) : typeDefinition);

            CreateFakeFieldAndForwardConstructorDefinitions(target, type, fakeImplementation, fakeHolder);
            CreateFakeFieldAndForwardConstructor(target, type, fakeImplementation, methodMap);

            target.MainModule.Types.Add(fakeImplementation);

            return fakeImplementation;
        }

        void AttachInterfaces(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeReference selfFakeHolder)
        {
            foreach (var iface in type.Interfaces)
            {
                typeDefinition.Interfaces.Add(rewriter.Rewrite(target, type, typeDefinition, iface, null, null, null, true, selfFakeHolder));
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                AttachInterfaces(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], selfFakeHolder);
            }
        }

        static bool ShouldSkip(TypeDefinition type)
        {
            if (!type.IsPublic && !type.IsNestedPublic)
                return true;

            if (type.IsEnum)
                return true;

            if (IsAttribute(type))
                return true;

            if (type.Name == "<Module>")
                return true;

            if (type.FullName == "System.Void" || type.FullName == "System.Array" || type.FullName == "System.String" || type.FullName == "System.Boolean" || type.FullName == "System.Single" || type.FullName == "System.Int32" || type.FullName == "System.Int64")
                return true;

            return false;
        }

        static bool IsAttribute(TypeDefinition type)
        {
            return type.BaseType != null && (type.BaseType.FullName == "System.Attribute" || IsAttribute(type.BaseType.Resolve()));
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

            // TODO add generic constraints to __T generic parameters
            for (var i = 0; i < type.GenericParameters.Count; ++i)
            {
                var originalGenericParameter = type.GenericParameters[i];
                if (!originalGenericParameter.HasConstraints)
                    continue;

                var definitionGenericParameter = typeDefinition.GenericParameters[i];
                for (var j = 0; j < originalGenericParameter.Constraints.Count; j++)
                {
                    var constraint = originalGenericParameter.Constraints[j];
                    definitionGenericParameter.Constraints.Insert(j,
                        rewriter.Rewrite(target, type, typeDefinition, constraint, null, null, null, true));
                }
            }

            for (var i = 0; i < type.GenericParameters.Count; ++i)
            {
                var originalGenericParameter = type.GenericParameters[i];
                if (!originalGenericParameter.HasConstraints)
                    continue;

                var definitionGenericParameter = typeDefinition.GenericParameters[i + type.GenericParameters.Count];
                foreach (var constraint in originalGenericParameter.Constraints)
                    definitionGenericParameter.Constraints.Add(
                        rewriter.ImportRecursively(target,
                            rewriter.ReplaceGenericParameter(constraint,
                                type.GenericParameters.ToArray(),
                                typeDefinition.GenericParameters.Skip(type.GenericParameters.Count).ToArray())));
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                AttachGenericTypeConstraints(target, type.NestedTypes[i], typeDefinition.NestedTypes[i]);
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
                importedTypeReference = importedTypeReference.MakeGenericInstanceType(typeDefinition.GenericParameters.Skip(type.GenericParameters.Count).Cast<TypeReference>().ToArray());
            }

            return importedTypeReference;
        }

        TypeDefinition CreateTargetTypes(AssemblyDefinition target, TypeDefinition type, Dictionary<string, TypeDefinition> implementationTypes, TypeDefinition fakeHolder)
        {
            var typeDefinition = new TypeDefinition(RewriteNamespace(type.Namespace), RewriteGenericTypeName(type), CreateTargetTypeAttributes(type));

            foreach (var gp in type.GenericParameters)
            {
                typeDefinition.GenericParameters.Add(new GenericParameter(gp.Name, typeDefinition));
            }
            foreach (var gp in type.GenericParameters)
                typeDefinition.GenericParameters.Add(new GenericParameter($"__{gp.Name}", typeDefinition));

            for (var i = 0; i < type.GenericParameters.Count; ++i)
            {
                typeDefinition.GenericParameters[i].Constraints.Add(fakeHolder.MakeGenericInstanceType(typeDefinition.GenericParameters[type.GenericParameters.Count + i]));
            }

            var importedType = type.HasGenericParameters ? target.MainModule.Import(type).MakeGenericInstanceType(typeDefinition.GenericParameters.Skip(type.GenericParameters.Count).ToArray()) : target.MainModule.Import(type);
            var fakeHolderInstance = fakeHolder.MakeGenericInstanceType(importedType);
            typeDefinition.Interfaces.Add(fakeHolderInstance);

            CreateNestedTypes(target, type, typeDefinition, fakeHolder);

            target.MainModule.Types.Add(typeDefinition);

            //typeDefinition.BaseType = RewriteTypeReference(target, type, typeDefinition, null, null, type.BaseType);

            //typeDefinition.Methods.Clear();

            ////if (NeedsTypeFieldAndForwardConstructor(type))
            ////{
            ////    CreateFakeFieldAndForwardConstructor(target, type, typeDefinition);
            ////}

            //target.MainModule.Types.Add(typeDefinition);

            //if (NeedsFakeImplementation(type))
            //{
            //    var propertyDefinition = new PropertyDefinition(GetFakeForwardPropertyName(type), PropertyAttributes.None, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
            //    propertyDefinition.HasThis = true;
            //    propertyDefinition.GetMethod = new MethodDefinition(GetFakeForwardPropertyGetterNamePrefix(type), MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Public, propertyDefinition.PropertyType);
            //    propertyDefinition.GetMethod.DeclaringType = typeDefinition;

            //    typeDefinition.Properties.Add(propertyDefinition);

            //    //implementationTypes[typeDefinition.FullName] = CreateFakeImplementation(target, type, typeDefinition);
            //}

            return typeDefinition;
        }

        static string RewriteGenericTypeName(TypeDefinition type)
        {
            var name = type.Name;
            if (name.Contains("`"))
            {
                var parts = name.Split('`');
                name = $"{parts[0]}`{int.Parse(parts[1]) * 2}";
            }
            return name;
        }

        void CreateNestedTypes(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeDefinition fakeHolder)
        {
            foreach (var nestedType in type.NestedTypes)
            {
                var nestedTypeDefinition = new TypeDefinition(nestedType.Namespace, RewriteGenericTypeName(nestedType), CreateTargetTypeAttributes(nestedType));

                foreach (var gp in nestedType.GenericParameters)
                {
                    nestedTypeDefinition.GenericParameters.Add(new GenericParameter(gp.Name, nestedTypeDefinition));
                }
                // TODO: test with nested types with generic parameters

                var importedType = nestedType.HasGenericParameters ? target.MainModule.Import(nestedType).MakeGenericInstanceType(nestedTypeDefinition.GenericParameters.Skip(nestedType.GenericParameters.Count).ToArray()) : target.MainModule.Import(nestedType);
                var fakeHolderInstance = fakeHolder.MakeGenericInstanceType(importedType);
                nestedTypeDefinition.Interfaces.Add(fakeHolderInstance);

                typeDefinition.NestedTypes.Add(nestedTypeDefinition);

                CreateNestedTypes(target, nestedType, nestedTypeDefinition, fakeHolder);
            }
        }

        void CreateTypeFieldAndForwardConstructorDefinitionsIfNeeded(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<string, TypeDefinition> implementationTypes, TypeDefinition fakeHolder)
        {
            if (NeedsTypeFieldAndForwardConstructor(type))
            {
                CreateFakeFieldAndForwardConstructorDefinitions(target, type, typeDefinition, fakeHolder);
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                CreateTypeFieldAndForwardConstructorDefinitionsIfNeeded(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], implementationTypes, fakeHolder);
            }
        }

        void CreateFakeFieldAndForwardConstructorDefinitions(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, TypeDefinition fakeHolder)
        {
            var field = new FieldDefinition(k_FakeForward, FieldAttributes.Private, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
            typeDefinition.Fields.Add(field);

            var forwardConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.MainModule.TypeSystem.Void);
            forwardConstructor.DeclaringType = typeDefinition;
            forwardConstructor.Parameters.Add(new ParameterDefinition("forward", ParameterAttributes.None, field.FieldType));
            typeDefinition.Methods.Add(forwardConstructor);

            //if (NeedsFakeForwardProperties(type))
            //{
            //    foreach (var myType in GetTypesToCreateForwardPropertiesFor(target, type))
            //    {
            //        if (!NeedsFakeForwardPropertyDefinition(myType.Resolve()))
            //            continue;

            //        var rewriteTypeReference = RewriteTypeReference(target, type, typeDefinition, null, null, myType, null, lookupFake: false);
            //        if (rewriteTypeReference.HasGenericParameters && !rewriteTypeReference.IsGenericInstance)
            //            rewriteTypeReference = rewriteTypeReference.MakeGenericInstanceType(typeDefinition.GenericParameters.Skip(type.GenericParameters.Count).ToArray());
            //        var fakeForwardProp = new PropertyDefinition(GetFakeForwardPropertyImplementationName(myType), PropertyAttributes.None, rewriteTypeReference);
            //        fakeForwardProp.HasThis = true;
            //        typeDefinition.Properties.Add(fakeForwardProp);

            //        var methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName;
            //        if (myType == type.BaseType)
            //            methodAttributes = methodAttributes & ~MethodAttributes.NewSlot;
            //        var getFakeForwardProp = new MethodDefinition(GetFakeForwardPropertyGetterImplementationNamePrefix(myType), methodAttributes, fakeForwardProp.PropertyType);
            //        getFakeForwardProp.DeclaringType = typeDefinition;

            //        fakeForwardProp.GetMethod = getFakeForwardProp;
            //    }
            //}
        }

        void CreateForwardConstructorBodyAndFakeImplementationIfNeeded(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<MethodDefinition, MethodDefinition> methodMap, Dictionary<string, TypeDefinition> implementationTypes, TypeDefinition fakeHolder)
        {
            if (NeedsTypeFieldAndForwardConstructor(type))
            {
                CreateFakeFieldAndForwardConstructor(target, type, typeDefinition, methodMap);
            }

            if (NeedsFakeImplementation(type))
            {
                implementationTypes[typeDefinition.FullName] = CreateFakeImplementation(target, type, typeDefinition, methodMap, implementationTypes, fakeHolder);
            }

            for (var i = 0; i < type.NestedTypes.Count; ++i)
            {
                CreateForwardConstructorBodyAndFakeImplementationIfNeeded(target, type.NestedTypes[i], typeDefinition.NestedTypes[i], methodMap, implementationTypes, fakeHolder);
            }
        }

        static TypeAttributes CreateTargetTypeAttributes(TypeDefinition type)
        {
            if (type.Attributes.HasFlag(TypeAttributes.Interface))
                return type.Attributes;
            return type.Attributes & ~TypeAttributes.Abstract & ~TypeAttributes.HasSecurity;
        }

        string GetFakeForwardPropertyGetterNamePrefix(TypeReference type)
        {
            throw new InvalidOperationException();
//            return k_FakeForwardPropertyGetterNamePrefix + GetSafeNameFromTypeReferenceFullNameStrippingFake(type);
        }

        string GetFakeForwardPropertyName(TypeReference type)
        {
            throw new InvalidOperationException();
//            return k_FakeForwardPropertyNamePrefix + GetSafeNameFromTypeReferenceFullNameStrippingFake(type);
        }

        static string GetSafeNameFromTypeReferenceFullNameStrippingFake(TypeReference type)
        {
            throw new InvalidOperationException();
            //var fullName = type.FullName;

            //if (type.IsGenericInstance)
            //    fullName = type.Resolve().FullName;

            //if (type.Namespace == "Fake" || type.Namespace.StartsWith("Fake."))
            //    fullName = fullName.Substring(5);

            //return fullName.Replace(".", "_dot_").Replace("+", "_plus_").Replace("`", "_tick_").Replace("/", "_slash_");
        }

        IEnumerable<TypeReference> GetTypesToCreateForwardPropertiesFor(AssemblyDefinition target, TypeDefinition type)
        {
            throw new InvalidOperationException();
            //yield return type;
            //foreach (var iface in type.Interfaces)
            //{
            //    if (ShouldSkip(iface.Resolve()))
            //        continue;

            //    yield return iface;
            //}
            //var baseType = GetModuleTypeDefinition(target, type.BaseType);
            //if (baseType != null)
            //    yield return type.BaseType;
        }

        void CreateFakeFieldAndForwardConstructor(AssemblyDefinition target, TypeDefinition type, TypeDefinition typeDefinition, Dictionary<MethodDefinition, MethodDefinition> methodMap)
        {
            //var field = new FieldDefinition(k_FakeForward, FieldAttributes.Private, MakeGenericInstanceTypeIfNecessary(target, type, type, typeDefinition));
            //typeDefinition.Fields.Add(field);

            //var forwardConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.MainModule.TypeSystem.Void);
            //forwardConstructor.DeclaringType = typeDefinition;
            //forwardConstructor.Parameters.Add(new ParameterDefinition("forward", ParameterAttributes.None, field.FieldType));

            var field = typeDefinition.Fields.Single();
            FieldReference fieldReference = field;
            if (type.HasGenericParameters)
                fieldReference = new FieldReference(fieldReference.Name, fieldReference.FieldType, typeDefinition.MakeGenericInstanceType(typeDefinition.GenericParameters.ToArray()));

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
                .AddInstruction(OpCodes.Stfld, fieldReference)
                .AddInstruction(OpCodes.Ret);

            //typeDefinition.Methods.Add(forwardConstructor);

            if (false /*NeedsFakeForwardProperties(type)*/)
            {
//                var i = -1;
//                foreach (var myType in GetTypesToCreateForwardPropertiesFor(target, type))
//                {
//                    if (!NeedsFakeForwardPropertyDefinition(myType.Resolve()))
//                        continue;

//                    ++i;

//                    var fakeForwardProp = typeDefinition.Properties[i];

//                    var getFakeForwardProp = fakeForwardProp.GetMethod;
//                    getFakeForwardProp.AddInstruction(OpCodes.Ldarg_0)
//                        .AddInstruction(OpCodes.Ldfld, field)
//                        .AddInstruction(OpCodes.Ret);

//                    if (typeDefinition.Name != myType.Name) // HACK: verify same types with and without Fake namespace correctly
//                    {
//                        var baseMethod = GetModuleTypeDefinition(target, myType.Resolve()).Properties.Single(p => p.Name == GetFakeForwardPropertyImplementationName(myType.Resolve()) || p.Name == GetFakeForwardPropertyName(myType.Resolve()));
//                        var reference = RewriteMethodReference(target, myType.Resolve(), typeDefinition, baseMethod.GetMethod, getFakeForwardProp, baseMethod.GetMethod, methodMap, false);
//                        if (myType.IsGenericInstance)
//                            reference = reference.MakeHostInstanceGeneric(target, ((GenericInstanceType)myType).GenericArguments.Select(ga => RewriteTypeReference(target, type, typeDefinition, null, null, ga, null, lookupFake: false)).ToArray());
//                        else if (myType.HasGenericParameters && !reference.DeclaringType.IsGenericInstance)
//                            reference = reference.MakeHostInstanceGeneric(target, myType.GenericParameters.ToArray()); // TODO: Only do this for faked types
////                        var reference = new MethodReference(.Name, baseMethod);
//                        //getFakeForwardProp.Overrides.Add(myType.IsGenericInstance ? new MethodReference(baseMethod.GetMethod.Name, RewriteTypeReference(target, myType.Resolve(), typeDefinition, null, null, myType), myType) : baseMethod.GetMethod);
//                        getFakeForwardProp.Overrides.Add(reference);
//                    }

//                    fakeForwardProp.GetMethod = getFakeForwardProp;
//                }
            }
        }

        string GetFakeForwardPropertyImplementationName(TypeReference type)
        {
            throw new InvalidOperationException();
            //var idx = type.FullName.LastIndexOf("`");
            //if (idx == -1)
            //    return $"Fake.{type.FullName.Replace("/", "+")}.{GetFakeForwardPropertyName(type)}";
            //var baseName = type.FullName;
            //if (Regex.Match(type.FullName, @"`\d+<").Success)
            //{
            //    baseName = Regex.Replace(baseName, @"`\d+<", "<");
            //}
            //else
            //{
            //    // TODO: consider nested classes A.B<>.C<>
            //    baseName = $"{type.FullName.Substring(0, idx)}<{string.Join(",", type.GenericParameters.Select(gp => gp.Name))}>";
            //}
            //return $"Fake.{baseName}.{GetFakeForwardPropertyName(type)}";
        }

        string GetFakeForwardPropertyGetterImplementationNamePrefix(TypeReference type)
        {
            throw new InvalidOperationException();
            //return $"Fake.{Regex.Replace(type.FullName, @"`\d<", "<")}.{GetFakeForwardPropertyGetterNamePrefix(type)}";
        }

        bool NeedsFakeForwardPropertyDefinition(TypeDefinition type)
        {
            throw new InvalidOperationException();
            //return type.IsInterface || type.IsClass && type.IsAbstract || type.IsNestedPublic;
        }

        bool NeedsFakeForwardProperties(TypeDefinition type)
        {
            throw new InvalidOperationException();
            /*
            if (ShouldSkip(type))
                return false;

            return type.IsInterface || type.IsNestedPublic || type.IsClass && type.IsAbstract || type.Interfaces.Any(i => NeedsFakeForwardProperties(i.Resolve()));
            */
        }

        static bool NeedsTypeFieldAndForwardConstructor(TypeDefinition type)
        {
            return !type.IsInterface;
        }

        static bool NeedsFakeImplementation(TypeDefinition type)
        {
            return type.IsInterface;
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

        public static MethodReference MakeHostInstanceGeneric(this MethodReference method, AssemblyDefinition target, bool trimGenericCount = true, params TypeReference[] arguments)
        {
            var typeReference = method.DeclaringType;

            string name = typeReference.Name;
            if (trimGenericCount)
                name = typeReference.Name.Contains("`") ? typeReference.Name.Substring(0, typeReference.Name.IndexOf("`")) : typeReference.Name;

            var realTypeReference = new TypeReference(typeReference.Namespace, name, typeReference.Module, typeReference.Scope, typeReference.IsValueType);
            foreach (var gp in typeReference.GenericParameters)
                realTypeReference.GenericParameters.Add(new GenericParameter(gp.Name, realTypeReference));
            realTypeReference = realTypeReference.MakeGenericInstanceType(arguments);

            var reference = new MethodReference(method.Name, method.ReturnType, realTypeReference)
            {
                HasThis = method.HasThis,
                ExplicitThis = method.ExplicitThis,
                CallingConvention = method.CallingConvention
            };

            foreach (var parameter in method.Parameters)
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

            foreach (var gp in method.GenericParameters)
                reference.GenericParameters.Add(new GenericParameter(gp.Name, reference));

            // TODO: More method generic fun
            if (method.ReturnType is GenericParameter rgp)
            {
                if (rgp.DeclaringType != null)
                    reference.ReturnType = arguments[method.DeclaringType.GenericParameters.IndexOf(rgp)];
            }

            return reference;
        }

        public static FieldReference MakeHostInstanceGeneric(this FieldReference field, AssemblyDefinition target, bool trimGenericCount = true, params TypeReference[] arguments)
        {
            var typeReference = field.DeclaringType;

            string name = typeReference.Name;
            if (trimGenericCount)
                name = typeReference.Name.Contains("`") ? typeReference.Name.Substring(0, typeReference.Name.IndexOf("`")) : typeReference.Name;

            var realTypeReference = new TypeReference(typeReference.Namespace, name, typeReference.Module, typeReference.Scope, typeReference.IsValueType);
            foreach (var gp in typeReference.GenericParameters)
                realTypeReference.GenericParameters.Add(new GenericParameter(gp.Name, realTypeReference));
            realTypeReference = realTypeReference.MakeGenericInstanceType(arguments);

            var reference = new FieldReference(field.Name, field.FieldType, realTypeReference);

            // TODO: More method generic fun
            if (field.FieldType is GenericParameter rgp)
            {
                if (rgp.DeclaringType != null)
                    reference.FieldType = arguments[field.DeclaringType.GenericParameters.IndexOf(rgp)];
            }

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
