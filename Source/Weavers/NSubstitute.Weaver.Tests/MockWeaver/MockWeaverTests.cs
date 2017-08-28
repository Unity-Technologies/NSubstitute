using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using NUnit.Framework;

namespace NSubstitute.Weaver.Tests
{
    [TestFixture]
    public class MockWeaverTests
    {
        [Test]
        public void Abstract_Properties_Are_Not_Patched()
        {
            AssertPatchedAssembly(
                "abstract class C { public abstract int Prop { get ; } }",
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetType("C");
                    var method = type.Methods.SingleOrDefault(m => m.Name == "__mock_get_Prop");

                    Assert.That(method, Is.Null, "Abstract methods/properties should not be patched.");
                });
        }

        [Test]
        public void Abstract_Methods_Are_Not_Patched()
        {
            AssertPatchedAssembly(
                "abstract class C { public abstract int M(); }",
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetType("C");
                    var method = type.Methods.SingleOrDefault(m => m.Name == "__mock_M");

                    Assert.That(method, Is.Null, "Abstract methods/properties should not be patched.");
                });
        }

        [Test, Category("Structures")]
        public void SimpleStruct_Method()
        {
            bool called = false;
            AssertHookInjection(
                "struct SimpleStruct { public int M() { return -1; } }",
                "SimpleStruct",
                (_, __) => { called = true; return 42; },
                42);

            Assert.That(called, Is.True, "Hook should have been executed");
        }

        [Test, Category("Structures")]
        public void SimpleStruct_Property()
        {
            bool called = false;
            AssertHookInjection(
                "public struct SimpleStruct_Property { public int P { get { return -1; } set { } } }",
                "SimpleStruct_Property",
                (_, __) => { called = true; return 42; },
                42);

            Assert.That(called, Is.True, "Hook should have been executed");
        }

        [Test]
        public void Non_Abstract_Methods_Are_Patched_When_Declaring_Type_Is_Abstract()
        {
            AssertPatchedAssembly(
                "abstract class C { public abstract int M(); public void M1() {} }",
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetType("C");
                    var method = type.Methods.SingleOrDefault(m => m.Name == "__mock_M");

                    AssertPatchedType(type);

                    Assert.That(method, Is.Null, "Abstract methods/properties should not be patched.");
                });
        }

        [Test]
        public void ICalls()
        {
            AssertHookInjection(
                "using System.Runtime.CompilerServices; class C { [MethodImpl(MethodImplOptions.InternalCall)] public extern int M(); }",
                "icall", (_, __) => 42, 42);
        }

        [Test]
        public void ICallCtor()
        {
            AssertHookInjection(
                "using System.Runtime.CompilerServices; class C { [MethodImpl(MethodImplOptions.InternalCall)] public extern C(); }",
                "icall", (_, __) => 42, 42);
        }

        [Test]
        public void VoidNoParamInstanceMethod()
        {
            bool called = false;
            AssertHookInjection(
                "public class C { public void VoidNoParamInstanceMethod() { } }",
                "VoidNoParamInstanceMethod",
                (_, __) => { called = true; return null; },
                null);

            Assert.That(called, Is.True, "Hook should have been executed");
        }

        [Test]
        public void ArrayNoParamInstanceMethod()
        {
            AssertHookInjection(
                "public class C { public int[] ArrayMethod() { return new[] { 42 }; } }",
                (_, __) => new[] { 1 });
        }

        [Test]
        public void NonVoidNoParamInstanceMethod()
        {
            AssertHookInjection("public class Simple { public int NoParamInstanceMethod() { return 42; } }", (_, __) => 1);
        }

        [Test]
        public void NonParamStaticMethod()
        {
            var codeToPatch = "public class Simple { public static int NoParamStaticMethod() { return 42; } }";
            AssertHookInjection(codeToPatch, (_, __) => 3);
        }

        [Test]
        public void NonVoidInstanceMethodWithParams()
        {
            var codeToPatch = "public class Simple { public static bool NonVoidInstanceMethodWithParams(int i) { return false; } }";
            AssertHookInjection(codeToPatch, (_, __) => true);
        }

        [TestCase("public class C { public bool P { get { return false; } } }", true)]
        [TestCase("public class C { public string P { get { return \"Foo\"; } } }", "Bar")]
        public void InstancePropeties(string codeToPatch, object fakedValue)
        {
            var hookLambda = Expression.Lambda(Expression.Convert(Expression.Constant(fakedValue), typeof(object)), false, Expression.Parameter(typeof(Type[])), Expression.Parameter(typeof(object[])));
            AssertHookInjection(codeToPatch, hookLambda);
        }

        [TestCase("public class C { public static bool P { get { return false; } } }", true)]
        [TestCase("public class C { public static string P { get { return \"Foo\"; } } }", "Bar")]
        public void StaticPropeties(string codeToPatch, object fakedValue)
        {
            var hookLambda = Expression.Lambda(Expression.Convert(Expression.Constant(fakedValue), typeof(object)), false, Expression.Parameter(typeof(Type[])), Expression.Parameter(typeof(object[])));
            AssertHookInjection(codeToPatch, hookLambda);
        }

        [TestCase("StringConstant")]
        [TestCase(42)]
        [TestCase(true)]
        public void GenericMethod(object expectedHookedValue)
        {
            var hookLambda = Expression.Lambda(Expression.Convert(Expression.Constant(expectedHookedValue), typeof(object)), false, Expression.Parameter(typeof(Type[])), Expression.Parameter(typeof(object[])));
            AssertHookInjection("class C { public T GetValue<T>(T v) { return default(T); } }", "GenericMethod-" + expectedHookedValue.GetType().Name, (Func<Type[], object[], object>)hookLambda.Compile(), expectedHookedValue, null, new[] { expectedHookedValue.GetType() });
        }

        [TestCase("StringConstant")]
        [TestCase(42)]
        [TestCase(true)]
        public void MethodOnGenericType(object expectedHookedValue)
        {
            var hookLambda = Expression.Lambda(Expression.Convert(Expression.Constant(expectedHookedValue), typeof(object)), false, Expression.Parameter(typeof(Type[])), Expression.Parameter(typeof(object[])));
            AssertHookInjection("class C<T> { public T GetValue(T v) { return default(T); } }", "MethodOnGenericType-" + expectedHookedValue.GetType().Name, (Func<Type[], object[], object>)hookLambda.Compile(), expectedHookedValue);
        }

        [Test]
        public void MultipleGenericArguments()
        {
            Type[] collectedGenArgs = null;
            AssertHookInjection(
                "class C { public void M<T ,R>(R v) {} }",
                "MultipleGenericArguments", (genArgs, args) =>
                {
                    collectedGenArgs = genArgs;
                    return null;
                },
                null,
                null,
                new[] { typeof(int), typeof(string) });

            Assert.That(collectedGenArgs, Is.Not.Null);
            Assert.That(collectedGenArgs, Is.EqualTo(new[] { typeof(int), typeof(string) }));
        }

        [TestCase("StringConstant")]
        [TestCase(42)]
        [TestCase(true)]
        public void PropertyGetterOnGenericType(object expectedHookedValue)
        {
            var hookLambda = Expression.Lambda(Expression.Convert(Expression.Constant(expectedHookedValue), typeof(object)), false, Expression.Parameter(typeof(Type[])), Expression.Parameter(typeof(object[])));
            AssertHookInjection("class C<T> { public T P { get { return default(T); } } }", "PropertyGetterOnGenericType-" + expectedHookedValue.GetType().Name, (Func<Type[], object[], object>)hookLambda.Compile(), expectedHookedValue);
        }

        [Test]
        public void MethodWithOutAndRefParams()
        {
            var source = "class C { public void M(int i, out int j, ref int x) { j = x; x = 42; } }";

            var originalArgs = new object[] {0, 1, 2};

            AssertHookInjection(
                source,
                "MethodWithOutAndRefParams", (types, args) =>
                {
                    args[0] = 666;
                    args[1] = -1;
                    args[2] = (int)args[2] - 4;

                    return null;
                },
                null,
                originalArgs);

            Assert.That(originalArgs[0], Is.EqualTo(0));
            Assert.That(originalArgs[1], Is.EqualTo(-1));
            Assert.That(originalArgs[2], Is.EqualTo(-2));
        }

        [Test]
        public void MethodWithOutAndRefParamsWithDifferentTypes()
        {
            var source = @"
class C {
    public void M(ref float x, ref System.DateTime dt, ref string q) { x = 1.0f; dt = new System.DateTime(1); q = ""Hello "" + q; }
}";

            var originalArgs = new object[] { 0.0f, new DateTime(0), "world" };

            AssertHookInjection(
                source,
                "MethodWithOutAndRefParams", (types, args) =>
                {
                    args[0] = 2.0f;
                    args[1] = new DateTime(2);
                    args[2] = "Goodbye " + args[2];

                    return null;
                },
                null,
                originalArgs);

            Assert.That(originalArgs[0], Is.EqualTo(2.0f));
            Assert.That(originalArgs[1], Is.EqualTo(new DateTime(2)));
            Assert.That(originalArgs[2], Is.EqualTo("Goodbye world"));
        }

        [Test, Category("Methods")]
        public void Method_With_Default_Value()
        {
            AssertPatchedAssembly(
                "public class C { public int M(int v = 42) { return v; } }",
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetType("C");
                    var method = type.Methods.SingleOrDefault(m => m.Name == "__mock_M");

                    Assert.That(method.Parameters[0].HasConstant, Is.True);
                    Assert.That(method.Parameters[0].Constant, Is.EqualTo(42));
                });
        }

        [Test]
        public void TestPropertySetter()
        {
            bool hasBeenCalled = false;
            AssertHookInjection("class C { public int P { set { } } }", "TestPropertySetter", (_, __) =>
                {
                    hasBeenCalled = true;
                    return null;
                },
                null);

            Assert.That(hasBeenCalled, Is.True);
        }

        [Test]
        public void Events()
        {
            bool hasBeenCalled = false;
            AssertHookInjection("class C { public event System.EventHandler E; }", "Events", (_, __) =>
                {
                    hasBeenCalled = true;
                    return null;
                },
                null);

            Assert.That(hasBeenCalled, Is.True);
        }

        [Test]
        public void PropertyGetterAndSetters()
        {
            AssertRecordedHookInjection(
                "public class C { public int P { get { return 1; } set { } } }",
                "[System.Runtime.CompilerServices.CompilerGeneratedAttribute] class T { public object Test() { var c = new C(); c.P = -1; return c.P; } }");
        }

        [Test]
        public void ExceptionTest()
        {
            AssertHookInjection(
                "public class C { public void M() { try { throw new System.Exception(\"Hello world\"); } catch (System.Exception ex) { throw; } } }",
                (t, o) => null);
        }

        [TestCase("class C { public delegate void SimpleDelegate(); }", TestName = "Simple")]
        [TestCase("class C { public delegate T GenericDelegate<T>(); }", TestName = "Generic")]
        public void WillNotPatchDelegates(string source)
        {
            AssertPatchedAssembly(
                source,
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetAllTypes().Single(t => t.Name.Contains("Delegate"));
                    var mockedMethods = type.Methods.Where(m => m.Name.Contains("__mock_"));

                    Assert.That(mockedMethods, Is.Empty, "Delegates should not be patched.");
                });
        }

        [Test]
        public void WillNotPatchTestFixtureTypes()
        {
            AssertPatchedAssembly(
                "class TestFixtureAttribute : System.Attribute { } [TestFixture] class C { void Dummy() { } }",
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetType("C");
                    AssertNonPatchedType(type);
                });
        }

        [Test]
        public void WillNotAddDefaultCtorIfNoNormalMethods()
        {
            AssertPatchedAssembly(
                "public class C { private C() {} }",
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetType("C");
                    Assert.That(type.Methods.Where(m => m.IsConstructor).All(c => c.IsPrivate));
                });
        }

        [TestCase("public class C {}", TestName = "NoDefaultCtor")]
        [TestCase("public class C { private C() {} void Dummy() { } }", TestName = "PrivateDefaultCtor")]
        [TestCase("public class C { public C() { System.Console.WriteLine(42); } void Dummy() { } }", TestName = "NonEmptyDefaultCtor")]
        [TestCase("public class C { public C(int i = 0) { System.Console.WriteLine(42); } void Dummy() { } }", TestName = "DefaultCtorWithDefaultParameters")]
        [TestCase("public class Base {} public class C : Base { }", TestName = "NoDefaultCtorNonObjectAsBaseType")]
        [TestCase("public class C : Base { } public class Base {}", TestName = "NoDefaultCtorNonObjectAsBaseTypeReversed")]
        [TestCase("public class Base { public Base(int i) {} void Dummy() { } } public class C : Base { C(int i):base(i) { } void Dummy() { } }", TestName = "NoDefaultCtorWithBaseTypeWithNoDefaultCtor")]
        [TestCase("public class Base<T> { } public class C : Base<object> { C(int i):base() { } void Dummy() { } }", TestName = "GenericBase")]
        [TestCase("public class Base { static Base() {} } public class C : Base { public C() { } void Dummy() { } }", TestName = "BaseHasCctor")]
        [TestCase("public class C : Base { public C() : base(null) { } void Dummy() { } } public class Base { public Base(string s) { } }", TestName = "DefaultCtorWithBaseThatHasNonDefaultCtor")]
        [TestCase("public class C : Base { public C(float f) : base(null) { } void Dummy() { } } public class Base { public Base(string s) {} void Dummy() { } }", TestName = "NonDefaultCtorWithMockedBaseThatHasNonDefaultCtor")]
        public void Ensure_A_Public_Non_Throwing_Default_Ctor_Exists(string source)
        {
            AssertPatchedAssembly(
                source,
                patchedAssembly =>
                {
                    var type = patchedAssembly.MainModule.GetAllTypes().Single(t => t.Name == "C");
                    var ctors = type.Methods.Where(m => m.IsConstructor && (m.Parameters.Count == 0 || m.Parameters.All(p => p.HasDefault)));

                    Assert.That(ctors.Count(), Is.EqualTo(1));

                    var ctor = ctors.Single();

                    Assert.That(
                        ctor.Body.Instructions.Where(inst => inst.OpCode != OpCodes.Nop).Select(inst => inst.OpCode).ToArray(),
                        Is.EqualTo(new[] { OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Ret }),
                        "Default .ctor should have no body.");

                    Assert.That(ctor.IsPublic, Is.True, "Default .ctor should be public");

                    var baseCtorCalled = (MethodReference)ctor.Body.Instructions.Single(inst => inst.OpCode == OpCodes.Call).Operand;
                    Assert.That(baseCtorCalled.DeclaringType.Name, Is.EqualTo(type.BaseType.Name));
                });
        }

        private void AssertPatchedAssembly(string testSource, Action<AssemblyDefinition> validator, [CallerMemberName] string testName = null)
        {
            var testAssemblyPath = CompileAssemblyAndCacheResult(testName, "AssertPatchedAssembly", new[] { testSource });
            var hookedTestAssemblyPath = Path.ChangeExtension(testAssemblyPath, ".Patched" + Path.GetExtension(testAssemblyPath));

            using (var assembly = File.OpenRead(testAssemblyPath))
            {
                MockWeaver.InjectFakes(assembly, hookedTestAssemblyPath, Assembly.GetExecutingAssembly().Location);

                Console.WriteLine("Original assembly: {0}", testAssemblyPath);
                Console.WriteLine("Patched assembly: {0}", hookedTestAssemblyPath);

                var patchedAssembly = AssemblyDefinition.ReadAssembly(hookedTestAssemblyPath);
                validator(patchedAssembly);
            }
        }

        static void AssertRecordedHookInjection(string codeToPatch, string testSource, [CallerMemberName] string testName = null)
        {
            var testAssemblyPath = CompileAssemblyAndCacheResult(testName, "Simple", new[] { codeToPatch, testSource });
            var hookedTestAssemblyPath = Path.ChangeExtension(testAssemblyPath, ".Injected" + Path.GetExtension(testAssemblyPath));

            using (var assembly = File.OpenRead(testAssemblyPath))
            {
                MockWeaver.InjectFakes(assembly, hookedTestAssemblyPath, Assembly.GetExecutingAssembly().Location);

                Console.WriteLine("Original assembly: {0}", testAssemblyPath);
                Console.WriteLine("Patched assembly: {0}", hookedTestAssemblyPath);

                var patchedAssembly = Assembly.LoadFrom(hookedTestAssemblyPath);

                var testType = patchedAssembly.GetType("T");
                var method = testType.GetMethod("Test");
                var obj = Activator.CreateInstance(testType);

                var collector = new List<object[]>();
                CastlePatchedInterceptorRegistry.Register(
                    obj,
                    (_, args) =>
                    {
                        collector.Add(args);
                        return null;
                    });

                method.Invoke(obj, new object[0]);
                Assert.That(collector, Is.Not.Empty);
            }
        }

        static void AssertHookInjection(string codeToPatch, Expression<Func<Type[], object[], object>> hookLambda, [CallerMemberName] string testName = null)
        {
            var hookFunc = hookLambda.Compile();
            AssertHookInjection(codeToPatch, testName, hookFunc, hookFunc(null, null));
        }

        static void AssertHookInjection(string codeToPatch, LambdaExpression hookLambda, [CallerMemberName] string testName = null)
        {
            var expectedValueFromHook = ((ConstantExpression)((UnaryExpression)hookLambda.Body).Operand).Value;
            AssertHookInjection(codeToPatch, testName, (Func<Type[], object[], object>)hookLambda.Compile(), expectedValueFromHook);
        }

        static void AssertHookInjection(string codeToPatch, string testName, Func<Type[], object[], object> hookFunc, object expectedValueFromHook, object[] args = null, Type[] genArgs = null)
        {
            CastlePatchedInterceptorRegistry.Clear();

            var tree = CSharpSyntaxTree.ParseText(codeToPatch);
            var code = (CompilationUnitSyntax)tree.GetRoot();
            var testClass = code.Members.OfType<TypeDeclarationSyntax>().Single();
            var originalMethodDeclaration = (MethodDeclarationSyntax)testClass.Members.SingleOrDefault(m => m.IsKind(SyntaxKind.MethodDeclaration));

            var testAssemblyPath = CompileAssemblyAndCacheResult(testName, "Simple", new[] {codeToPatch});
            var hookedTestAssemblyPath = Path.ChangeExtension(testAssemblyPath, ".Fake" + Path.GetExtension(testAssemblyPath));

            using (var assembly = File.OpenRead(testAssemblyPath))
            {
                MockWeaver.InjectFakes(assembly, hookedTestAssemblyPath, Assembly.GetExecutingAssembly().Location);

                Console.WriteLine("Original assembly: {0}", testAssemblyPath);
                Console.WriteLine("Patched assembly: {0}", hookedTestAssemblyPath);

                var fakedAssembly = Assembly.LoadFrom(hookedTestAssemblyPath);

                var type = fakedAssembly.GetType(testClass.Identifier.Text);
                if (testClass.TypeParameterList != null && testClass.TypeParameterList.Parameters.Count > 0)
                {
                    type = fakedAssembly.GetType(testClass.Identifier.Text + "`" + testClass.TypeParameterList.Parameters.Count);
                    genArgs = genArgs ?? new[] { expectedValueFromHook.GetType() };
                    type = type.MakeGenericType(genArgs);
                }

                MethodInfo method;
                if (originalMethodDeclaration != null)
                {
                    method = type.GetMethod(originalMethodDeclaration.Identifier.ValueText);
                }
                else
                {
                    var originalPropertyDeclaration = (PropertyDeclarationSyntax)testClass.Members.SingleOrDefault(m => m.IsKind(SyntaxKind.PropertyDeclaration));
                    if (originalPropertyDeclaration != null)
                    {
                        var prop = type.GetProperty(originalPropertyDeclaration.Identifier.ValueText);
                        method = prop.GetMethod ?? prop.SetMethod;
                    }
                    else
                    {
                        var originalEvent = (EventFieldDeclarationSyntax)testClass.Members.SingleOrDefault(m => m.IsKind(SyntaxKind.EventFieldDeclaration));
                        var evt = type.GetEvent(originalEvent.Declaration.Variables[0].Identifier.ValueText);

                        method = evt.AddMethod ?? evt.RemoveMethod;
                    }
                }

                args = args ?? new object[method.GetParameters().Length];

                Action registerHook;
                object obj = null;
                if (method.IsStatic)
                {
                    registerHook = () => CastlePatchedInterceptorRegistry.Register(type, hookFunc);
                }
                else
                {
                    obj = Activator.CreateInstance(type);
                    registerHook = () => CastlePatchedInterceptorRegistry.Register(obj, hookFunc);
                }

                //var originalValue = method.Invoke(obj, dummyParams);

                registerHook();

                if (method.IsGenericMethodDefinition)
                    method = method.MakeGenericMethod(genArgs);

                var actualValueFromHook = method.Invoke(obj, args);

                if (method.ReturnType != typeof(void))
                {
                    Assert.AreEqual(expectedValueFromHook, actualValueFromHook);
                    //Assert.AreNotEqual(originalValue, actualValueFromHook);
                }
            }
        }

        static void AssertPatchedType(TypeDefinition type, bool invert = false)
        {
            var instanceMockInterceptor = type.Fields.SingleOrDefault(f => f.Name == "__mockInterceptor");
            var staticMockInterceptor = type.Fields.SingleOrDefault(f => f.Name == "__mockStaticInterceptor");

            if (invert)
            {
                Assert.That(instanceMockInterceptor, Is.Null, "Interceptor field found.");
                Assert.That(staticMockInterceptor, Is.Null, "Interceptor field found.");
            }
            else
            {
                Assert.That(instanceMockInterceptor, Is.Not.Null, "Interceptor field not found.");
                Assert.That(staticMockInterceptor, Is.Not.Null, "Interceptor field not found.");
            }
        }

        static void AssertNonPatchedType(TypeDefinition type)
        {
            AssertPatchedType(type, true);
        }

        protected static string CompileAssemblyAndCacheResult(string assemblyNamePrefix, string subfolder, IEnumerable<string> sources, IEnumerable<string> assemblyReferences = null)
        {
            var outputFolder = Path.Combine(Path.GetTempPath(), "FakeTests", subfolder);
            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            var outputPath = OutputPathFor(outputFolder, assemblyNamePrefix, assemblyReferences, sources);
            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            return CompileAssembly(outputPath, sources, assemblyReferences);
        }

        protected static string CompileAssembly(string outputPath, IEnumerable<string> sources, IEnumerable<string> assemblyReferences)
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var codeDomProvider = new Microsoft.CSharp.CSharpCodeProvider();
            var p = new CompilerParameters();

            p.OutputAssembly = outputPath;
            p.IncludeDebugInformation = true;
            p.CompilerOptions = "/o- /debug+ /d:UNITY_EDITOR /noconfig /warn:0";

            if (assemblyReferences != null)
            {
                p.ReferencedAssemblies.AddRange(
                    assemblyReferences.Where(an => !an.Contains("mscorlib.dll") && !an.Contains("System.dll")).ToArray());
            }

            p.ReferencedAssemblies.Add(typeof(Enumerable).Assembly.Location);

            var compilerResult = codeDomProvider.CompileAssemblyFromSource(p, sources.ToArray());
            if (compilerResult.Errors.Count > 0)
            {
                throw new Exception(compilerResult.Errors.OfType<CompilerError>().Aggregate("", (acc, curr) => acc + "\r\n" + curr.ErrorText));
            }

            return compilerResult.PathToAssembly;
        }

        static string OutputPathFor(string outputFolder, string assemblyNamePrefix, IEnumerable<string> assemblyReferences, IEnumerable<string> sources)
        {
            var ms = new MemoryStream();

            WriteToStream(ms, sources);
            WriteToStream(ms, assemblyReferences);

            ms.Position = 0;

            var hasher = new SHA256Managed();
            var hash = hasher.ComputeHash(ms);

            var hashStr = BitConverter.ToString(hash).Replace("-", "");

            return Path.Combine(outputFolder, assemblyNamePrefix + "." + hashStr + ".dll");
        }

        static void WriteToStream(Stream ms, IEnumerable<string> values)
        {
            if (values == null)
                return;

            foreach (var value in values)
            {
                var data = Encoding.UTF8.GetBytes(value);
                ms.Write(data, 0, data.Length);
            }
        }
    }

    class LiteralExpressionValueExtractor : CSharpSyntaxVisitor<object>
    {
        public override object VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            return node.Token.Value;
        }
    }

    public class CastlePatchedInterceptorRegistry
    {
        public static void Register(object obj, Func<Type[], object[], object> replacement)
        {
            registered[obj.GetHashCode()] = replacement;
        }

        public static Type[] GetGenericArguments(Type type)
        {
            return new[] { type };
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // for call stack query
        public static object CallMockMethodOrImpl(object mockedInstance, Type[] genericArgs, object[] callArgs)
        {
            Func<Type[], object[], object> found = null;
            if (mockedInstance == null && registered.Count == 1)
                found = registered.Values.First();

            if (found == null && !registered.TryGetValue(mockedInstance.GetHashCode(), out found))
            {
                return null;
            }

            return found(genericArgs, callArgs);
        }

        static IDictionary<long, Func<Type[], object[], object>> registered = new Dictionary<long, Func<Type[], object[], object>>();

        public static void Clear()
        {
            registered.Clear();
        }
    }
}
