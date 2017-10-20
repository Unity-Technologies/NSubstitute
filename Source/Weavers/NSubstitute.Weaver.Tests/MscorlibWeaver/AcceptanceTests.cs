#define PEVERIFY

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper;
using NUnit.Framework;
using Shouldly;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver
{
    [TestFixture]
    class AcceptanceTests
    {
        const string pathPrefix = @"c:\users\henriks\documents\visual studio 2017\Projects\FakeMscorlibRunner\FakeMscorlibRunner";

        [Test]
        public void CopyEverything()
        {
            var mscorlib = AssemblyDefinition.ReadAssembly(typeof(void).Assembly.Location);
            var nsubstitute = AssemblyDefinition.ReadAssembly(typeof(Substitute).Assembly.Location);
            var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("mscorlib.fake", mscorlib.Name.Version), mscorlib.MainModule.Name, ModuleKind.Dll);

            var copier = new Copier(new ProcessTypeResolver(mscorlib));
            copier.Copy(mscorlib, ref target, nsubstitute, mscorlib.MainModule.Types.Select(t => t.FullName).ToArray());

            target.Write(Path.Combine(pathPrefix, "fake.mscorlib.dll"));
        }

        static string GetMethodName([CallerMemberName] string method = null)
        {
            return method;
        }

        [Test]
        [Category("NG")]
        public void InnerTypeTest()
        {
            CreateAssemblyFromCode(@"public class A { public class B { public int X; } public B Foo(B b) { return b; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            a.NestedTypes.Count.ShouldBe(1);
            var method = a.Methods.Single(m => m.Name == "Foo");
            var instruction = method.Body.Instructions.First(i => i.OpCode == OpCodes.Callvirt);
            instruction.Operand.ToString().ShouldBe("A/B Fake.A/B::Fake.A/B.get__FakeForwardProp_A_slash_B()");
                
        }

        [Test]
        [Category("NG")]
        public void PrivateInterfaceImplementationTest()
        {
            CreateAssemblyFromCode(@"public interface IA { void Foo(); } public interface IB { int Foo(); } public class A : IA, IB { public int Foo() { return 0; } void IA.Foo() {} }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            var methods = a.Methods.Where(m => m.Name.Contains("Foo")).ToList();

            var expectedMethod = "System.Void Fake.A::Fake.IA.Foo()";
            Assert.That(methods.Select(m => m.FullName), Contains.Item(expectedMethod));

            var method = methods.Single(m => m.FullName == expectedMethod);
            Assert.That(method.Overrides, Has.Count.EqualTo(1));
            Assert.That(method.Overrides[0].FullName, Is.EqualTo("System.Void Fake.IA::Foo()"));
            ((MethodReference)method.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt).Operand).FullName.ShouldBe("System.Void IA::Foo()");
        }

        [Test]
        [Category("NG")]
        public void TypeGetsFakeHolderTest()
        {
            CreateAssemblyFromCode(@"public class A {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            a.Interfaces.Count.ShouldBe(1);
        }

        [Test]
        [Category("NG")]
        public void GenericTypeGetsFakeHolderTest()
        {
            CreateAssemblyFromCode(@"public interface IA<T> {} public class A<T> : IA<T> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A`2");
            a.Interfaces.ShouldBeEmpty();
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
        public void SimplePublicClassGetsFakeFieldAndCtor()
        {
            CreateAssemblyFromCode(@"public class A {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "A");

            var targetType = target.MainModule.Types.Single(t => t.Name == "A");
            targetType.FullName.ShouldBe("Fake.A");
            targetType.BaseType.ShouldBeSameType(target.MainModule.TypeSystem.Object);
            targetType.Fields.Count.ShouldBe(1);

            var originalType = mscorlib.MainModule.Types.Single(t => t.Name == "A");
            targetType.ShouldHaveForwardField(originalType);
            targetType.ShouldHaveForwardConstructor(originalType);
        }

        [Test]
        [Category("NG")]
        public void SimplePublicClassWithGenericsGetsFakeFieldAndCtor()
        {
            CreateAssemblyFromCode(@"public class A<T, U> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "A`2");

            var targetType = target.MainModule.Types.Single(t => t.Name == "A`2");
            targetType.FullName.ShouldBe("Fake.A`2");
            targetType.GenericParameters.Select(gp => gp.Name).ShouldBe(new[] { "T", "U" });

            var originalType = mscorlib.MainModule.Types.Single(t => t.Name == "A`2");
            var instanceType = originalType.MakeGenericInstanceType(targetType.GenericParameters.Cast<TypeReference>().ToArray());
            targetType.ShouldHaveForwardField(instanceType);
            targetType.ShouldHaveForwardConstructor(instanceType);
        }

        [Test]
        [Category("NG")]
        public void PublicClassWithGenericTypeConstraints()
        {
            CreateAssemblyFromCode(@"public class A<U> where U : System.Collections.Generic.List<U> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "A`1");

            var targetType = target.MainModule.Types.Single(t => t.Name == "A`1");
            targetType.FullName.ShouldBe("Fake.A`1");
            targetType.GenericParameters[0].Constraints.ShouldNotBeEmpty();
            targetType.GenericParameters[0].Constraints[0].FullName.ShouldBe("System.Collections.Generic.List`1<U>");
        }

        [Test]
        [Category("NG")]
        public void PrivateClassIsntProcessed()
        {
            CreateAssemblyFromCode(@"class A {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldNotContain(t => t.Name == "A");
        }

        [Test]
        [Category("NG")]
        public void PublicClassWithGenericTypeConstraintInTypeConstraint()
        {
            CreateAssemblyFromCode(@"public class A<U> where U : System.Collections.Generic.List<System.Collections.Generic.List<U>> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "A`1");

            var targetType = target.MainModule.Types.Single(t => t.Name == "A`1");
            targetType.FullName.ShouldBe("Fake.A`1");
            targetType.GenericParameters[0].Constraints.ShouldNotBeEmpty();
            targetType.GenericParameters[0].Constraints[0].FullName.ShouldBe("System.Collections.Generic.List`1<System.Collections.Generic.List`1<U>>");
        }

        [Test]
        [Category("NG")]
        public void EnumIsNotProcessed()
        {
            CreateAssemblyFromCode(@"public enum A {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldNotContain(t => t.Name == "A");
        }

        [Test]
        [Category("NG")]
        public void DontRewriteInternalMethods()
        {
            CreateAssemblyFromCode(@"public class A { internal void Foo() { } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.Name == "A");
            a.Methods.Select(m => m.Name).ShouldNotContain("Foo");
        }

        [Test]
        [Category("NG")]
        public void HasFieldMarshalParameter()
        {
            CreateAssemblyFromCode(@"public class A { public void Foo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string x) {} }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            var foo = a.Methods.Single(m => m.Name == "Foo");

            var p = foo.Parameters.Single();
            p.HasFieldMarshal.ShouldBeFalse();
        }

        [Test]
        [Category("NG")]
        public void SimpleInterfaceIsTranslated()
        {
            CreateAssemblyFromCode(@"public interface IA {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);
            
            target.MainModule.Types.ShouldContain(t => t.Name == "IA");

            var targetType = target.MainModule.Types.Single(t => t.Name == "IA");
            targetType.FullName.ShouldBe("Fake.IA");
            targetType.GetConstructors().ShouldBeEmpty();
            targetType.Fields.ShouldBeEmpty();
        }

        [Test]
        [Category("NG")]
        public void SimpleInterfaceImplementingInterfaceAreBothTranslatedAndImplementsPreserved()
        {
            CreateAssemblyFromCode(@"public interface IA {} public interface IB : IA {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "IA");
            target.MainModule.Types.ShouldContain(t => t.Name == "IB");

            var parentType = target.MainModule.Types.Single(t => t.Name == "IA");
            parentType.FullName.ShouldBe("Fake.IA");

            var targetType = target.MainModule.Types.Single(t => t.Name == "IB");
            targetType.FullName.ShouldBe("Fake.IB");

            targetType.Interfaces[1].ShouldBeSameType(parentType);
        }

        [Test]
        [Category("NG")]
        public void SimpleInterfaceImplementingGenericInterfaceBothTranslatedAndImplementsPreserved()
        {
            CreateAssemblyFromCode(@"public interface IA<T> {} public interface IB : IA<int> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.Name == "IA`1");
            target.MainModule.Types.ShouldContain(t => t.Name == "IB");

            var parentType = target.MainModule.Types.Single(t => t.Name == "IA`1");
            parentType.FullName.ShouldBe("Fake.IA`1");

            var targetType = target.MainModule.Types.Single(t => t.Name == "IB");
            targetType.FullName.ShouldBe("Fake.IB");

            targetType.Interfaces.Count.ShouldBe(2);
            targetType.Interfaces[1].ShouldBeSameType(parentType.MakeGenericInstanceType(mscorlib.MainModule.TypeSystem.Int32));
        }

        [Test]
        public void SimpleInterfaceWithMethodIsTranslated()
        {
            CreateAssemblyFromCode(@"public interface IA { int Foo(IA x); }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            target.MainModule.Types.ShouldContain(t => t.FullName == "Fake.IA");

            var targetType = target.MainModule.Types.Single(t => t.FullName == "Fake.IA");
            targetType.Methods.Select(m => m.Name).ShouldBe(new[] { "Foo", "get__FakeForwardProp_IA" });

            var method = targetType.Methods.Single(m => m.Name == "Foo");
            method.Parameters.Count.ShouldBe(1);
            method.Parameters[0].Name.ShouldBe("x");
            method.Parameters[0].ParameterType.ShouldBeSameType(targetType);
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
            CreateAssemblyFromCode("public abstract class B { public abstract B Baz(); public B Bar(B b) { return b; } } public abstract class A : B { public virtual A Foo(A a, int b) { return a; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var b = target.MainModule.Types.Single(t => t.FullName == "Fake.B");

            var bar = b.Methods.Single(m => m.Name == "Bar");
            bar.ReturnType.ShouldBeSameType(b);
            bar.IsAbstract.ShouldBeFalse();

            var callvirt = bar.Body.Instructions.First(i => i.OpCode == OpCodes.Callvirt);
            callvirt.Operand.ShouldBeOfType<MethodDefinition>();

            var method = (MethodDefinition)callvirt.Operand;
            method.Name.ShouldBe("Fake.B.get__FakeForwardProp_B");
        }

        [Test]
        [Category("NG")]
        public void IEquatableType()
        {
            CreateAssemblyFromCode("public interface IEquatable<T> { bool Equals(T other); } public sealed class TimeZoneInfo : IEquatable<TimeZoneInfo> { public bool Equals(TimeZoneInfo other) { return false; } }", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var fakeImpl = target.MainModule.Types.Single(t => t.FullName == "Fake._FakeImpl_IEquatable`1");
            var equals = fakeImpl.Methods.Single(m => m.Name == "Equals");
            equals.Parameters[0].ParameterType.ShouldBeSameType(fakeImpl.GenericParameters[0]);

            var callUnderlyingMethod = equals.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt);
            callUnderlyingMethod.Operand.ShouldBeOfType<MethodReference>();
            var reference = (MethodReference)callUnderlyingMethod.Operand;
            reference.DeclaringType.ShouldBeOfType<GenericInstanceType>();

            var timeZoneInfo = target.MainModule.Types.Single(t => t.FullName == "Fake.TimeZoneInfo");
            var tzEquals = timeZoneInfo.Methods.Single(m => m.Name == "Equals");
            tzEquals.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt).Select(i => i.Operand.ToString()).ShouldBe(new[] { "System.Boolean TimeZoneInfo::Equals(TimeZoneInfo)" });
            tzEquals.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldfld).ShouldBe(2);
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

            var ib = target.MainModule.Types.Single(t => t.FullName == "Fake.IB`1");
            ib.Properties.Select(p => p.Name).ShouldBe(new[]{"_FakeForwardProp_IB_tick_1"});

            var b = target.MainModule.Types.Single(t => t.FullName == "Fake.B");
            b.Properties.Select(p => p.Name).ShouldBe(new[] { "Fake.IB<System.Int32>._FakeForwardProp_IB_tick_1", "Fake.IA<System.Int32>._FakeForwardProp_IA_tick_1", "Fake.__FakeHolder<global::B>.Forward", "Fake.__FakeHolder<global::IB`1<System.Int32>>.Forward", "Fake.__FakeHolder<global::IA`1<System.Int32>>.Forward" });
        }

        [Test]
        [Category("NG")]
        public void MultipleInterfaceImplementationsGenerateSeparateForwardProperties()
        {
            CreateAssemblyFromCode("public interface IA<T> {} public class A : IA<int>, IA<float> {}", out AssemblyDefinition target, out AssemblyDefinition mscorlib);

            var a = target.MainModule.Types.Single(t => t.FullName == "Fake.A");
            a.Properties.Select(p => p.Name).ShouldBe(new []{"Fake.IA<System.Int32>._FakeForwardProp_IA_tick_1", "Fake.IA<System.Single>._FakeForwardProp_IA_tick_1", "Fake.__FakeHolder<global::A>.Forward", "Fake.__FakeHolder<global::IA`1<System.Int32>>.Forward", "Fake.__FakeHolder<global::IA`1<System.Single>>.Forward"});

            var fakeImplType = target.MainModule.Types.Single(t => t.FullName == "Fake._FakeImpl_IA`1");
            var prop = fakeImplType.Properties.Single(p => p.Name == "Fake.IA<T>._FakeForwardProp_IA_tick_1");
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
                "IL_000a: callvirt IA Fake.__FakeHolder<IA>::get_Forward()",
                "IL_000f: ldarg z",
                "IL_0013: callvirt IA IA::Bar(IA,System.Int32)",
                "IL_0018: newobj System.Void Fake._FakeImpl_IA::.ctor(IA)",
                "IL_001d: ret"
            });

            var fakeForwardMethod = targetImplementation.Methods.Single(m => m.Name == "Fake.__FakeHolder<IA>.get_Forward");
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

        static void CreateAssemblyFromCode(string program, out AssemblyDefinition target, out AssemblyDefinition mscorlib, [CallerMemberName] string mscorlibAssemblyName = null)
        {
            var st = CSharpSyntaxTree.ParseText(program);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug, allowUnsafe: true);
            var csc = CSharpCompilation.Create($"{mscorlibAssemblyName}InMemoryAssembly", options: options, syntaxTrees: new[] { st }, references: new[]
            {
                MetadataReference.CreateFromFile(typeof(Enum).Assembly.Location)
            });

            mscorlib = EmitAndReadAssembly(csc, $"{mscorlibAssemblyName}InMemoryAssembly.dll");
            var nsubstitute = AssemblyDefinition.ReadAssembly(typeof(Substitute).Assembly.Location);
            target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition($"{mscorlibAssemblyName}.fake", mscorlib.Name.Version), mscorlib.MainModule.Name, ModuleKind.Dll);
#if PEVERIFY
            ((DefaultAssemblyResolver)target.MainModule.AssemblyResolver).AddSearchDirectory(pathPrefix);
#endif

            var copier = new Copier(new ProcessTypeResolver(mscorlib), tr => tr.Namespace == "System" || tr.Namespace.StartsWith("System."));
            copier.Copy(mscorlib, ref target, nsubstitute, mscorlib.MainModule.Types.Select(t => t.FullName).ToArray());

            WriteForPeVerify(target, mscorlibAssemblyName);
        }
    }

    static class ShouldlyExtensions
    {
        public static void ShouldBeSameType(this TypeReference actual, TypeReference expected)
        {
            if (actual is GenericParameter)
            {
                expected.ShouldBeOfType<GenericParameter>();
                actual.FullName.ShouldBe(expected.FullName);
                return;
            }

            actual.FullName.ShouldBe(expected.FullName);
            actual.Resolve().Scope.Name.ShouldBe(expected.Resolve().Scope.Name);
        }

        public static void ShouldBeSameMethod(this MethodReference actual, MethodReference expected)
        {
            actual.FullName.ShouldBe(expected.FullName);
            //actual.ReturnType.ShouldBeSameType(expected.ReturnType);
            //actual.Parameters.Count.ShouldBe(expected.Parameters.Count);
            //for (var i = 0; i < actual.Parameters.Count; ++i)
            //{
            //    actual.Parameters[i].Name.ShouldBe(expected.Parameters[i].Name);
            //    actual.Parameters[i].ParameterType.ShouldBeSameType(expected.Parameters[i].ParameterType);
            //}
        }

        public static void ShouldHaveForwardField(this TypeDefinition actual, TypeReference expectedFieldType)
        {
            actual.Fields.ShouldContain(f => f.Name == "__fake_forward");
            var forwardField = actual.Fields.Single(f => f.Name == "__fake_forward");
            forwardField.FieldType.ShouldBeSameType(expectedFieldType);
        }

        public static void ShouldHaveForwardConstructor(this TypeDefinition actual, TypeReference expectedType)
        {
            var constructors = actual.GetConstructors().Where(c => c.Parameters.Count == 1).ToList();
            constructors.ShouldNotBeEmpty();

            var forwardCandidates = constructors.Where(c => c.Parameters.Any(p => p.ParameterType.FullName == expectedType.FullName)).ToList();
            forwardCandidates.ShouldNotBeEmpty();

            forwardCandidates.ForEach(fc => fc.Parameters[0].ParameterType.ShouldBeSameType(expectedType));
        }

        public static IEnumerable<string> InstructionsToString(this Collection<Instruction> instructions)
        {
            return instructions.Select(i => i.ToString());
        }
    }
}
