using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using NSubstitute.Weaver.MscorlibWeaver.MscorlibWrapper;
using NUnit.Framework;
using Shouldly;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver
{
    [TestFixture]
    class CopierTests
    {
        AssemblyDefinition mscorlib;
        AssemblyDefinition target;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            mscorlib = AssemblyDefinition.ReadAssembly(typeof(void).Assembly.Location);
            mscorlib.Name.Name.ShouldBe("mscorlib");
            target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("mscorlib.fake", new Version(999, 999)), "MAINMODULE", ModuleKind.Dll);
        }

        [Test]
        public void CopyCustomAttributes_NoPublicAttributes_NothingIsCopied()
        {
            var onlyPrivateCustomAttributesType = mscorlib.MainModule.Types.First(t => t.IsClass && t.CustomAttributes.Count > 0 && t.CustomAttributes.All(ca => !ca.Constructor.DeclaringType.Resolve().IsPublic));

            var targetType = new TypeDefinition(onlyPrivateCustomAttributesType.Namespace, onlyPrivateCustomAttributesType.Name, onlyPrivateCustomAttributesType.Attributes);

            var sut = new Copier(new ProcessTypeResolver(mscorlib));

            sut.CopyCustomAttributes(target, onlyPrivateCustomAttributesType, targetType);

            targetType.CustomAttributes.Count.ShouldBe(0);
        }

        [Test]
        public void CopyCustomAttributes_NoCustomAttributes_NethodIsCopied()
        {
            var noCustomAttributeType = mscorlib.MainModule.Types.First(t => t.IsClass && t.CustomAttributes.Count == 0);

            var targetType = new TypeDefinition(noCustomAttributeType.Namespace, noCustomAttributeType.Name, noCustomAttributeType.Attributes);

            var sut = new Copier(new ProcessTypeResolver(mscorlib));

            sut.CopyCustomAttributes(target, noCustomAttributeType, targetType);

            targetType.CustomAttributes.Count.ShouldBe(0);
        }

        [Test]
        public void CopyCustomAttributes_PublicAttributes_AreCopied()
        {
            var publicCustomAttributesType = mscorlib.MainModule.Types.First(t => t.IsClass && t.CustomAttributes.Count > 0 && t.CustomAttributes.Any(ca => ca.ConstructorArguments.Count > 0 && ca.Constructor.DeclaringType.Resolve().IsPublic));

            var targetType = new TypeDefinition(publicCustomAttributesType.Namespace, publicCustomAttributesType.Name, publicCustomAttributesType.Attributes);

            var sut = new Copier(new ProcessTypeResolver(mscorlib));

            sut.CopyCustomAttributes(target, publicCustomAttributesType, targetType);

            targetType.CustomAttributes.Count.ShouldBe(publicCustomAttributesType.CustomAttributes.Count);

            for (var i = 0; i < publicCustomAttributesType.CustomAttributes.Count; ++i)
            {
                var original = publicCustomAttributesType.CustomAttributes[i];
                var copy = targetType.CustomAttributes[i];

                copy.Constructor.FullName.ShouldBe(original.Constructor.FullName);
                copy.ConstructorArguments.Count.ShouldBe(original.ConstructorArguments.Count);

                for (var j = 0; j < copy.ConstructorArguments.Count; ++j)
                {
                    var copyArg = copy.ConstructorArguments[j];
                    var origArg = original.ConstructorArguments[j];

                    copyArg.Value.ShouldBe(origArg.Value);
                }
            }

            targetType.CustomAttributes[0].AttributeType.Resolve().Module.Assembly.FullName.ShouldBe(mscorlib.FullName);
        }

        [Test]
        public void CopyCustomAttributes_TargetCustomAttributeBeingFaked_HasFakedCall()
        {
            var publicCustomAttributesType = mscorlib.MainModule.Types.First(t => t.HasCustomAttributes && t.CustomAttributes[0].Constructor.FullName == "System.Void System.Runtime.InteropServices.ComVisibleAttribute::.ctor(System.Boolean)");

            Console.WriteLine(publicCustomAttributesType.CustomAttributes[0].Constructor.FullName);
            var targetType = new TypeDefinition(publicCustomAttributesType.Namespace, publicCustomAttributesType.Name, publicCustomAttributesType.Attributes);
            var comVisible = new TypeDefinition("System.Runtime.InteropServices", "ComVisibleAttribute", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit);
            comVisible.Methods.Add(new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.MainModule.TypeSystem.Void) { Parameters = { new ParameterDefinition("visibility", ParameterAttributes.None, target.MainModule.TypeSystem.Boolean) } });
            target.MainModule.Types.Add(comVisible);

            var sut = new Copier(null);

            sut.CopyCustomAttributes(target, publicCustomAttributesType, targetType);
            
            targetType.CustomAttributes[0].AttributeType.Resolve().Module.Assembly.FullName.ShouldBe(target.FullName);
            targetType.CustomAttributes[0].ConstructorArguments[0].Type.FullName.ShouldBe("System.Boolean");
        }

        [Test]
        public void CopyCustomAttributes_TargetCustomAttributeArgumentBeingFaked_HasFakedCall()
        {
            var publicCustomAttributesType = mscorlib.MainModule.Types.First(t => t.HasCustomAttributes && t.CustomAttributes[0].Constructor.FullName == "System.Void System.Runtime.InteropServices.ComVisibleAttribute::.ctor(System.Boolean)");

            Console.WriteLine(publicCustomAttributesType.CustomAttributes[0].Constructor.FullName);
            var targetType = new TypeDefinition(publicCustomAttributesType.Namespace, publicCustomAttributesType.Name, publicCustomAttributesType.Attributes);
            var boolean = new TypeDefinition("System", "Boolean", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.AnsiClass | TypeAttributes.Serializable | TypeAttributes.BeforeFieldInit, target.MainModule.Import(mscorlib.MainModule.Types.Single(t => t.FullName == "System.ValueType")));
            target.MainModule.Types.Add(boolean);

            var sut = new Copier(null);

            sut.CopyCustomAttributes(target, publicCustomAttributesType, targetType);
            
            targetType.CustomAttributes[0].AttributeType.Resolve().Module.Assembly.FullName.ShouldBe(mscorlib.FullName);
            targetType.CustomAttributes[0].ConstructorArguments[0].Type.Resolve().Module.Assembly.FullName.ShouldBe(target.FullName);
        }

        [Test]
        public void CopyType_GenericBaseTypeInstantiation_FakesBase()
        {
            var baseType = mscorlib.MainModule.Types.Single(t => t.FullName == "System.Collections.Generic.Comparer`1");
            var type = mscorlib.MainModule.Types.Single(t => t.FullName == "System.Collections.Generic.NullableComparer`1");

            var sut = new Copier(null);

            sut.CopyType(target, baseType);
            var result = sut.CopyType(target, type);

            result.BaseType.IsGenericInstance.ShouldBeTrue();
            var genericBase = (GenericInstanceType)result.BaseType;
            genericBase.FullName.ShouldBe("Fake.System.Collections.Generic.Comparer`1<System.Nullable`1<T>>");
            genericBase.GenericArguments[0].FullName.ShouldBe("System.Nullable`1<T>");
            var nullable = (GenericInstanceType)genericBase.GenericArguments[0];
            nullable.GenericArguments[0].ShouldBe(result.GenericParameters[0]);
        }
    }
}
