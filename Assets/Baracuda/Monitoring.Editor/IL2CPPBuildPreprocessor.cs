// Copyright (c) 2022 Jonathan Lang

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Baracuda.Monitoring.API;
using Baracuda.Monitoring.IL2CPP;
using Baracuda.Monitoring.Internal.Profiling;
using Baracuda.Monitoring.Internal.Units;
using Baracuda.Monitoring.Internal.Utilities;
using Baracuda.Pooling.Concretions;
using Baracuda.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Scripting;
using Assembly = System.Reflection.Assembly;
using Debug = UnityEngine.Debug;

namespace Baracuda.Monitoring.Editor
{
    // Review:
    // Type definition generation could be simplified. Remove return values and just create a list that the types are
    // added to during an initial profiling process.
    public class IL2CPPBuildPreprocessor : IPreprocessBuildWithReport
    {
        #region --- Interface & Public Access ---

        /// <summary>
        /// Call this method to manually generate AOT types fort IL2CPP scripting backend.
        /// You can set the filepath of the target script file in the monitoring settings.
        /// </summary>
        public static void GenerateIL2CPPAheadOfTimeTypes()
        {
#if !DISABLE_MONITORING
            OnPreprocessBuildInternal();
#endif
        }

        public int callbackOrder => MonitoringSettings.GetInstance().PreprocessBuildCallbackOrder;

        public void OnPreprocessBuild(BuildReport report)
        {
#if !DISABLE_MONITORING
            if (!MonitoringSettings.GetInstance().UseIPreprocessBuildWithReport)
            {
                return;
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (PlayerSettings.GetScriptingBackend(group) == ScriptingImplementation.IL2CPP)
            {
                OnPreprocessBuildInternal();
            }
#endif
        }

        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Data & Nested Types ---

        private const BindingFlags STATIC_FLAGS = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags INSTANCE_FLAGS = BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        
        
        private static readonly string preserveAttribute = $"[{typeof(PreserveAttribute).FullName}]";
        private static readonly string methodImpAttribute = $"[{typeof(MethodImplAttribute).FullName}({typeof(MethodImplOptions).FullName}.{MethodImplOptions.NoOptimization.ToString()})]";
        private static readonly string aotBridgeClass = $"{typeof(AOTBridge).FullName}";

        private static readonly HashSet<Type> uniqueTypes = new HashSet<Type>();

#pragma warning disable CS0414
        private static List<string> errorLog = null;

        private class TypeDefinitionResult
        {
            public readonly Type Type;
            public readonly string FullDefinition;
            public readonly string RawDefinition;

            public TypeDefinitionResult(Type type, string fullDefinition, string rawDefinition)
            {
                Type = type;
                FullDefinition = fullDefinition;
                RawDefinition = rawDefinition;
            }
        }

        #endregion

#if !DISABLE_MONITORING

        #region --- Preprocess ---

        private static void OnPreprocessBuildInternal()
        {
            var textFile = MonitoringSettings.GetInstance().ScriptFileIL2CPP;
            var throwOnError = MonitoringSettings.GetInstance().ThrowOnTypeGenerationError;
            var filePath = AssetDatabase.GetAssetPath(textFile);
            Debug.Log($"Starting IL2CPP AOT Type Definition Generation.\nFilePath: {filePath}");

            errorLog = new List<string>();
            uniqueTypes.Clear();
            var typeDefinitionResults = GetTypeDefinitions();

            var content = new StringBuilder();

            content.Append("//---------- ----------------------------- ----------\n");
            content.Append("//---------- !!! AUTOGENERATED CONTENT !!! ----------\n");
            content.Append("//---------- ----------------------------- ----------\n");

            content.Append('\n');
            content.Append("//Runtime Monitoring");
            content.Append('\n');
            content.Append("//File generated: ");
            content.Append(DateTime.Now.ToString("u"));
            content.Append('\n');
            content.Append("//Please dont change the contents of this file. Otherwise IL2CPP runtime may not work with runtime monitoring!");
            content.Append('\n');
            content.Append("//Ensure that this file is located in Assembly-CSharp. Otherwise this file may not compile.");
            content.Append('\n');
            content.Append("//https://github.com/JohnBaracuda/Runtime-Monitoring");
            content.Append('\n');
            

            content.Append('\n');
            content.Append("#if ENABLE_IL2CPP && !DISABLE_MONITORING");
            content.Append('\n');

            content.Append('\n');
            content.Append("internal class IL2CPP_AOT\n{");
            
            for (var index = 0; index < typeDefinitionResults.Length; index++)
            {
                var result = typeDefinitionResults[index];
                content.Append("\n    ");
                content.Append("//");
                content.Append(result.RawDefinition);
                content.Append("\n    ");
                content.Append(preserveAttribute);
                content.Append("\n    ");
                content.Append(result.FullDefinition);
                content.Append(' ');
                content.Append("AOT_GENERATED_TYPE_");
                content.Append(index);
                content.Append(';');
                content.Append("\n    ");
            }
            
            content.Append('\n');
            content.Append("\n    ");
            content.Append(preserveAttribute);
            content.Append("\n    ");
            content.Append(methodImpAttribute);
            content.Append("\n    ");
            content.Append("private static void AOT()");
            content.Append("\n    ");
            content.Append("{");
            
            foreach (var uniqueType in uniqueTypes)
            {
                if (uniqueType.IsValueTypeArray())
                {
                    content.Append("\n        ");
                    content.Append(aotBridgeClass);
                    content.Append('.');
                    content.Append("AOTValueTypeArray");
                    content.Append('<');
                    content.Append(ToGenericTypeStringFullName(uniqueType.GetElementType()));
                    content.Append(">();");
                }
                else if (uniqueType.IsArray)
                {
                    content.Append("\n        ");
                    content.Append(aotBridgeClass);
                    content.Append('.');
                    content.Append("AOTReferenceTypeArray");
                    content.Append('<');
                    content.Append(ToGenericTypeStringFullName(uniqueType.GetElementType()));
                    content.Append(">();");
                }
                else if (uniqueType.IsGenericIDictionary())
                {
                    content.Append("\n        ");
                    content.Append(aotBridgeClass);
                    content.Append('.');
                    content.Append("AOTDictionary");
                    content.Append('<');
                    content.Append(ToGenericTypeStringFullName(uniqueType.GetGenericArguments()[0]));
                    content.Append(',');
                    content.Append(ToGenericTypeStringFullName(uniqueType.GetGenericArguments()[1]));
                    content.Append(">();");
                }
                else if (uniqueType.IsGenericIEnumerable(true))
                {
                    content.Append("\n        ");
                    content.Append(aotBridgeClass);
                    content.Append('.');
                    content.Append("AOTEnumerable");
                    content.Append('<');
                    content.Append(ToGenericTypeStringFullName(uniqueType.GetGenericArguments()[0]));
                    content.Append(">();");
                }
            }
            content.Append("\n    ");
            content.Append("}");

            content.Append('\n');
            content.Append('}');

            content.Append('\n');
            content.Append("#endif //ENABLE_IL2CPP && !DISABLE_MONITORING");
            content.Append('\n');
            
            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var stream = new FileStream(filePath, FileMode.OpenOrCreate);
            stream.Dispose();
            File.WriteAllText(filePath, content.ToString());

            foreach (var error in errorLog)
            {
                Debug.LogError(error);
            }

            if (throwOnError && errorLog.Any())
            {
                throw new OperationCanceledException("[MONITORING] Exception: Not all AOT types could be generated! " +
                                                     "This may lead to [ExecutionEngineException] exceptions in IL2CPP runtime! " +
                                                     "Cancelling build process!");
            }

            AssetDatabase.Refresh();
            Debug.Log("Successfully Completed IL2CPP AOT Type Definition Generation");
        }

        private static void BufferErrorInternal(string error)
        {
            if (errorLog.Contains(error))
            {
                return;
            }

            errorLog.Add(error);
        }

        /*
         * Profiling   
         */

        private static TypeDefinitionResult[] GetTypeDefinitions()
        {
            var definitionList = new List<TypeDefinitionResult>(200);

            foreach (var filteredAssembly in AssemblyProfiler.GetFilteredAssemblies())
            {
                if (IsEditorAssembly(filteredAssembly))
                {
                    continue;
                }

                foreach (var type in filteredAssembly.GetTypes())
                {
                    foreach (var memberInfo in type.GetMembers(STATIC_FLAGS))
                    {
                        if (memberInfo.HasAttribute<MonitorAttribute>(true))
                        {
                            foreach (var value in GetTypeDefinition(memberInfo))
                            {
                                if (value == null)
                                {
                                    continue;
                                }

                                if (!definitionList.Contains(value))
                                {
                                    definitionList.Add(value);
                                }
                            }
                        }
                    }

                    foreach (var memberInfo in type.GetMembers(INSTANCE_FLAGS))
                    {
                        if (memberInfo.HasAttribute<MonitorAttribute>(true))
                        {
                            foreach (var value in GetTypeDefinition(memberInfo))
                            {
                                if (value == null)
                                {
                                    continue;
                                }

                                if (!definitionList.Contains(value))
                                {
                                    definitionList.Add(value);
                                }
                            }
                        }
                    }
                }
            }

            return definitionList.ToArray();
        }

        /*
         * MemberInfo Profiling   
         */

        private static IEnumerable<TypeDefinitionResult> GetTypeDefinition(MemberInfo memberInfo)
        {
            switch (memberInfo)
            {
                case FieldInfo fieldInfo:
                    return GetDefinitionFromFieldInfo(fieldInfo);
                case PropertyInfo propertyInfo:
                    return GetDefinitionFromPropertyInfo(propertyInfo);
                case EventInfo eventInfo:
                    return GetDefinitionFromEventInfo(eventInfo);
                case MethodInfo methodInfo:
                    return GetDefinitionFromMethodInfo(methodInfo);
                default:
                    return null;
            }
        }

        private static IEnumerable<TypeDefinitionResult> GetDefinitionFromEventInfo(EventInfo eventInfo)
        {
            Debug.Assert(eventInfo.DeclaringType != null, "eventInfo.DeclaringType != null");
            Debug.Assert(eventInfo.EventHandlerType != null, "eventInfo.EventHandlerType != null");

            var targetType = eventInfo.DeclaringType;
            var valueType = eventInfo.EventHandlerType;

            yield return CreateTypeDefinitionFor(typeof(EventProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(EventUnit<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueUnit<,>), targetType, valueType);
        }

        private static IEnumerable<TypeDefinitionResult> GetDefinitionFromPropertyInfo(PropertyInfo propertyInfo)
        {
            System.Diagnostics.Debug.Assert(propertyInfo.DeclaringType != null, "propertyInfo.DeclaringType != null");
            System.Diagnostics.Debug.Assert(propertyInfo.PropertyType != null, "propertyInfo.PropertyType != null");

            var targetType = propertyInfo.DeclaringType;
            var valueType = propertyInfo.PropertyType;

            yield return CreateTypeDefinitionFor(typeof(PropertyProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(PropertyUnit<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueUnit<,>), targetType, valueType);
        }

        private static IEnumerable<TypeDefinitionResult> GetDefinitionFromFieldInfo(FieldInfo fieldInfo)
        {
            System.Diagnostics.Debug.Assert(fieldInfo.DeclaringType != null, "fieldInfo.DeclaringType != null");
            System.Diagnostics.Debug.Assert(fieldInfo.FieldType != null, "fieldInfo.FieldType != null");
            
            var targetType = fieldInfo.DeclaringType;
            var valueType = fieldInfo.FieldType;

            yield return CreateTypeDefinitionFor(typeof(FieldProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(FieldUnit<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueUnit<,>), targetType, valueType);
        }
        
        private static IEnumerable<TypeDefinitionResult> GetDefinitionFromMethodInfo(MethodInfo methodInfo)
        {
            System.Diagnostics.Debug.Assert(methodInfo.DeclaringType != null, "methodInfo.DeclaringType != null");
            System.Diagnostics.Debug.Assert(methodInfo.ReturnType != null, "methodInfo.ReturnType != null");

            var targetType = methodInfo.DeclaringType;
            var valueType = methodInfo.ReturnType.NotVoid();

            yield return CreateTypeDefinitionFor(typeof(MethodProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueProfile<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(MethodUnit<,>), targetType, valueType);
            yield return CreateTypeDefinitionFor(typeof(ValueUnit<,>), targetType, valueType);
        }

        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Create TypeDefinitionResult ---

        private static TypeDefinitionResult CreateTypeDefinitionFor(Type generic, Type targetType, Type valueType)
        {
            if (valueType.IsGenericParameter)
            {
                return null;
            }
            
            CheckType(generic, out var parsedGenericType);
            CheckType(targetType, out var parsedTargetType);
            CheckType(valueType, out var parsedValueType);

            if (parsedGenericType == null || parsedTargetType == null || parsedValueType == null)
            {
                return null;
            }

            var typedGeneric = parsedGenericType.MakeGenericType(parsedTargetType, parsedValueType);

            var fullDefinition = ToGenericTypeStringFullName(typedGeneric);
            var rawDefinition = generic.MakeGenericType(targetType, valueType).ToSyntaxString();

            if (string.IsNullOrWhiteSpace(fullDefinition) || string.IsNullOrWhiteSpace(rawDefinition))
            {
                return null;
            }

            uniqueTypes.Add(valueType);
            return new TypeDefinitionResult(parsedValueType, fullDefinition, rawDefinition);
        }

        /*
         * Helper   
         */

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckType(Type type, out Type validated)
        {
            if (type == typeof(object))
            {
                validated = type;
                return;
            }

            if (type.IsAccessible())
            {
                validated = type;
                return;
            }

            if (type.IsClass)
            {
                validated = typeof(object);
                return;
            }

            if (type.IsEnum)
            {
                switch (Marshal.SizeOf(Enum.GetUnderlyingType(type)))
                {
                    case 1:
                        validated = typeof(Enum8);
                        return;
                    case 2:
                        validated = typeof(Enum16);
                        return;
                    case 4:
                        validated = typeof(Enum32);
                        return;
                    case 8:
                        validated = typeof(Enum64);
                        return;
                }
            }

            var error =
                $"[MONITORING] Error: {type.ToSyntaxString()} is not accessible! ({type.FullName?.Replace('+', '.')})" +
                $"\nCannot generate AOT code for unmanaged internal/private types! " +
                $"Please make sure that {type.ToSyntaxString()} and all of its declaring types are either public or use a managed type instead of struct!";

            BufferErrorInternal(error);
            validated = null;
        }


        private static UnityEditor.Compilation.Assembly[] UnityAssemblies { get; }

        static IL2CPPBuildPreprocessor()
        {
            UnityAssemblies = CompilationPipeline.GetAssemblies();
        }

        public static bool IsEditorAssembly(Assembly assembly)
        {
            var editorAssemblies = UnityAssemblies;

            for (var i = 0; i < editorAssemblies.Length; i++)
            {
                var unityAssembly = editorAssemblies[i];

                if (unityAssembly.name != assembly.GetName().Name)
                {
                    continue;
                }
#if UNITY_2020_1_OR_NEWER
                if (unityAssembly.flags.HasFlagUnsafe(AssemblyFlags.EditorAssembly))
                {
                    return true;
                }
#else
                var intFlag = (int) unityAssembly.flags;
                if (intFlag.HasFlag32((int)AssemblyFlags.EditorAssembly))
                {
                    return true;
                }
#endif
            }

            return false;
        }


        //--------------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<Type, string> typeCacheFullName = new Dictionary<Type, string>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ToGenericTypeStringFullName(Type type)
        {
            if (typeCacheFullName.TryGetValue(type, out var value))
            {
                return value;
            }

            if (type.IsStatic())
            {
                return typeof(object).FullName?.Replace('+', '.');
            }

            if (type.IsGenericType)
            {
                var builder = ConcurrentStringBuilderPool.Get();
                var argBuilder = ConcurrentStringBuilderPool.Get();

                var arguments = type.GetGenericArguments();

                foreach (var t in arguments)
                {
                    // Let's make sure we get the argument list.
                    var arg = ToGenericTypeStringFullName(t);

                    if (argBuilder.Length > 0)
                    {
                        argBuilder.AppendFormat(", {0}", arg);
                    }
                    else
                    {
                        argBuilder.Append(arg);
                    }
                }

                if (argBuilder.Length > 0)
                {
                    Debug.Assert(type.FullName != null, "type.FullName != null");
                    builder.AppendFormat("{0}<{1}>", type.FullName.Split('`')[0],
                        argBuilder);
                }

                var retType = builder.ToString();

                typeCacheFullName.Add(type, retType.Replace('+', '.'));

                ConcurrentStringBuilderPool.ReleaseStringBuilder(builder);
                ConcurrentStringBuilderPool.ReleaseStringBuilder(argBuilder);
                return retType.Replace('+', '.');
            }

            Debug.Assert(type.FullName != null, $"type.FullName != null | {type.Name}, {type.DeclaringType}");
            
            var returnValue = type.FullName.Replace('+', '.');
            typeCacheFullName.Add(type, returnValue);
            return returnValue;
        }

        #endregion

#endif //!DISABLE_MONITORING
    }
}