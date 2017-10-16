using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Castle.DynamicProxy;
using NSubstitute.Exceptions;

namespace NSubstitute.Proxies.CastleDynamicProxy
{
    public class CastlePatchedInterceptorRegistry
    {
        public static Action<object, IInterceptor> GetInstanceMocker(Type forType)
        {
            var field = GetOrAddTypeFieldCache(forType).InstanceInterceptor;
            if (field == null)
                return null;

            return (instance, interceptor) =>
            {
                if (field.GetValue(instance) != null)
                    throw new SubstituteException("Unexpected double hook of an instance");

                field.SetValue(instance, interceptor);
            };
        }

        public static Action<IInterceptor> GetStaticMocker(Type forType)
        {
            var field = GetOrAddTypeFieldCache(forType).StaticInterceptor;
            if (field == null)
                return null;

            return interceptor =>
            {
                if (field.GetValue(null) != null)
                    throw new SubstituteException("Unexpected double hook of a type (did you forget to dispose your previous substitute?)");

                field.SetValue(null, interceptor);
            };
        }

        public static void UnMockStatics(Type forType)
        {
            var field = GetOrAddTypeFieldCache(forType).StaticInterceptor;
            if (field == null)
                throw new SubstituteException("Unexpected non-patched type when attempting to unmock statics");

            if (field.GetValue(null) == null)
                throw new SubstituteException("Unexpected unmock of an already non-mocked type");

            field.SetValue(null, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object CallMockMethodOrImpl(object mockedInstance, Type[] methodGenericTypes, object[] methodCallArgs)
        {
            var mockedMethod = (MethodInfo)new StackTrace(1).GetFrame(0).GetMethod();
            var mockedType = mockedInstance?.GetType() ?? mockedMethod.DeclaringType;
            Debug.Assert(mockedType != null, "mockedType != null");

            if (mockedMethod.IsGenericMethodDefinition)
                mockedMethod = mockedMethod.MakeGenericMethod(methodGenericTypes);

            var originalMethod = GetOrAddOriginalMethodCache(mockedMethod);

            TypeFieldCache typeFieldCache;
            if (s_TypeFieldCache.TryGetValue(mockedType, out typeFieldCache))
            {
                var field = mockedInstance == null
                    ? typeFieldCache.StaticInterceptor
                    : typeFieldCache.InstanceInterceptor;
                var interceptor = (IInterceptor)field.GetValue(mockedInstance);
                if (interceptor != null)
                {
                    var invocation = new Invocation
                    {
                        Arguments = methodCallArgs,
                        Method = mockedMethod,
                        MethodInvocationTarget = originalMethod,
                        Proxy = mockedInstance,
                    };

                    interceptor.Intercept(invocation);

                    Array.Copy(invocation.Arguments, methodCallArgs, methodCallArgs.Length);
                    return invocation.ReturnValue;
                }
            }

            return originalMethod.Invoke(mockedInstance, methodCallArgs);
        }

        class Invocation : IInvocation
        {
            // NSubstitute does not currently require these
            public Type[] GenericArguments { get { throw new NotImplementedException(); } }
            public Type TargetType { get { throw new NotImplementedException(); } }
            public object GetArgumentValue(int index) { throw new NotImplementedException(); }
            public MethodInfo GetConcreteMethod() { throw new NotImplementedException(); }
            public MethodInfo GetConcreteMethodInvocationTarget() { throw new NotImplementedException(); }
            public void SetArgumentValue(int index, object value) { throw new NotImplementedException(); }

            // minimum config required for nsub
            public object[] Arguments { get; set; }
            public MethodInfo Method { get; set; }
            public MethodInfo MethodInvocationTarget { get; set; }
            public object Proxy { get; set; }
            public object ReturnValue { get; set; }

            public object InvocationTarget => Proxy;

            public void Proceed()
            {
                ReturnValue = MethodInvocationTarget.Invoke(InvocationTarget, Arguments);
            }
        }

        class TypeFieldCache
        {
            public TypeFieldCache(Type type)
            {
                InstanceInterceptor = type.GetField("__mockInterceptor", BindingFlags.Instance | BindingFlags.NonPublic);
                StaticInterceptor = type.GetField("__mockStaticInterceptor", BindingFlags.Static | BindingFlags.NonPublic);
            }

            public readonly FieldInfo InstanceInterceptor, StaticInterceptor;
        }

        static TypeFieldCache GetOrAddTypeFieldCache(Type type)
        {
            TypeFieldCache typeFieldCache;
            if (s_TypeFieldCache.TryGetValue(type, out typeFieldCache))
                return typeFieldCache;

            typeFieldCache = new TypeFieldCache(type);
            s_TypeFieldCache.Add(type, typeFieldCache);
            return typeFieldCache;
        }

        static MethodInfo GetOrAddOriginalMethodCache(MethodInfo mockedMethod)
        {
            MethodInfo originalMethod;
            if (s_MockToOriginalCache.TryGetValue(mockedMethod, out originalMethod))
                return originalMethod;

            // standard prefix on renamed original implementations by cil rewriter
            var originalMethodName = "__mock_" + mockedMethod.Name;

            // need to convert back to generic definition so we can look it up, even though we're just going to go back and instantiate it
            var paramTypesToMatch = (mockedMethod.IsGenericMethod ? mockedMethod.GetGenericMethodDefinition() : mockedMethod)
                .GetParameters().Select(p => p.ParameterType).ToArray();
            originalMethod = mockedMethod.DeclaringType.GetMethodExt(originalMethodName, paramTypesToMatch);

            if (originalMethod == null)
                throw new SubstituteException($"Unable to find method {originalMethodName} from {mockedMethod.DeclaringType.FullName}.{mockedMethod.Name}");

            if (originalMethod.IsGenericMethodDefinition)
                originalMethod = originalMethod.MakeGenericMethod(mockedMethod.GetGenericArguments());

            s_MockToOriginalCache.Add(mockedMethod, originalMethod);

            return originalMethod;
        }

        static readonly Dictionary<Type, TypeFieldCache> s_TypeFieldCache = new Dictionary<Type, TypeFieldCache>();
        static readonly Dictionary<MethodInfo, MethodInfo> s_MockToOriginalCache = new Dictionary<MethodInfo, MethodInfo>();
    }

    // this is copied straight from http://stackoverflow.com/a/7182379 and could probably be cut way down to serve just the purpose we need here
    static class LocalExtensions
    {
        /// <summary>
        /// Search for a method by name and parameter types.  
        /// Unlike GetMethod(), does 'loose' matching on generic
        /// parameter types, and searches base interfaces.
        /// </summary>
        /// <exception cref="AmbiguousMatchException"/>
        public static MethodInfo GetMethodExt(  this Type thisType, 
                                                string name, 
                                                params Type[] parameterTypes)
        {
            return GetMethodExt(thisType, 
                                name, 
                                BindingFlags.Instance 
                                | BindingFlags.Static 
                                | BindingFlags.Public 
                                | BindingFlags.NonPublic
                                | BindingFlags.FlattenHierarchy, 
                                parameterTypes);
        }

        /// <summary>
        /// Search for a method by name, parameter types, and binding flags.  
        /// Unlike GetMethod(), does 'loose' matching on generic
        /// parameter types, and searches base interfaces.
        /// </summary>
        /// <exception cref="AmbiguousMatchException"/>
        public static MethodInfo GetMethodExt(  this Type thisType, 
                                                string name, 
                                                BindingFlags bindingFlags, 
                                                params Type[] parameterTypes)
        {
            MethodInfo matchingMethod = null;

            // Check all methods with the specified name, including in base classes
            GetMethodExt(ref matchingMethod, thisType, name, bindingFlags, parameterTypes);

            // If we're searching an interface, we have to manually search base interfaces
            if (matchingMethod == null && thisType.IsInterface)
            {
                foreach (Type interfaceType in thisType.GetInterfaces())
                    GetMethodExt(ref matchingMethod, 
                                 interfaceType, 
                                 name, 
                                 bindingFlags, 
                                 parameterTypes);
            }

            return matchingMethod;
        }

        private static void GetMethodExt(   ref MethodInfo matchingMethod, 
                                            Type type, 
                                            string name, 
                                            BindingFlags bindingFlags, 
                                            params Type[] parameterTypes)
        {
            // Check all methods with the specified name, including in base classes
            foreach (MethodInfo methodInfo in type.GetMember(name, 
                                                             MemberTypes.Method, 
                                                             bindingFlags))
            {
                // Check that the parameter counts and types match, 
                // with 'loose' matching on generic parameters
                ParameterInfo[] parameterInfos = methodInfo.GetParameters();
                if (parameterInfos.Length == parameterTypes.Length)
                {
                    int i = 0;
                    for (; i < parameterInfos.Length; ++i)
                    {
                        if (!parameterInfos[i].ParameterType
                                              .IsSimilarType(parameterTypes[i]))
                            break;
                    }
                    if (i == parameterInfos.Length)
                    {
                        if (matchingMethod == null)
                            matchingMethod = methodInfo;
                        else
                            throw new AmbiguousMatchException(
                                   "More than one matching method found!");
                    }
                }
            }
        }

        /// <summary>
        /// Special type used to match any generic parameter type in GetMethodExt().
        /// </summary>
        public class T
        { }

        /// <summary>
        /// Determines if the two types are either identical, or are both generic 
        /// parameters or generic types with generic parameters in the same
        ///  locations (generic parameters match any other generic paramter,
        /// but NOT concrete types).
        /// </summary>
        private static bool IsSimilarType(this Type thisType, Type type)
        {
            // Ignore any 'ref' types
            if (thisType.IsByRef)
                thisType = thisType.GetElementType();
            if (type.IsByRef)
                type = type.GetElementType();

            // Handle array types
            if (thisType.IsArray && type.IsArray)
                return thisType.GetElementType().IsSimilarType(type.GetElementType());

            // If the types are identical, or they're both generic parameters 
            // or the special 'T' type, treat as a match
            if (thisType == type || ((thisType.IsGenericParameter || thisType == typeof(T)) 
                                 && (type.IsGenericParameter || type == typeof(T))))
                return true;

            // Handle any generic arguments
            if (thisType.IsGenericType && type.IsGenericType)
            {
                Type[] thisArguments = thisType.GetGenericArguments();
                Type[] arguments = type.GetGenericArguments();
                if (thisArguments.Length == arguments.Length)
                {
                    for (int i = 0; i < thisArguments.Length; ++i)
                    {
                        if (!thisArguments[i].IsSimilarType(arguments[i]))
                            return false;
                    }
                    return true;
                }
            }

            return false;
        }
    }
}
