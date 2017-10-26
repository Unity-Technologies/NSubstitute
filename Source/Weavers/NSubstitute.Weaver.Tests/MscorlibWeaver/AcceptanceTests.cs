#define PEVERIFY

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper;
using NUnit.Framework;
using Shouldly;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver
{
    [TestFixture]
    class AcceptanceTests
    {
        const string pathPrefix = ".";
        //TODO: Add a test Generic type with non generic nested type (NG test).

        [Test]
        public void Copy_NoSkippedTypes_PatchesAllTypesFromOriginalAssembly()
        {
            var mscorlibAssembly = AssemblyDefinition.ReadAssembly(typeof(void).Assembly.Location);
            var nsubstituteAssembly = AssemblyDefinition.ReadAssembly(typeof(Substitute).Assembly.Location);
            var patchedAssembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(
                "mscorlib.fake", mscorlibAssembly.Name.Version),
                mscorlibAssembly.MainModule.Name,
                ModuleKind.Dll);

            var copier = new Copier(new ProcessTypeResolver(mscorlibAssembly));
            copier.Copy(mscorlibAssembly, ref patchedAssembly, nsubstituteAssembly,
                mscorlibAssembly.MainModule.Types.Select(t => t.FullName).ToArray());

            var allOriginalTypes = mscorlibAssembly.MainModule.Types.Select(t => t.Name);
            var allPatchedTypes = patchedAssembly.MainModule.Types.Select(t => t.Name);
            allOriginalTypes.ShouldBe(allPatchedTypes, ignoreOrder: true);

            patchedAssembly.Write(Path.Combine(pathPrefix, "fake.mscorlib.dll"));
        }

        static string GetMethodName([CallerMemberName] string method = null)
        {
            return method;
        }

        [Test]
        [Category("NG")]
        public void MethodWillReturnPatchedInnerType()
        {
            const string code = @"public class A { public class B { public int X; } public B Foo(B b) { return b; } }";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A");
            patchedTypeA.NestedTypes.ShouldContainOnly(1);

            var fooMethod = patchedTypeA.Methods.Single(m => m.Name == "Foo");
            fooMethod.Body.Instructions.Select(i => i.ToString()).ShouldBe(new[]
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld A Fake.A::__fake_forward",
                "IL_0006: ldarg b",
                "IL_000a: callvirt T Fake.__FakeHolder`1<A/B>::get_Forward()",
                "IL_000f: callvirt A/B A::Foo(A/B)",
                "IL_0014: newobj System.Void Fake.A/B::.ctor(A/B)",
                "IL_0019: ret"
            });
        }

        [Test]
        [Category("NG")]
        public void PrivateInterfaceImplementation_HasOverride()
        {
            var code = @"public interface IA { void Foo(); } 
                         public interface IB { int Foo(); } 
                         public class A : IA, IB 
                         { 
                             public int Foo() { return 0; } 
                             void IA.Foo() {} 
                         }";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A");
            var allFooMethods = patchedTypeA.Methods.Where(m => m.Name.Contains("Foo"));

            var expectedMethods = new[]
            {
                "System.Void Fake.A::Fake.IA.Foo()",
                "System.Int32 Fake.A::Foo()"
            };
            allFooMethods.ShouldHaveMembers(expectedMethods);

            var fooMethodWithOverride = allFooMethods.Single(m => m.FullName == "System.Void Fake.A::Fake.IA.Foo()");
            fooMethodWithOverride.Overrides.ShouldHaveMembers(new[] { "System.Void Fake.IA::Foo()" });

            fooMethodWithOverride.ShouldContainVirtualMethodCall("System.Void IA::Foo()");
        }

        [Test]
        [Category("NG")]
        public void PatchedTypeOf_SimplePublicClass_ImplementsFakeHolderInterface()
        {
            var code = @"public class A {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A");
            patchedTypeA.ShouldNotBeNull();

            patchedTypeA.Interfaces.ShouldHaveMembers(new[] { "Fake.__FakeHolder`1<A>" });
        }

        [Test]
        [Category("NG")]
        public void PatchedTypeOf_SimplePublicClass_HasFakeFieldAndCtor()
        {
            var code = @"public class A {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A");
            patchedTypeA.ShouldNotBeNull();

            patchedTypeA.BaseType.ShouldBeSameType(patchedAssembly.MainModule.TypeSystem.Object);
            patchedTypeA.Fields.ShouldContainOnly(1);

            var originalTypeA = originalAssembly.MainModule.GetType("A");
            patchedTypeA.ShouldHaveForwardField(originalTypeA);
            patchedTypeA.ShouldHaveForwardConstructor(originalTypeA);
        }

        [Test]
        [Category("NG")]
        public void PatchedTypeOf_GenericType_ImplementsFakeHolderInterface()
        {
            var code = @"public interface IA<T> {} 
                         public class A<T> : IA<T> {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A`2");
            patchedTypeA.ShouldNotBeNull();

            patchedTypeA.Interfaces.ShouldHaveMembers(new[]
            {
                "Fake.__FakeHolder`1<A`1<__T>>",
                "Fake.IA`2<T,__T>",
                "Fake.__FakeHolder`1<IA`1<__T>>"
            });
        }

        [Test]
        [Category("NG")]
        [Ignore("Fails depressingly")]
        public void PrivateInterfaceImplementationNotWithGenericsTest()
        {
            CreateAssemblyFromCode(@"public interface IA<T> { T Foo<U>(U u); } public interface IB<T> { int Foo<V>(); } public class A<T> : IA<float>, IA<IA<int>>, IB<T> { public int Foo<V>() { return 0; } float IA<float>.Foo<U>(U u) { return 0; } IA<int> IA<IA<int>>.Foo<U>(U u) { return null; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A`1");
            var methods = a.Methods.Where(m => m.Name.Contains("Foo")).ToList();

            var expectedMethod = "System.Single Fake.A`1::Fake.IA<System.Single>.Foo(U)";
            Assert.That(methods.Select(m => m.FullName), Contains.Item(expectedMethod));
            var expectedMethod2 = "Fake.IA`1<System.Int32> Fake.A`1::Fake.IA<Fake.IA<System.Int32>>.Foo(U)";
            Assert.That(methods.Select(m => m.FullName), Contains.Item(expectedMethod2));

            var method = methods.Single(m => m.FullName == expectedMethod);
            Assert.That(method.Overrides, Has.Count.EqualTo(1));
            Assert.That(method.Overrides[0].FullName, Is.EqualTo("System.Void Fake.IA::Foo()"));
        }

        [Test]
        [Category("NG")]
        public void PatchedTypeOf_SimplePublicClassWithGenerics_HasFakeFieldAndCtor()
        {
            var code = @"public class A<T, U> {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A`4");
            patchedTypeA.ShouldNotBeNull();
            patchedTypeA.GenericParameters.ShouldHaveMembers(new[] { "T", "U", "__T", "__U" });

            var originalType = originalAssembly.MainModule.GetType("A`2");
            var genericParams = patchedTypeA.GenericParameters.Cast<TypeReference>().Skip(2).ToArray();
            var instanceType = originalType.MakeGenericInstanceType(genericParams);
            patchedTypeA.ShouldHaveForwardField(instanceType);
            patchedTypeA.ShouldHaveForwardConstructor(instanceType);
        }

        [Test]
        [Category("NG")]
        public void PatchedTypeOf_PublicClassWithGenericTypeConstraints_HasConstraints()
        {
            var code = @"public class A<U> where U : System.Collections.Generic.List<U> {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A`2");
            patchedTypeA.ShouldNotBeNull();

            patchedTypeA.GenericParameters.ShouldHaveMembers(new[] { "U", "__U" });
            patchedTypeA.GenericParameters[0].Constraints.ShouldHaveMembers(
                new[] 
                {
                    "System.Collections.Generic.List`1<U>",
                    "Fake.__FakeHolder`1<__U>"
                });
        }

        [TestCase("public enum A {}", TestName = "PublicEnum_IsNotProcessed")]
        [TestCase("class A {}", TestName = "PrivateClass_IsNotProcessed")]
        [Category("NG")]
        public void TypesThatAreNotProcessed(string code)
        {
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            patchedAssembly.MainModule.Types.ShouldNotContain(t => t.Name == "A");
        }

        [TestCase("public class A {  protected void Foo() { } }", TestName = "ProtectedMethod_IsNotProcessed")]
        [TestCase("public class A {  internal void Foo() { } }", TestName = "InternalMethod_IsNotProcessed")]
        [TestCase("public class A {  private void Foo() { } }", TestName = "PrivateMethod_IsNotProcessed")]
        [TestCase("public class A {  void Foo() { } }", TestName = "MethodWithNoExplicitModifier_IsNotProcessed")]
        [Category("NG")]
        public void MethodsThatAreNotProcessed(string code)
        {
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);
          
            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A");
            patchedTypeA.Methods.ShouldNotContain(m => m.Name == "Foo");
        }

        [Test]
        [Category("NG")]
        public void PatchedTypeOf_PublicClassWithGenericTypeConstraintInTypeConstraint_HasConstraints()
        {
            var code = @"public class A<U> where U : System.Collections.Generic.List<System.Collections.Generic.List<U>> {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A`2");
            patchedTypeA.ShouldNotBeNull();

            patchedTypeA.GenericParameters.ShouldHaveMembers(new[] { "U", "__U" });
            patchedTypeA.GenericParameters[0].Constraints.ShouldHaveMembers(
                new[]
                {
                    "System.Collections.Generic.List`1<System.Collections.Generic.List`1<U>>",
                    "Fake.__FakeHolder`1<__U>"
                });
        }

        [Test]
        [Category("NG")]
        public void PatchedMethod_ParameterMarkedWithMarshalAs_HasFieldMarshalSetToFalse()
        {
            var code =
                @"public class A { public void Foo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string x) {} }";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeA = patchedAssembly.MainModule.GetType("Fake.A");
            var fooMethod = patchedTypeA.Methods.Single(m => m.Name == "Foo");
            
            var fooParameter = fooMethod.Parameters.Single();
            fooParameter.HasFieldMarshal.ShouldBeFalse();
        }

        [Test]
        [Category("NG")]
        public void Translated_SimpleInterface_HasNoCtorAndFields()
        {
            var code = @"public interface IA {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedInterfaceAI = patchedAssembly.MainModule.GetType("Fake.IA");
            patchedInterfaceAI.ShouldNotBeNull();
            
            patchedInterfaceAI.GetConstructors().ShouldBeEmpty();
            patchedInterfaceAI.Fields.ShouldBeEmpty();
        }

        [Test]
        [Category("NG")]
        public void SimpleInterfaceImplementingInterfaceAreBothTranslatedAndImplementsPreserved()
        {
            var code = @"public interface IA {} public interface IB : IA {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);
            
            var patchedParentInterface = patchedAssembly.MainModule.GetType("Fake.IA");
            patchedParentInterface.ShouldNotBeNull();
            var patchedChildInterface = patchedAssembly.MainModule.GetType("Fake.IB");
            patchedChildInterface.ShouldNotBeNull();
            
            patchedChildInterface.Interfaces.ShouldContainOnly(2);
            patchedChildInterface.Interfaces[1].ShouldBeSameType(patchedParentInterface);
        }

        [Test]
        [Category("NG")]
        public void SimpleInterfaceImplementingGenericInterfaceBothTranslatedAndImplementsPreserved()
        {
            var code = @"public interface IA<T> {} public interface IB : IA<int> {}";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeIA = patchedAssembly.MainModule.GetType("Fake.IA`2");
            patchedTypeIA.ShouldNotBeNull();
            var patchedTypeIB = patchedAssembly.MainModule.GetType("Fake.IB");
            patchedTypeIB.ShouldNotBeNull();

            patchedTypeIB.Interfaces.ShouldHaveMembers(new []
            {
                "Fake.__FakeHolder`1<IB>",
                "Fake.IA`2<Fake.SelfFakeHolder`1<System.Int32>,System.Int32>"
            });

            var selfFakeHolder = patchedAssembly.MainModule.GetType("Fake.SelfFakeHolder`1");
            var originalInt32 = originalAssembly.MainModule.TypeSystem.Int32;
            patchedTypeIB.Interfaces[1].ShouldBeSameType(patchedTypeIA.MakeGenericInstanceType(
                selfFakeHolder.MakeGenericInstanceType(originalInt32), originalInt32));
        }

        [Test]
        public void SimpleInterfaceWithMethodIsTranslated()
        {
            var code = @"public interface IA { int Foo(IA x); }";
            var originalAssembly = CreateAssembly(code);
            var patchedAssembly = PatchAssembly(originalAssembly);

            var patchedTypeIA = patchedAssembly.MainModule.GetType("Fake.IA");
            patchedTypeIA.ShouldNotBeNull();
            patchedTypeIA.Methods.ShouldHaveMembers(new[]
            {
                "System.Int32 Fake.IA::Foo(Fake.IA)",
                "get__FakeForwardProp_IA"
            });

            var fooMethod = patchedTypeIA.Methods.Single(m => m.Name == "Foo");
            fooMethod.Parameters.ShouldHaveSingleItem();
            fooMethod.Parameters[0].Name.ShouldBe("x");
            fooMethod.Parameters[0].ParameterType.ShouldBeSameType(patchedTypeIA);
        }

        [TestCase(
            "public class A { public void Foo() {} }",
            "A", "Foo", 0, "RV_Void_Param_",
            "IL_0000: ldarg.0",
            "IL_0001: ldfld A Fake.A::__fake_forward",
            "IL_0006: callvirt System.Void A::Foo()",
            "IL_000b: ret")]
        [TestCase(
            "public class A { public static void Foo() {} }",
            "A", "Foo", 0, "S_RV_Void_Param_",
            "IL_0000: call System.Void A::Foo()",
            "IL_0005: ret")]
        [TestCase(
            "public class A { public void Foo(int x) {} }",
            "A", "Foo", 1, "RV_Void_Param_int",
            "IL_0000: ldarg.0",
            "IL_0001: ldfld A Fake.A::__fake_forward",
            "IL_0006: ldarg x",
            "IL_000a: callvirt System.Void A::Foo(System.Int32)",
            "IL_000f: ret")]
        [TestCase(
            "public class A {} public class B { public A Foo() { return new A(); } }",
            "B", "Foo", 0, "RV_A_Param_",
            "IL_0000: ldarg.0",
            "IL_0001: ldfld B Fake.B::__fake_forward",
            "IL_0006: callvirt A B::Foo()",
            "IL_000b: newobj System.Void Fake.A::.ctor(A)",
            "IL_0010: ret")]
        [TestCase(
            "public class A { public A Foo<T>(T x) { return null; } }",
            "A", "Foo", 1, "RV_A_Param_OfT",
            "IL_0000: ldarg.0",
            "IL_0001: ldfld A Fake.A::__fake_forward",
            "IL_0006: ldarg x",
            "IL_000a: callvirt A A::Foo<T>(T)",
            "IL_000f: newobj System.Void Fake.A::.ctor(A)",
            "IL_0014: ret")]
        [Category("NG")]
        public void WrapMethod(string program, string typeName, string methodName, int parameterCount, string suffix, params string[] result)
        {
            CreateAssemblyFromCode(program, out AssemblyDefinition target, out AssemblyDefinition mscorlib, GetMethodName() + suffix);

            target.MainModule.Types.ShouldContain(t => t.FullName == "Fake." + typeName);

            var targetType = target.MainModule.Types.Single(t => t.FullName == "Fake." + typeName);
            targetType.Methods.ShouldContain(m => m.Name == methodName);

            var fooMethod = targetType.Methods.Single(m => m.Name == methodName);
            fooMethod.Parameters.Count.ShouldBe(parameterCount);
            fooMethod.Body.Instructions.InstructionsToString().ShouldBe(result);
        }

        [Test]
        [Category("NG")]
        public void AbstractClassGeneratesWrapperClass()
        {
            CreateAssemblyFromCode("public abstract class B { public abstract B Baz(); public B Bar(B b) { return b; } } public abstract class A : B { public virtual A Foo(A a, int b) { return a; } }", out AssemblyDefinition patched, out AssemblyDefinition original);

            var b = patched.MainModule.Types.Single(t => t.FullName == "Fake.B");

            var bar = b.Methods.Single(m => m.Name == "Bar");
            bar.ReturnType.ShouldBeSameType(b);
            bar.IsAbstract.ShouldBeFalse();

            var callvirt = bar.Body.Instructions.First(i => i.OpCode == OpCodes.Callvirt);
            callvirt.Operand.ShouldBeOfType<MethodReference>();

            var method = (MethodReference)callvirt.Operand;
            method.FullName.ShouldBe("T Fake.__FakeHolder`1<B>::get_Forward()");
        }

        [Test]
        [Category("NG")]
        public void TestForwardConstructorIsValid()
        {
            CreateAssemblyFromCode("public class A {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var type = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            var ctor = type.Methods.Single(m => m.Name == ".ctor");

            ctor.Body.Instructions.Select(i => i.ToString()).ShouldBe(new []
            {
                "IL_0000: ldarg.0",
                "IL_0001: call System.Void System.Object::.ctor()",
                "IL_0006: ldarg.0",
                "IL_0007: ldarg.1",
                "IL_0008: stfld A Fake.A::__fake_forward",
                "IL_000d: ret"
            });

            var forward = type.Methods.Single(m => m.Name == "Fake.__FakeHolder<global::A>.get_Forward");

            forward.Body.Instructions.Select(i => i.ToString()).ShouldBe(new []
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld A Fake.A::__fake_forward",
                "IL_0006: ret"
            });

            forward.Overrides.Single().FullName.ShouldBe("T Fake.__FakeHolder`1<A>::get_Forward()");

            var forwardProperty = type.Properties.Single(m => m.Name == "Fake.__FakeHolder<global::A>.Forward");
            forwardProperty.FullName.ShouldBe("A Fake.A::Fake.__FakeHolder<global::A>.Forward()");
            forwardProperty.GetMethod.ShouldBe(forward);
        }

        [Test]
        [Category("NG")]
        public void IEquatableType()
        {
            CreateAssemblyFromCode("public interface IEquatable<T> { bool Equals(T other); } public sealed class TimeZoneInfo : IEquatable<TimeZoneInfo> { public bool Equals(TimeZoneInfo other) { return false; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var fakeImpl = target.MainModule.Types.Single(t => t.FullName == "Fake._FakeImpl_IEquatable`2");
            var equals = fakeImpl.Methods.Single(m => m.Name == "Equals");
            equals.Parameters[0].ParameterType.ShouldBeSameType(fakeImpl.GenericParameters[0]);

            equals.Body.Instructions.Select(i => i.ToString()).ShouldBe(new[]
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld IEquatable`1<__T> Fake._FakeImpl_IEquatable`2<T,__T>::__fake_forward",
                "IL_0006: ldarga.s other",
                "IL_0008: constrained. T",
                "IL_000e: callvirt T Fake.__FakeHolder`1<__T>::get_Forward()",
                "IL_0013: callvirt System.Boolean IEquatable`1<__T>::Equals(T)",
                "IL_0018: ret"
            });

            var timeZoneInfo = target.MainModule.Types.Single(t => t.FullName == "Fake.TimeZoneInfo");
            var tzEquals = timeZoneInfo.Methods.Single(m => m.Name == "Equals");
            tzEquals.Body.Instructions.Select(i => i.ToString()).ShouldBe(new[]
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld TimeZoneInfo Fake.TimeZoneInfo::__fake_forward",
                "IL_0006: ldarg other",
                "IL_000a: callvirt T Fake.__FakeHolder`1<TimeZoneInfo>::get_Forward()",
                "IL_000f: callvirt System.Boolean TimeZoneInfo::Equals(TimeZoneInfo)",
                "IL_0014: ret"
            });
        }

        [Test]
        [Category("NG")]
        public void PInvokeGetsRewritten()
        {
            CreateAssemblyFromCode("using System.Runtime.InteropServices; public class A { [DllImport(\"kernel32.dll\")] public static extern void DebugBreakProcess(uint dwProcessHandle); }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            var debugBreak = a.Methods.Single(m => m.Name == "DebugBreakProcess");

            debugBreak.Body.Instructions.ShouldNotBeEmpty();
            debugBreak.HasBody.ShouldBeTrue();
        }

        [Test]
        [Category("NG")]
        public void SpecializedInterfaceGeneratesForwardProperties()
        {
            CreateAssemblyFromCode("public interface IA<T> {} public interface IB<T> : IA<T> {} public class B : IB<int> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var b = target.MainModule.Types.Single(t => t.FullName == "Fake.B");
            b.Properties.Select(p => p.Name).ShouldBe(new[] { "Fake.__FakeHolder<global::B>.Forward", "Fake.__FakeHolder<global::IB`1<System.Int32>>.Forward", "Fake.__FakeHolder<global::IA`1<System.Int32>>.Forward" });
        }

        [Test]
        [Category("NG")]
        public void ReferenceFromGenericTypeToGenericInterface()
        {
            CreateAssemblyFromCode("public interface IA<T> {} public class A<T> : IA<T> {}", out AssemblyDefinition target, out AssemblyDefinition original);

            var interfaceType = original.MainModule.Types.Single(t => t.FullName == "IA`1");
            var implType = original.MainModule.Types.Single(t => t.FullName == "A`1");

            var interfaceTypeDefinition = target.MainModule.Types.Single(t => t.FullName == "Fake.IA`2");
            var implTypeDefinition = target.MainModule.Types.Single(t => t.FullName == "Fake.A`2");

            var implTypeReference = interfaceType.MakeGenericInstanceType(implType.GenericParameters[0]);
            var expected = interfaceTypeDefinition.MakeGenericInstanceType(implTypeDefinition.GenericParameters.ToArray());

            var sut = new ReferenceRewriter();
            sut.Rewrite(target, implType, implTypeDefinition, implTypeReference).ShouldBeSameType(expected);
        }

        [Test]
        [Category("NG")]
        public void InterfaceReturningTypeParameter()
        {
            CreateAssemblyFromCode("public interface IA<T> { T Foo(T other); } public class A<T> : IA<T> { public T Foo(T other) { return other; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.Name == "A`2");
            var foo = a.Methods.Single(m => m.Name == "Foo");

            foo.Body.Instructions.Select(i => i.ToString()).ShouldBe(new[]
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld A`1<__T> Fake.A`2<T,__T>::__fake_forward",
                "IL_0006: ldarga.s other",
                "IL_0008: constrained. T",
                "IL_000e: callvirt T Fake.__FakeHolder`1<__T>::get_Forward()",
                "IL_0013: callvirt T A`1<__T>::Foo(T)",
                "IL_0018: stloc.0",
                "IL_0019: ldtoken T",
                "IL_001e: call System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)",
                "IL_0023: ldc.i4.1",
                "IL_0024: newarr System.Object",
                "IL_0029: dup",
                "IL_002a: ldc.i4.0",
                "IL_002b: ldc.i4.1",
                "IL_002c: newarr __T",
                "IL_0031: dup",
                "IL_0032: ldc.i4.0",
                "IL_0033: ldloc.0",
                "IL_0034: stelem.any __T",
                "IL_0039: stelem.ref",
                "IL_003a: call System.Object System.Activator::CreateInstance(System.Type,System.Object[])",
                "IL_003f: unbox.any T",
                "IL_0044: ret",
            });
        }

        [Test]
        [Category("NG")]
        public void MultipleInterfaceImplementationsGenerateSeparateForwardProperties()
        {
            CreateAssemblyFromCode("public interface IA<T> {} public class A : IA<int>, IA<float> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            a.Interfaces.Select(i => i.FullName).ShouldBe(new[]
                {
                    "Fake.__FakeHolder`1<A>",
                    "Fake.IA`2<Fake.SelfFakeHolder`1<System.Int32>,System.Int32>",
                    "Fake.IA`2<Fake.SelfFakeHolder`1<System.Single>,System.Single>",
                    "Fake.__FakeHolder`1<IA`1<System.Int32>>",
                    "Fake.__FakeHolder`1<IA`1<System.Single>>"
                }
            );
            a.Properties.Select(p => p.Name).ShouldBe(new []{"Fake.__FakeHolder<global::A>.Forward", "Fake.__FakeHolder<global::IA`1<System.Int32>>.Forward", "Fake.__FakeHolder<global::IA`1<System.Single>>.Forward"});

            var fakeImplType = target.MainModule.Types.Single(t => t.FullName == "Fake._FakeImpl_IA`2");
            var prop = fakeImplType.Properties.Single(p => p.Name == "Fake.__FakeHolder<global::IA`1<__T>>.Forward");
            prop.PropertyType.ShouldBeOfType<GenericInstanceType>();
        }

        [Test]
        [Category("NG")]
        public void GetAllInterfacesTest()
        {
            var ad = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("foo", new Version(1, 0)), "MODULE", ModuleKind.Dll);

            var fakeHolder = new TypeDefinition("Fake", "__FakeHolder`1", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            fakeHolder.GenericParameters.Add(new GenericParameter("T", fakeHolder));
            ad.MainModule.Types.Add(fakeHolder);

            var nonGenericInterface = new TypeDefinition("Fake", "IA", TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public);
            nonGenericInterface.Interfaces.Add(fakeHolder.MakeGenericType(nonGenericInterface));
            ad.MainModule.Types.Add(nonGenericInterface);

            var genericInterface = new TypeDefinition("Fake", "IB`1", TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public);
            genericInterface.GenericParameters.Add(new GenericParameter("U", genericInterface));
            genericInterface.Interfaces.Add(fakeHolder.MakeGenericType(genericInterface.MakeGenericType(genericInterface.GenericParameters[0])));
            ad.MainModule.Types.Add(genericInterface);

            var nonGenericClass = new TypeDefinition("Fake", "A", TypeAttributes.Class | TypeAttributes.Public);
            nonGenericClass.Interfaces.Add(fakeHolder.MakeGenericInstanceType(nonGenericClass));
            nonGenericClass.Interfaces.Add(nonGenericInterface);
            ad.MainModule.Types.Add(nonGenericClass);

            var genericClass = new TypeDefinition("Fake", "B`1", TypeAttributes.Class | TypeAttributes.Public);
            genericClass.GenericParameters.Add(new GenericParameter("T", genericClass));
            genericClass.Interfaces.Add(fakeHolder.MakeGenericInstanceType(genericClass.MakeGenericInstanceType(genericClass.GenericParameters[0])));
            genericClass.Interfaces.Add(genericInterface.MakeGenericInstanceType(genericClass.GenericParameters[0]));
            ad.MainModule.Types.Add(genericClass);

            var genericInterface2 = new TypeDefinition("Fake", "IC`1", TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public);
            genericInterface2.GenericParameters.Add(new GenericParameter("T", genericInterface2));
            genericInterface2.Interfaces.Add(genericInterface.MakeGenericType(genericInterface2.GenericParameters[0]));
            genericInterface2.Interfaces.Add(fakeHolder.MakeGenericType(genericInterface2.MakeGenericType(genericInterface2.GenericParameters[0])));
            ad.MainModule.Types.Add(genericInterface2);

            var nonGenericClass2 = new TypeDefinition("Fake", "C", TypeAttributes.Public | TypeAttributes.Class);
            nonGenericClass2.Interfaces.Add(genericInterface2.MakeGenericType(ad.MainModule.TypeSystem.Int32));
            nonGenericClass2.Interfaces.Add(fakeHolder.MakeGenericType(nonGenericClass2));
            ad.MainModule.Types.Add(nonGenericClass2);

            Copier.GetAllInterfaces(ad, fakeHolder).ShouldBeEmpty();

            var ifaces = Copier.GetAllInterfaces(ad, nonGenericInterface).ToList();
            ifaces.Count.ShouldBe(1);
            ifaces[0].ShouldBeSameType(fakeHolder.MakeGenericType(nonGenericInterface));

            ifaces = Copier.GetAllInterfaces(ad, genericInterface).ToList();
            ifaces.Count.ShouldBe(1);
            ifaces[0].ShouldBeSameType(fakeHolder.MakeGenericType(genericInterface.MakeGenericType(genericInterface.GenericParameters[0])));

            ifaces = Copier.GetAllInterfaces(ad, nonGenericClass).ToList();
            ifaces.Count.ShouldBe(3);
            ifaces[0].ShouldBeSameType(fakeHolder.MakeGenericType(nonGenericClass));
            ifaces[1].ShouldBeSameType(nonGenericInterface);
            ifaces[2].ShouldBeSameType(fakeHolder.MakeGenericType(nonGenericInterface));

            ifaces = Copier.GetAllInterfaces(ad, genericClass).ToList();
            ifaces.Count.ShouldBe(3);
            ifaces[0].ShouldBeSameType(fakeHolder.MakeGenericType(genericClass.MakeGenericType(genericClass.GenericParameters[0])));
            ifaces[1].ShouldBeSameType(genericInterface.MakeGenericInstanceType(genericClass.GenericParameters[0]));
            ifaces[2].ShouldBeSameType(fakeHolder.MakeGenericType(genericInterface.MakeGenericInstanceType(genericClass.GenericParameters[0])));

            ifaces = Copier.GetAllInterfaces(ad, nonGenericClass2).ToList();
            ifaces[0].ShouldBeSameType(genericInterface2.MakeGenericType(ad.MainModule.TypeSystem.Int32));
            ifaces[1].ShouldBeSameType(genericInterface.MakeGenericType(ad.MainModule.TypeSystem.Int32));
            ifaces[2].ShouldBeSameType(fakeHolder.MakeGenericType(ifaces[1]));
            ifaces[3].ShouldBeSameType(fakeHolder.MakeGenericType(ifaces[0]));
            ifaces[4].ShouldBeSameType(fakeHolder.MakeGenericType(nonGenericClass2));
        }

        [Test]
        [Category("NG")]
        public void InterfaceGeneratesWrapperClass()
        {
            CreateAssemblyFromCode("public interface IA { int Foo(int x); IA Bar(IA y, int z); }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.FullName == "Fake.IA");
            target.MainModule.Types.ShouldContain(t => t.FullName == "Fake._FakeImpl_IA");

            var targetInterface = target.MainModule.Types.Single(t => t.FullName == "Fake.IA");
            var targetImplementation = target.MainModule.Types.Single(t => t.FullName == "Fake._FakeImpl_IA");

            targetImplementation.Interfaces.Count.ShouldBe(2);
            targetImplementation.Interfaces[0].ShouldBeSameType(targetInterface);

            targetImplementation.Methods.Select(m => m.Name).ShouldBe(new[] { ".ctor", "Foo", "Bar", "Fake.__FakeHolder<global::IA>.get_Forward" }, ignoreOrder: true);

            var fooMethod = targetImplementation.Methods.Single(m => m.Name == "Foo");
            fooMethod.Parameters.Count.ShouldBe(1);

            fooMethod.Body.Instructions.InstructionsToString().ShouldBe(new[]
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld IA Fake._FakeImpl_IA::__fake_forward",
                "IL_0006: ldarg x",
                "IL_000a: callvirt System.Int32 IA::Foo(System.Int32)",
                "IL_000f: ret"
            });

            var barMethod = targetImplementation.Methods.Single(m => m.Name == "Bar");
            barMethod.Body.Instructions.InstructionsToString().ShouldBe(new[]
            {
                "IL_0000: ldarg.0",
                "IL_0001: ldfld IA Fake._FakeImpl_IA::__fake_forward",
                "IL_0006: ldarg y",
                "IL_000a: callvirt T Fake.__FakeHolder`1<IA>::get_Forward()",
                "IL_000f: ldarg z",
                "IL_0013: callvirt IA IA::Bar(IA,System.Int32)",
                "IL_0018: newobj System.Void Fake._FakeImpl_IA::.ctor(IA)",
                "IL_001d: ret"
            });

            var fakeForwardMethod = targetImplementation.Methods.Single(m => m.Name == "Fake.__FakeHolder<global::IA>.get_Forward");
            fakeForwardMethod.Overrides.ShouldHaveSingleItem();
        }

        [Test]
        public void SelfReferentialMethodIsRewritten()
        {
            CreateAssemblyFromCode("public class A { public A Run(A a) { return a; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "A");

            var targetType = target.MainModule.Types.Single(t => t.Name == "A");
            targetType.Methods.Where(m => !m.IsConstructor).Select(m => m.Name).ShouldBe(new[] { "Run" });

            var runMethod = targetType.Methods.Single(m => m.Name == "Run");
            runMethod.ReturnType.ShouldBeSameType(targetType);
            runMethod.Parameters.Count.ShouldBe(1);
            runMethod.Parameters[0].ParameterType.ShouldBeSameType(targetType);
        }

        [Test]
        public void SimpleAbstractClass()
        {
            CreateAssemblyFromCode("public abstract class A {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.Count.ShouldBe(3);
            target.MainModule.Types.ShouldContain(t => t.Name == "A");
            target.MainModule.Types.ShouldContain(t => t.Name == "FakeForwardA");

            var targetType = target.MainModule.Types.Single(t => t.Name == "FakeForwardA");
            targetType.ShouldHaveForwardConstructor(mscorlib.MainModule.Types.Single(t => t.Name == "A"));
            targetType.ShouldHaveForwardField(mscorlib.MainModule.Types.Single(t => t.Name == "A"));
        }

        [Test]
        public void SelfReferentialMethodInAbstractClass()
        {
            CreateAssemblyFromCode("public abstract class A { public abstract A Run(A a); }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.Count.ShouldBe(3);
            target.MainModule.Types.ShouldContain(t => t.Name == "A");
            target.MainModule.Types.ShouldContain(t => t.Name == "FakeForwardA");
        }

        //[Test]
        //public void SimpleInterface()
        //{
        //    var targetAndLib = CreateAssemblyFromCode("public interface IA {}", GetMethodName());
        //    var target = targetAndLib.Item1;
        //    var mscorlib = targetAndLib.Item2;

        //    target.MainModule.Types.ShouldContain(t => t.Name == "IA");
        //    target.MainModule.Types.ShouldContain(t => t.Name == "FakeForwardIA");

        //    var targetType = target.MainModule.Types.Single(t => t.Name == "IA");
        //    targetType.Methods.ShouldBeEmpty();
        //    targetType.Fields.ShouldBeEmpty();

        //    var targetForwardType = target.MainModule.Types.Single(t => t.Name == "FakeForwardIA");
        //    targetForwardType.ShouldHaveForwardConstructor(mscorlib.MainModule.Types.Single(t => t.Name == "IA"));
        //    targetForwardType.ShouldHaveForwardField(mscorlib.MainModule.Types.Single(t => t.Name == "IA"));

        //    WriteForPeVerify(target, GetMethodName());
        //}

        static void WriteForPeVerify(AssemblyDefinition target, string targetAssemblyName)
        {
#if PEVERIFY
            target.Write(Path.Combine(pathPrefix, $"{targetAssemblyName}.fake.dll"));
#endif
        }

        static AssemblyDefinition EmitAndReadAssembly(CSharpCompilation csc, string mscorlibName)
        {
#if PEVERIFY
            var memoryAssemblyPath = Path.Combine(pathPrefix, mscorlibName);
            var r = csc.Emit(memoryAssemblyPath);
            if (!r.Success)
                r.Diagnostics.ShouldBeEmpty();
            r.Success.ShouldBeTrue();
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(pathPrefix);
            return AssemblyDefinition.ReadAssembly(memoryAssemblyPath, new ReaderParameters { AssemblyResolver = resolver });
#else
            var ms = new MemoryStream();
            var r = csc.Emit(ms);
            r.Success.ShouldBeTrue();
            ms.Position = 0;
            return AssemblyDefinition.ReadAssembly(ms);
#endif
        }

        static AssemblyDefinition CreateAssembly(string program, [CallerMemberName] string assemblyName = null)
        {
            var st = CSharpSyntaxTree.ParseText(program);
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true);

            var csc = CSharpCompilation.Create($"{assemblyName}InMemoryAssembly",
                options: compilationOptions,
                syntaxTrees: new[] { st },
                references: new[] { MetadataReference.CreateFromFile(typeof(Enum).Assembly.Location) });

            return EmitAndReadAssembly(csc, $"{assemblyName}InMemoryAssembly.dll");
        }

        static AssemblyDefinition PatchAssembly(AssemblyDefinition original)
        {
            var patched = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition($"{original.Name}.fake",
                    original.Name.Version),
                original.MainModule.Name, ModuleKind.Dll);
#if PEVERIFY
            ((DefaultAssemblyResolver)patched.MainModule.AssemblyResolver).AddSearchDirectory(pathPrefix);
#endif

            var copier = new Copier(new ProcessTypeResolver(original),
                skipType: tr => tr.Namespace == "System" || tr.Namespace.StartsWith("System."));
            copier.Copy(original, ref patched, null, original.MainModule.Types.Select(t => t.FullName).ToArray());

            WriteForPeVerify(patched, original.Name.ToString());

            return patched;
        }

        static void CreateAssemblyFromCode(string program, out AssemblyDefinition patched, out AssemblyDefinition original, [CallerMemberName] string originalAssemblyName = null)
        {
            original = CreateAssembly(program, originalAssemblyName);

            patched = PatchAssembly(original);
        }
    }
}
