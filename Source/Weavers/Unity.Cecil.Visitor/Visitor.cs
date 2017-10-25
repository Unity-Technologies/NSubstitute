using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace Unity.Cecil.Visitor
{
    public enum Role
    {
        None,
        EventAdder,
        EventRemover,
        Member,
        BaseType,
        NestedType,
        Interface,
        InterfaceType,
        ReturnType,
        GenericParameter,
        Getter,
        Setter,
        ElementType,
        GenericArgument,
        Parameter,
        MethodBody,
        DeclaringType,
        Attribute,
        AttributeConstructor,
        AttributeType,
        AttributeArgument,
        AttributeArgumentType,
        LocalVariable,
        Operand
    }

    public class Context
    {
        private readonly Role _role;
        private readonly object _data;
        private readonly Context _parent;

        public static Context None
        {
            get { return new Context(Role.None, null); }
        }

        private Context(Role role, object data, Context parent = null)
        {
            _role = role;
            _data = data;
            _parent = parent;
        }

        public Role Role
        {
            get { return _role; }
        }

        public object Data
        {
            get { return _data; }
        }

        public Context Parent
        {
            get { return _parent; }
        }

        public Context Member(object data)
        {
            return new Context(Role.Member, data, this);
        }

        public Context NestedType(TypeDefinition data)
        {
            return new Context(Role.NestedType, data, this);
        }

        public Context BaseType(TypeDefinition data)
        {
            return new Context(Role.BaseType, data, this);
        }

        public Context Interface(TypeDefinition data)
        {
            return new Context(Role.Interface, data, this);
        }

#if NO // why?? need new cecil?
        public Context InterfaceType(InterfaceImplementation data)
        {
            return new Context(Role.InterfaceType, data, this);
        }
#endif

        public Context ReturnType(object data)
        {
            return new Context(Role.ReturnType, data, this);
        }

        public Context GenericParameter(object data)
        {
            return new Context(Role.GenericParameter, data, this);
        }

        public Context Getter(object data)
        {
            return new Context(Role.Getter, data, this);
        }

        public Context Setter(object data)
        {
            return new Context(Role.Setter, data, this);
        }

        public Context EventAdder(object data)
        {
            return new Context(Role.EventAdder, data, this);
        }

        public Context EventRemover(object data)
        {
            return new Context(Role.EventRemover, data, this);
        }

        public Context ElementType(object data)
        {
            return new Context(Role.ElementType, data, this);
        }

        public Context GenericArgument(object data)
        {
            return new Context(Role.GenericArgument, data, this);
        }

        public Context Parameter(object data)
        {
            return new Context(Role.Parameter, data, this);
        }

        public Context MethodBody(object data)
        {
            return new Context(Role.MethodBody, data, this);
        }

        public Context DeclaringType(object data)
        {
            return new Context(Role.DeclaringType, data, this);
        }

        public Context Attribute(object data)
        {
            return new Context(Role.Attribute, data, this);
        }

        public Context AttributeConstructor(object data)
        {
            return new Context(Role.AttributeConstructor, data, this);
        }

        public Context AttributeType(object data)
        {
            return new Context(Role.AttributeType, data, this);
        }

        public Context AttributeArgument(object data)
        {
            return new Context(Role.AttributeArgument, data, this);
        }

        public Context AttributeArgumentType(object data)
        {
            return new Context(Role.AttributeArgumentType, data, this);
        }

        public Context LocalVariable(object data)
        {
            return new Context(Role.LocalVariable, data, this);
        }

        public Context Operand(object data)
        {
            return new Context(Role.Operand, data, this);
        }
    }

    public class Visitor
    {
        public void Visit<T>(T node, Context context)
        {
            var nodeType = node.GetType();
            var method = FindVisitMethodFor(nodeType);
            if (method == null)
                throw new NotImplementedException("Invalid Cecil Definition " + nodeType.Name);

            method.Invoke(this, new object[] { node, context });
        }

        private static MethodInfo FindVisitMethodFor(Type type)
        {
            return GetMethodPortable(
                typeof(Visitor),
                "Visit",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { type, typeof(Context) });
        }

        // Need to copy this Portability method here so that we can avoid referencing our Portability assembly,
        // which we don't want to do because there are other projects in the Unity repo that reference this .csproj
        // and we don't want them to depend on Portability
        private static MethodInfo GetMethodPortable(Type type, string name, BindingFlags flags, Type[] parameters)
        {
#if NETCORE
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name)
                    continue;

                var actualParameters = method.GetParameters();

                if (actualParameters.Length != parameters.Length)
                    continue;

                var match = true;
                for (int i = 0; i < actualParameters.Length; i++)
                {
                    if (actualParameters[i].ParameterType != parameters[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return method;
            }

            return null;
#else
            return type.GetMethod(
                name,
                flags,
                null,
                parameters,
                new ParameterModifier[0]);
#endif
        }

        protected virtual void Visit(AssemblyDefinition assemblyDefinition, Context context)
        {
            foreach (var moduleDefinition in assemblyDefinition.Modules)
                Visit(moduleDefinition, context.Member(assemblyDefinition));
        }

        protected virtual void Visit(ModuleDefinition moduleDefinition, Context context)
        {
            var listOfTypes = moduleDefinition.Types.ToList();
            listOfTypes.Sort((x, y) => TypeReferenceExtensions.InheritanceChainLength(x)
                - TypeReferenceExtensions.InheritanceChainLength(y));

            foreach (var typeDefinition in listOfTypes)
                Visit(typeDefinition, context.Member(moduleDefinition));
        }

        protected virtual void Visit(TypeDefinition typeDefinition, Context context)
        {
            if (typeDefinition.BaseType != null)
                VisitTypeReference(typeDefinition.BaseType, context.BaseType(typeDefinition));
            foreach (var customAttribute in typeDefinition.CustomAttributes)
                Visit(customAttribute, context.Attribute(typeDefinition));

            foreach (var interfaceImpl in typeDefinition.Interfaces)
                Visit(interfaceImpl, context.Interface(typeDefinition));

            foreach (var genericParameter in typeDefinition.GenericParameters)
                Visit(genericParameter, context.GenericParameter(context));

            foreach (var propertyDefinition in typeDefinition.Properties)
                Visit(propertyDefinition, context.Member(typeDefinition));

            foreach (var fieldDefinition in typeDefinition.Fields)
                Visit(fieldDefinition, context.Member(typeDefinition));

            foreach (var methodDefinition in typeDefinition.Methods)
                Visit(methodDefinition, context.Member(typeDefinition));

            foreach (var eventDefinition in typeDefinition.Events)
                Visit(eventDefinition, context.Member(typeDefinition));

            foreach (var nestedType in typeDefinition.NestedTypes)
                Visit(nestedType, context.NestedType(typeDefinition));
        }

        protected virtual void Visit(EventDefinition eventDefinition, Context context)
        {
            VisitTypeReference(eventDefinition.EventType, context.ReturnType(eventDefinition));

            foreach (var customAttribute in eventDefinition.CustomAttributes)
                Visit(customAttribute, context.Attribute(eventDefinition));

            Visit(eventDefinition.AddMethod, context.EventAdder(eventDefinition));

            Visit(eventDefinition.RemoveMethod, context.EventRemover(eventDefinition));
        }

        protected virtual void Visit(FieldDefinition fieldDefinition, Context context)
        {
            VisitTypeReference(fieldDefinition.FieldType, context.ReturnType(fieldDefinition));

            foreach (var customAttribute in fieldDefinition.CustomAttributes)
                Visit(customAttribute, context.Attribute(fieldDefinition));
        }

        protected virtual void Visit(PropertyDefinition propertyDefinition, Context context)
        {
            VisitTypeReference(propertyDefinition.PropertyType, context.ReturnType(propertyDefinition));

            foreach (var customAttribute in propertyDefinition.CustomAttributes)
                Visit(customAttribute, context.Attribute(propertyDefinition));

            if (propertyDefinition.GetMethod != null)
                Visit(propertyDefinition.GetMethod, context.Getter(propertyDefinition));

            if (propertyDefinition.SetMethod != null)
                Visit(propertyDefinition.SetMethod, context.Setter(propertyDefinition));
        }

        protected virtual void Visit(MethodDefinition methodDefinition, Context context)
        {
            Visit(methodDefinition.MethodReturnType, context.ReturnType(methodDefinition));

            foreach (var customAttribute in methodDefinition.CustomAttributes)
                Visit(customAttribute, context.Attribute(methodDefinition));

            foreach (var genericParameter in methodDefinition.GenericParameters)
                Visit(genericParameter, context.GenericParameter(methodDefinition));

            foreach (var parameterDefinition in methodDefinition.Parameters)
                Visit(parameterDefinition, context.Parameter(methodDefinition));

            if (!methodDefinition.HasBody)
                return;

            Visit(methodDefinition.Body, context.MethodBody(methodDefinition));
        }

        protected virtual void Visit(CustomAttribute customAttribute, Context context)
        {
            if (customAttribute.Constructor != null)
                Visit(customAttribute.Constructor, context.AttributeConstructor(customAttribute));

            if (customAttribute.AttributeType != null)
                VisitTypeReference(customAttribute.AttributeType, context.AttributeType(customAttribute));

            foreach (var customAttributeArgument in customAttribute.ConstructorArguments)
                Visit(customAttributeArgument, context.AttributeArgument(customAttribute));

            foreach (var fieldArgument in customAttribute.Fields)
                Visit(fieldArgument, context.AttributeArgument(customAttribute));

            foreach (var propertyArgument in customAttribute.Properties)
                Visit(propertyArgument, context.AttributeArgument(customAttribute));
        }

        protected virtual void Visit(CustomAttributeArgument customAttributeArgument, Context context)
        {
            VisitTypeReference(customAttributeArgument.Type, context.AttributeArgumentType(customAttributeArgument));
        }

        protected virtual void Visit(Mono.Cecil.CustomAttributeNamedArgument customAttributeNamedArgument, Context context)
        {
            Visit(customAttributeNamedArgument.Argument, context);
        }

        protected virtual void Visit(FieldReference fieldReference, Context context)
        {
            VisitTypeReference(fieldReference.FieldType, context.ReturnType(fieldReference));
            VisitTypeReference(fieldReference.DeclaringType, context.DeclaringType(fieldReference));
        }

        protected virtual void Visit(MethodReference methodReference, Context context)
        {
            VisitTypeReference(methodReference.ReturnType, context.ReturnType(methodReference));
            VisitTypeReference(methodReference.DeclaringType, context.DeclaringType(methodReference));

            foreach (var genericParameter in methodReference.GenericParameters)
                VisitTypeReference(genericParameter, context.GenericParameter(methodReference));

            foreach (var parameterDefinition in methodReference.Parameters)
                Visit(parameterDefinition, context.Parameter(methodReference));

            var genericInstanceMethod = methodReference as GenericInstanceMethod;
            if (genericInstanceMethod == null)
                return;

            foreach (var genericArgument in genericInstanceMethod.GenericArguments)
                VisitTypeReference(genericArgument, context.GenericArgument(genericInstanceMethod));
        }

        protected virtual void Visit(TypeReference typeReference, Context context)
        {
            if (typeReference.DeclaringType != null)
                VisitTypeReference(typeReference.DeclaringType, context.DeclaringType(typeReference));
        }

        protected virtual void Visit(MethodReturnType methodReturnType, Context context)
        {
            VisitTypeReference(methodReturnType.ReturnType, context.ReturnType(methodReturnType));
        }

        protected virtual void Visit(ParameterDefinition parameterDefinition, Context context)
        {
            VisitTypeReference(parameterDefinition.ParameterType, context.ReturnType(parameterDefinition));
        }

        protected virtual void Visit(Mono.Cecil.Cil.MethodBody methodBody, Context context)
        {
            foreach (var exceptionHandler in methodBody.ExceptionHandlers)
                Visit(exceptionHandler, context.Member(exceptionHandler));

            foreach (var variableDefinition in methodBody.Variables)
                Visit(variableDefinition, context.LocalVariable(methodBody));

            foreach (var instruction in methodBody.Instructions)
                Visit(instruction, context);
        }

        protected virtual void Visit(VariableDefinition variableDefinition, Context context)
        {
            VisitTypeReference(variableDefinition.VariableType, context.ReturnType(variableDefinition));
        }

        protected virtual void Visit(Instruction instruction, Context context)
        {
            if (instruction.Operand == null)
                return;

            if (instruction.Operand is Instruction)
                return;

            var fieldReference = instruction.Operand as FieldReference;
            if (fieldReference != null)
            {
                Visit(fieldReference, context.Operand(instruction));
                return;
            }

            var methodReference = instruction.Operand as MethodReference;
            if (methodReference != null)
            {
                Visit(methodReference, context.Operand(instruction));
                return;
            }

            var typeReference = instruction.Operand as TypeReference;
            if (typeReference != null)
            {
                VisitTypeReference(typeReference, context.Operand(instruction));
                return;
            }

            var parameterDefinition = instruction.Operand as ParameterDefinition;
            if (parameterDefinition != null)
            {
                Visit(parameterDefinition, context.Operand(instruction));
                return;
            }

            var variableDefinition = instruction.Operand as VariableDefinition;
            if (variableDefinition != null)
                Visit(variableDefinition, context.Operand(instruction));
        }

        protected virtual void Visit(ExceptionHandler exceptionHandler, Context context)
        {
            if (exceptionHandler.CatchType != null)
                VisitTypeReference(exceptionHandler.CatchType, context.ReturnType(exceptionHandler));
        }

        protected virtual void Visit(GenericParameter genericParameter, Context context)
        {
        }

        protected virtual void Visit(ArrayType arrayType, Context context)
        {
            VisitTypeReference(arrayType.ElementType, context.ElementType(arrayType));
        }

        protected virtual void Visit(PointerType pointerType, Context context)
        {
            VisitTypeReference(pointerType.ElementType, context.ElementType(pointerType));
        }

        protected virtual void Visit(ByReferenceType byReferenceType, Context context)
        {
            VisitTypeReference(byReferenceType.ElementType, context.ElementType(byReferenceType));
        }

        protected virtual void Visit(PinnedType pinnedType, Context context)
        {
            VisitTypeReference(pinnedType.ElementType, context.ElementType(pinnedType));
        }

        protected virtual void Visit(SentinelType sentinelType, Context context)
        {
            VisitTypeReference(sentinelType.ElementType, context.ElementType(sentinelType));
        }

        protected virtual void Visit(FunctionPointerType functionPointerType, Context context)
        {
        }

        protected virtual void Visit(RequiredModifierType requiredModifierType, Context context)
        {
            VisitTypeReference(requiredModifierType.ElementType, context.ElementType(requiredModifierType));
        }

        protected virtual void Visit(GenericInstanceType genericInstanceType, Context context)
        {
            VisitTypeReference(genericInstanceType.ElementType, context.ElementType(genericInstanceType));

            foreach (var genericArgument in genericInstanceType.GenericArguments)
                VisitTypeReference(genericArgument, context.GenericArgument(genericInstanceType));
        }

        protected void VisitTypeReference(TypeReference typeReference, Context context)
        {
            var genericParameter = typeReference as GenericParameter;
            if (genericParameter != null)
            {
                Visit(genericParameter, context);
                return;
            }

            var arrayType = typeReference as ArrayType;
            if (arrayType != null)
            {
                Visit(arrayType, context);
                return;
            }

            var pointerType = typeReference as PointerType;
            if (pointerType != null)
            {
                Visit(pointerType, context);
                return;
            }

            var byReferenceType = typeReference as ByReferenceType;
            if (byReferenceType != null)
            {
                Visit(byReferenceType, context);
                return;
            }

            var functionPointerType = typeReference as FunctionPointerType;
            if (functionPointerType != null)
            {
                Visit(functionPointerType, context);
                return;
            }

            var pinnedType = typeReference as PinnedType;
            if (pinnedType != null)
            {
                Visit(pinnedType, context);
                return;
            }

            var sentinelType = typeReference as SentinelType;
            if (sentinelType != null)
            {
                Visit(sentinelType, context);
                return;
            }

            var genericInstanceType = typeReference as GenericInstanceType;
            if (genericInstanceType != null)
            {
                Visit(genericInstanceType, context);
                return;
            }

            var requiredModifierType = typeReference as RequiredModifierType;
            if (requiredModifierType != null)
            {
                Visit(requiredModifierType, context);
                return;
            }

            Visit(typeReference, context);
        }

#if NO // why?? need new cecil?
        protected virtual void Visit(InterfaceImplementation interfaceImpl, Context context)
        {
            VisitTypeReference(interfaceImpl.InterfaceType, context.InterfaceType(interfaceImpl));

            foreach (var customAttribute in interfaceImpl.CustomAttributes)
                Visit(customAttribute, context.Attribute(interfaceImpl));
        }
#endif
    }
}
