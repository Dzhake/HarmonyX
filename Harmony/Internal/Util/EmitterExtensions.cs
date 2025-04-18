using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using MonoMod.Utils.Cil;
using OpCode = System.Reflection.Emit.OpCode;
using OpCodes = System.Reflection.Emit.OpCodes;
using CecilOpCode = Mono.Cecil.Cil.OpCode;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace HarmonyLib.Internal.Util
{
    internal static class EmitterExtensions
    {
        private static DynamicMethodDefinition emitDMD;
        private static MethodInfo emitDMDMethod;
        private static Action<CecilILGenerator, OpCode, object> emitCodeDelegate;
        private static Dictionary<Type, CecilOpCode> storeOpCodes = new Dictionary<Type, CecilOpCode>
        {
	        [typeof(sbyte)] = CecilOpCodes.Stind_I1,
	        [typeof(byte)] = CecilOpCodes.Stind_I1,
	        [typeof(short)] = CecilOpCodes.Stind_I2,
	        [typeof(ushort)] = CecilOpCodes.Stind_I2,
	        [typeof(int)] = CecilOpCodes.Stind_I4,
	        [typeof(uint)] = CecilOpCodes.Stind_I4,
	        [typeof(long)] = CecilOpCodes.Stind_I8,
	        [typeof(ulong)] = CecilOpCodes.Stind_I8,
	        [typeof(float)] = CecilOpCodes.Stind_R4,
	        [typeof(double)] = CecilOpCodes.Stind_R8,
        };

        private static Dictionary<Type, CecilOpCode> loadOpCodes = new Dictionary<Type, CecilOpCode>
        {
				[typeof(sbyte)] = CecilOpCodes.Ldind_I1,
				[typeof(byte)] = CecilOpCodes.Ldind_I1,
				[typeof(short)] = CecilOpCodes.Ldind_I2,
				[typeof(ushort)] = CecilOpCodes.Ldind_I2,
				[typeof(int)] = CecilOpCodes.Ldind_I4,
				[typeof(uint)] = CecilOpCodes.Ldind_I4,
				[typeof(long)] = CecilOpCodes.Ldind_I8,
				[typeof(ulong)] = CecilOpCodes.Ldind_I8,
				[typeof(float)] = CecilOpCodes.Ldind_R4,
				[typeof(double)] = CecilOpCodes.Ldind_R8,
        };

        [MethodImpl(MethodImplOptions.Synchronized)]
        static EmitterExtensions()
        {
            if (emitDMD != null)
                return;
            InitEmitterHelperDMD();
        }

        public static CecilOpCode GetCecilStoreOpCode(this Type t)
        {
	        if (t.IsEnum)
		        return CecilOpCodes.Stind_I4;
	        return storeOpCodes.TryGetValue(t, out var opCode) ? opCode : CecilOpCodes.Stind_Ref;
        }

		  public static CecilOpCode GetCecilLoadOpCode(this Type t)
		  {
	        if (t.IsEnum)
		        return CecilOpCodes.Ldind_I4;
	        return loadOpCodes.TryGetValue(t, out var opCode) ? opCode : CecilOpCodes.Ldind_Ref;
		  }

        public static Type OpenRefType(this Type t)
        {
            if (t.IsByRef)
                return t.GetElementType();
            return t;
        }

        private static void InitEmitterHelperDMD()
        {
            emitDMD = new DynamicMethodDefinition("EmitOpcodeWithOperand", typeof(void),
                                                  new[] {typeof(CecilILGenerator), typeof(OpCode), typeof(object)});
            var il = emitDMD.GetILGenerator();

            var current = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Brtrue, current);

            il.Emit(OpCodes.Ldstr, "Provided operand is null!");
            il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] {typeof(string)}));
            il.Emit(OpCodes.Throw);

            foreach (var method in typeof(CecilILGenerator).GetMethods().Where(m => m.Name == "Emit"))
            {
                var paramInfos = method.GetParameters();
                if (paramInfos.Length != 2)
                    continue;
                var types = paramInfos.Select(p => p.ParameterType).ToArray();
                if (types[0] != typeof(OpCode))
                    continue;

                var paramType = types[1];

                il.MarkLabel(current);
                current = il.DefineLabel();

                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Isinst, paramType);
                il.Emit(OpCodes.Brfalse, current);

                il.Emit(OpCodes.Ldarg_2);

                if (paramType.IsValueType)
                    il.Emit(OpCodes.Unbox_Any, paramType);

                var loc = il.DeclareLocal(paramType);
                il.Emit(OpCodes.Stloc, loc);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc, loc);
                il.Emit(OpCodes.Callvirt, method);
                il.Emit(OpCodes.Ret);
            }

            il.MarkLabel(current);
            il.Emit(OpCodes.Ldstr, "The operand is none of the supported types!");
            il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] {typeof(string)}));
            il.Emit(OpCodes.Throw);
            il.Emit(OpCodes.Ret);

            emitDMDMethod = emitDMD.Generate();
            emitCodeDelegate = emitDMDMethod
	                .CreateDelegate<Action<CecilILGenerator, OpCode, object>>();
        }

        public static void Emit(this CecilILGenerator il, OpCode opcode, object operand)
        {
            emitCodeDelegate(il, opcode, operand);
        }

        public static void MarkBlockBefore(this CecilILGenerator il, ExceptionBlock block)
        {
            switch (block.blockType)
            {
                case ExceptionBlockType.BeginExceptionBlock:
                    il.BeginExceptionBlock();
                    return;
                case ExceptionBlockType.BeginCatchBlock:
                    il.BeginCatchBlock(block.catchType);
                    return;
                case ExceptionBlockType.BeginExceptFilterBlock:
                    il.BeginExceptFilterBlock();
                    return;
                case ExceptionBlockType.BeginFaultBlock:
                    il.BeginFaultBlock();
                    return;
                case ExceptionBlockType.BeginFinallyBlock:
                    il.BeginFinallyBlock();
                    return;
                case ExceptionBlockType.EndExceptionBlock:
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static void MarkBlockAfter(this CecilILGenerator il, ExceptionBlock block)
        {
            if (block.blockType == ExceptionBlockType.EndExceptionBlock)
                il.EndExceptionBlock();
        }

        public static LocalBuilder GetLocal(this CecilILGenerator il, VariableDefinition varDef)
        {
            var vars = (Dictionary<LocalBuilder, VariableDefinition>) AccessTools
                                                                      .Field(typeof(CecilILGenerator), "_Variables")
                                                                      .GetValue(il);
            var loc = vars.FirstOrDefault(kv => kv.Value == varDef).Key;
            if (loc != null)
                return loc;
            // TODO: Remove once MonoMod allows to specify this manually
            var type = varDef.VariableType.ResolveReflection();
            var pinned = varDef.VariableType.IsPinned;
            var index = varDef.Index;
            loc = (LocalBuilder) (
                c_LocalBuilder_params == 4 ? c_LocalBuilder.Invoke(new object[] { index, type, null, pinned }) :
                c_LocalBuilder_params == 3 ? c_LocalBuilder.Invoke(new object[] { index, type, null }) :
                c_LocalBuilder_params == 2 ? c_LocalBuilder.Invoke(new object[] { type, null }) :
                c_LocalBuilder_params == 0 ? c_LocalBuilder.Invoke(new object[] { }) :
                throw new NotSupportedException()
            );

            f_LocalBuilder_position?.SetValue(loc, (ushort) index);
            f_LocalBuilder_is_pinned?.SetValue(loc, pinned);
            vars[loc] = varDef;
            return loc;
        }

        // https://github.com/MonoMod/MonoMod/commit/2011243901351e69b6b5da89631e01bc8eb61da5
        // https://github.com/dotnet/runtime/blob/f1332ab0d82ee0e21ca387cbd1c8a87c5dfa4906/src/coreclr/System.Private.CoreLib/src/System/Reflection/Emit/RuntimeLocalBuilder.cs
        // In .NET 9, LocalBuilder is an abstract type, so we look for RuntimeLocalBuilder first.
        private static readonly Type t_LocalBuilder =
            Type.GetType("System.Reflection.Emit.RuntimeLocalBuilder") ?? typeof(LocalBuilder);
        private static readonly ConstructorInfo c_LocalBuilder =
            t_LocalBuilder.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                .OrderByDescending(c => c.GetParameters().Length).First();
        private static readonly FieldInfo f_LocalBuilder_position =
            t_LocalBuilder.GetField("position", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo f_LocalBuilder_is_pinned =
            t_LocalBuilder.GetField("is_pinned", BindingFlags.NonPublic | BindingFlags.Instance);
        private static int c_LocalBuilder_params = c_LocalBuilder.GetParameters().Length;
    }
}
