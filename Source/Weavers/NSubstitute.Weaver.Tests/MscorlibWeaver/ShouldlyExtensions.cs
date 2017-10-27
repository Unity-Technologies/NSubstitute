using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using Shouldly;

namespace NSubstitute.Weaver.Tests.MscorlibWeaver
{
    static class ShouldlyExtensions
    {
        public static void ShouldBeInstructionSet(this Collection<Instruction> instructions, params string[] expected)
        {
            instructions.Select(i => i.ToString()).ShouldBe(expected);
        }

        public static void ShouldContainVirtualMethodCall(this MethodDefinition givenMethod, string expectedMethodCall)
        {
            var methodCalled = (givenMethod.Body.Instructions
                .Single(i => i.OpCode == OpCodes.Callvirt)
                .Operand) as MethodReference;
            methodCalled.ShouldNotBeNull();
            methodCalled.FullName.ShouldBe(expectedMethodCall);            
        }

        public static void ShouldHaveMembers(this IEnumerable<MemberReference> givenCollection, IEnumerable<string> expectedMemberNames)
        {
            var givenMemberNames = givenCollection.Select(m => m.FullName);
            givenMemberNames.ShouldBe(expectedMemberNames, ignoreOrder:true);
        }
        
        public static void ShouldContainOnly<T>(this IEnumerable<T> givenCollection, int count)
        {
            givenCollection.Count().ShouldBe(count);
        }

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
