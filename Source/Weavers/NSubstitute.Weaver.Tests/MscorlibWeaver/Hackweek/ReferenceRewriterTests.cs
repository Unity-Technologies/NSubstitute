using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper;
using NUnit.Framework;
using Shouldly;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver.Hackweek
{
    [TestFixture]
    [Category("NG")]
    class ReferenceRewriterTests
    {
        AssemblyTypeBuilder original, target;
        ReferenceRewriter sut;

        [SetUp]
        public void SetUp()
        {
            original = new AssemblyTypeBuilder().WithAssembly("A", "ModA");
            target = new AssemblyTypeBuilder().WithAssembly("A.fake", "ModA");
            sut = new ReferenceRewriter();
        }

        [Test]
        public void BasicTypeIsRewritten()
        {
            var type = original.WithType("", "A", TypeAttributes.Class).Type;
            var typeDefinition = target.WithType("Fake", "A", TypeAttributes.Class).Type;
            
            sut.Rewrite(target, type, typeDefinition, type).ShouldBeSameType(typeDefinition);
        }

        [Test]
        public void UnrelatedTypeStaysSame()
        {
            sut = new ReferenceRewriter(tr => tr.FullName == "System.Boolean");

            var type = original.WithType("", "A", TypeAttributes.Class).Type;
            var typeDefinition = target.WithType("Fake", "A", TypeAttributes.Class).Type;
            var typeReference = original.Target.MainModule.TypeSystem.Boolean;

            sut.Rewrite(target, type, typeDefinition, typeReference).ShouldBeSameType(typeReference);
        }

        [Test]
        public void OpenGenericStaysOpen()
        {
            var type = original.WithType("", "A`1", TypeAttributes.Class).WithGenericParameters("T");
            var typeDefinition = target.WithType("Fake", "A`2", TypeAttributes.Class).WithGenericParameters("T", "__T").Type;

            var actual = sut.Rewrite(target, type, typeDefinition, type);
            actual.ShouldBeSameType(typeDefinition);

            actual.GenericParameters[0].DeclaringType.ShouldBeSameType(typeDefinition);
        }

        [Test]
        public void GenericWithUnrelatedTypeGetsRewritten()
        {
            var simpleType = original.WithType("A", "Simple", TypeAttributes.Class).Type;
            var type = original.WithType("", "A`1", TypeAttributes.Class).WithGenericParameters("T");

            var simpleTypeDefinition = target.WithType("Fake.A", "Simple", TypeAttributes.Class).Type;
            var typeDefinition = target.WithType("Fake", "A`2", TypeAttributes.Class).WithGenericParameters("T", "__T");

            var reference = type.MakeGenericInstanceType(simpleType);
            var result = typeDefinition.MakeGenericInstanceType(simpleTypeDefinition, target.Target.MainModule.Import(simpleType));

            sut.Rewrite(target, type, typeDefinition, reference).ShouldBeSameType(result);
            sut.Rewrite(target, type, typeDefinition, type.Type.GenericParameters[0]).ShouldBe(typeDefinition.Type.GenericParameters[0]);
            Should.Throw<InvalidOperationException>(() => sut.Rewrite(target, type, typeDefinition, new GenericParameter("Q", simpleType)));
        }

        [Test]
        public void GenericInGenericGetsRewritten()
        {
            var simpleType = original.WithType("A", "Simple", TypeAttributes.Class).Type;
            var type = original.WithType("", "A`1", TypeAttributes.Class).WithGenericParameters("T");

            var simpleTypeDefinition = target.WithType("Fake.A", "Simple", TypeAttributes.Class).Type;
            var typeDefinition = target.WithType("Fake", "A`2", TypeAttributes.Class).WithGenericParameters("T", "__T");

            var reference = type.MakeGenericInstanceType(type.MakeGenericInstanceType(simpleType));
            var result = typeDefinition.MakeGenericInstanceType(typeDefinition.MakeGenericInstanceType(simpleTypeDefinition, target.Target.MainModule.Import(simpleType)), target.Target.MainModule.Import(type.Type).MakeGenericType(target.Target.MainModule.Import(simpleType)));

            sut.Rewrite(target, type, typeDefinition, reference).ShouldBeSameType(result);
        }

        [Test]
        public void ReferenceFromGenericTypeToGenericInterface()
        {
            var interfaceType = original.WithType("", "IA`1").WithGenericParameters("T").Type;
            var implType = original.WithType("", "A`1").WithGenericParameters("T").Type;

            var interfaceTypeDefinition = target.WithType("Fake", "IA`2").WithGenericParameters("T", "__T").Type;
            var implTypeDefinition = target.WithType("Fake", "A`2").WithGenericParameters("T", "__T").Type;

            var implTypeReference = interfaceType.MakeGenericInstanceType(implType.GenericParameters[0]);
            var expected = interfaceTypeDefinition.MakeGenericInstanceType(implTypeDefinition.GenericParameters.ToArray());

            target.Target.MainModule.AssemblyReferences.Add(new AssemblyNameReference("A", new Version(1, 0)));

            original.Target.Write("A.dll");

            var actual = sut.Rewrite(target, implType, implTypeDefinition, implTypeReference);
            actual.ShouldBeSameType(expected);

            var actualInstance = (GenericInstanceType) actual;
            actualInstance.GenericArguments[0].DeclaringType.ShouldBeSameType(implTypeDefinition);
            actualInstance.GenericArguments[1].DeclaringType.ShouldBeSameType(implTypeDefinition);
        }

        [Test]
        public void ReferenceToInstantiateGenericTypeFromNonGenericMethod()
        {
            var interfaceType = original.WithType("", "IA`1").WithGenericParameters("T").Type;
            var simpleType = original.WithType("", "A").Type;
            var method = original.WithMethod("Foo").WithParameter("arg1", interfaceType.MakeGenericInstanceType(simpleType)).Method;

            var origReference = method.Parameters[0].ParameterType;

            var interfaceTypeDefinition = target.WithType("Fake", "IA`2").WithGenericParameters("T", "__T").Type;
            var simpleTypeDefinition = target.WithType("Fake", "A").Type;
            var methodDefinition = original.WithMethod("Foo").WithParameter("arg1",
                interfaceTypeDefinition.MakeGenericInstanceType(simpleTypeDefinition, simpleType)).Method;

            var targetReference = methodDefinition.Parameters[0].ParameterType;

            sut.Rewrite(target, simpleType, simpleTypeDefinition, origReference, method, methodDefinition).ShouldBeSameType(targetReference);
        }

        [Test]
        public void NonFakedGenericParameterMustBeSafe()
        {
            var interfaceType = original.WithType("", "IA`1").WithGenericParameters("T").Type;
            var nonGenericType = original.WithType("", "A").Type;
            nonGenericType.Interfaces.Add(interfaceType.MakeGenericInstanceType(original.Target.MainModule.TypeSystem.Int32));

            var selfFakeHolder = target.WithType("Fake", "__SelfFakeHolder`1").WithGenericParameters("T").Type;
            var interfaceTypeDefinition = target.WithType("Fake", "IA`2").WithGenericParameters("T", "__T").Type;
            var nonGenericTypeDefinition = target.WithType("Fake", "A").Type;
            nonGenericTypeDefinition.Interfaces.Add(interfaceTypeDefinition.MakeGenericInstanceType(
                selfFakeHolder.MakeGenericInstanceType(target.Target.MainModule.TypeSystem.Int32),
                target.Target.MainModule.TypeSystem.Int32));

            var sut = new ReferenceRewriter(r => r.Name == "Int32");
            var result = sut.Rewrite(target, nonGenericType, nonGenericTypeDefinition, nonGenericType.Interfaces[0],
                null, null, null, true, selfFakeHolder);

            result.ShouldBeSameType(nonGenericTypeDefinition.Interfaces[0]);
        }

        [Test]
        [Ignore("Needs multi generic parameter impplementation before working")]
        public void ReferenceToGenericTypeFromGenericMethod()
        {
            var interfaceType = original.WithType("", "IA`1").WithGenericParameters("T").Type;
            var simpleType = original.WithType("", "A").Type;
            var method = original.WithMethod("Foo").WithMethodGenericParameters("T").WithParameter("arg1", interfaceType.MakeGenericInstanceType(original.Method.GenericParameters[0])).Method;

            var origReference = method.Parameters[0].ParameterType;

            var interfaceTypeDefinition = target.WithType("Fake", "IA`2").WithGenericParameters("T", "__T").Type;
            var simpleTypeDefinition = target.WithType("Fake", "A").Type;
            var methodDefinition = original.WithMethod("Foo").WithMethodGenericParameters("T", "__T").WithParameter("arg1",
                interfaceTypeDefinition.MakeGenericInstanceType(original.Method.GenericParameters.ToArray())).Method;

            var targetReference = methodDefinition.Parameters[0].ParameterType;

            sut.Rewrite(target, simpleType, simpleTypeDefinition, origReference, method, methodDefinition, new Dictionary<MethodDefinition, MethodDefinition>{{method, methodDefinition}}).ShouldBeSameType(targetReference);
        }

        [Test]
        public void MethodGenericReferenceGetsRewritten()
        {
            var simpleType = original.WithType("A", "Simple", TypeAttributes.Class).Type;
            var type = original.WithType("", "A", TypeAttributes.Class);

            var typeDefinition = target.WithType("Fake", "A", TypeAttributes.Class);
            typeDefinition.WithMethod(".ctor");

            var method = type.WithMethod("Method", MethodAttributes.Public).WithMethodGenericParameters("T");
            method.WithReturnType(method.Method.GenericParameters[0]);
            var methodDefinition = typeDefinition.WithMethod("Method", MethodAttributes.Public).WithMethodGenericParameters("T");
            methodDefinition.WithReturnType(methodDefinition.Method.GenericParameters[0]);

            var methodMap = new Dictionary<MethodDefinition, MethodDefinition> { { method, methodDefinition } };

            sut.Rewrite(target, type, typeDefinition, method.Method.ReturnType, method, methodDefinition, methodMap).ShouldBe(methodDefinition.Method.ReturnType);
            Should.Throw<InvalidOperationException>(() => sut.Rewrite(target, type, typeDefinition, new GenericParameter("Q", simpleType), method, methodDefinition));
        }

        [Test]
        public void SimpleMethodReferenceIsTranslated()
        {
            original.WithType("", "A").WithMethod("Method");
            target.WithType("Fake", "A").WithMethod("Method");

            sut.Reinstantiate(original, original, target, original.Method, target, original, null).ShouldBe(original.Method);
        }

        [Test]
        public void SimpleMethodParameterAndReturnTypeGetsTranslated()
        {
            original.WithType("", "A").WithMethod("Method", MethodAttributes.Public).WithReturnType(original).WithParameter("arg", original);
            target.WithType("Fake", "A").WithMethod("Method", MethodAttributes.Public).WithReturnType(target).WithParameter("arg", target);

            var actual = sut.Reinstantiate(original, original, target, original.Method, target, original, null);
            
            actual.ShouldBeSameMethod(original.Method);
        }

        [Test]
        public void SimpleMethodInGenericType()
        {
            var type = original.WithType("", "A`1").WithGenericParameters("T").Type;
            var instantiatedType = type.MakeGenericInstanceType(type.GenericParameters[0]);
            original.WithMethod("Method").WithReturnType(instantiatedType).WithParameter("arg", instantiatedType);

            var typeDefinition = target.WithType("Fake", "A`1").WithGenericParameters("T").Type;
            var instantiatedTypeDefinition = typeDefinition.MakeGenericInstanceType(typeDefinition.GenericParameters[0]);
            target.WithMethod("Method").WithReturnType(instantiatedTypeDefinition).WithParameter("arg", instantiatedTypeDefinition);

            var reference = original;

            sut.Reinstantiate(target, type, typeDefinition, original.Method, target, reference, null).ShouldBeSameMethod(original.Method);
        }

        [Test]
        public void GenericMethodRewrite()
        {
            var type = original.WithType("", "A");
            var method = original.WithMethod("Method").WithMethodGenericParameters("T").Method;
            original.WithReturnType(method.GenericParameters[0]).WithParameter("arg", method.GenericParameters[0]);

            var typeDefinition = target.WithType("", "A");
            var methodDefinition = target.WithMethod("Method").WithMethodGenericParameters("T").Method;
            target.WithReturnType(methodDefinition.GenericParameters[0]).WithParameter("arg", methodDefinition.GenericParameters[0]);

            var reference = new GenericInstanceMethod(method);
            reference.GenericArguments.Add(type);

            var actual = sut.Reinstantiate(target, type, typeDefinition, original.Method, target, reference, null);

            var expected = new GenericInstanceMethod(method);
            expected.GenericArguments.Add(typeDefinition);

            actual.ShouldBeSameMethod(expected);
        }

        [Test]
        public void TypeReferenceNull()
        {
            sut.Rewrite(null, null, null, null).ShouldBe(null);
        }

        [Test]
        public void GenericTypeReferenceArray()
        {
            var type = original.WithType("", "A");
            var method = original.WithMethod("Foo").WithMethodGenericParameters("T").Method;
            original.WithParameter("arg", method.GenericParameters[0].MakeArrayType());

            var typeDefinition = target.WithType("Fake", "A");
            target.WithMethod(".ctor");
            var methodDefinition = target.WithMethod("Foo").WithMethodGenericParameters("T").Method;
            target.WithParameter("arg", methodDefinition.GenericParameters[0].MakeArrayType());

            var methodMap = new Dictionary<MethodDefinition, MethodDefinition> { { method, methodDefinition } };

            var actual = sut.Rewrite(target, type, typeDefinition, method.GenericParameters[0].MakeArrayType(), method, methodDefinition, methodMap);

            actual.FullName.ShouldBe("T[]");
            var gp = (GenericParameter)actual.GetElementType();
            gp.DeclaringMethod.ShouldBeSameMethod(methodDefinition);
        }

        [Test]
        public void GenericTypeReferenceByRef()
        {
            var type = original.WithType("", "A");
            var method = original.WithMethod("Foo").WithMethodGenericParameters("T").Method;
            original.WithParameter("arg", method.GenericParameters[0].MakeByReferenceType());

            var typeDefinition = target.WithType("Fake", "A");
            target.WithMethod(".ctor");
            var methodDefinition = target.WithMethod("Foo").WithMethodGenericParameters("T").Method;
            target.WithParameter("arg", methodDefinition.GenericParameters[0].MakeByReferenceType());

            var methodMap = new Dictionary<MethodDefinition, MethodDefinition> { { method, methodDefinition } };

            var actual = sut.Rewrite(target, type, typeDefinition, method.GenericParameters[0].MakeByReferenceType(), method, methodDefinition, methodMap);

            actual.FullName.ShouldBe("T&");
            var gp = (GenericParameter)actual.GetElementType();
            gp.DeclaringMethod.ShouldBeSameMethod(methodDefinition);
        }

        public class AssemblyTypeBuilder
        {
            AssemblyDefinition target;
            TypeDefinition activeType;
            MethodDefinition activeMethod;

            public AssemblyTypeBuilder WithAssembly(string name, string moduleName)
            {
                target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), moduleName, ModuleKind.Dll);
                return this;
            }

            public AssemblyTypeBuilder WithType(string @namespace, string name, TypeAttributes typeAttributes = TypeAttributes.Class)
            {
                activeType = new TypeDefinition(@namespace, name, typeAttributes);
                target.MainModule.Types.Add(activeType);
                return this;
            }

            public AssemblyTypeBuilder WithGenericParameters(params string[] names)
            {
                foreach (var name in names)
                    activeType.GenericParameters.Add(new GenericParameter(name, activeType));
                return this;
            }

            public GenericInstanceType MakeGenericInstanceType(params TypeReference[] references)
            {
                return activeType.MakeGenericInstanceType(references);
            }

            public static implicit operator AssemblyDefinition(AssemblyTypeBuilder builder)
            {
                return builder.Target;
            }

            public static implicit operator TypeDefinition(AssemblyTypeBuilder builder) => builder.activeType;
            public static implicit operator MethodDefinition(AssemblyTypeBuilder builder) => builder.activeMethod;

            public AssemblyDefinition Target => target;
            public TypeDefinition Type => activeType;
            public MethodDefinition Method => activeMethod;

            public AssemblyTypeBuilder WithMethod(string name, MethodAttributes attributes = MethodAttributes.Public)
            {
                activeMethod = new MethodDefinition(name, attributes, target.MainModule.TypeSystem.Void);
                activeType.Methods.Add(activeMethod);
                return this;
            }

            public AssemblyTypeBuilder WithMethodGenericParameters(params string[] names)
            {
                foreach (var name in names)
                    activeMethod.GenericParameters.Add(new GenericParameter(name, activeMethod));
                return this;
            }

            public AssemblyTypeBuilder WithReturnType(TypeReference type)
            {
                activeMethod.ReturnType = type;
                return this;
            }

            public AssemblyTypeBuilder WithParameter(string name, TypeReference parameterType)
            {
                activeMethod.Parameters.Add(new ParameterDefinition(name, ParameterAttributes.None, parameterType));
                return this;
            }
        }
    }
}
