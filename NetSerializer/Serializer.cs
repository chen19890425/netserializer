﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace NetSerializer
{
	static partial class Serializer
	{
		static DynamicMethod GenerateDynamicSerializerStub(Type type)
		{
			var dm = new DynamicMethod("Serialize", null,
				new Type[] { typeof(Stream), type },
				typeof(Serializer), true);

			dm.DefineParameter(1, ParameterAttributes.None, "stream");
			dm.DefineParameter(2, ParameterAttributes.None, "value");

			return dm;
		}

		static MethodBuilder GenerateStaticSerializerStub(TypeBuilder tb, Type type)
		{
			var mb = tb.DefineMethod("Serialize", MethodAttributes.Public | MethodAttributes.Static, null, new Type[] { typeof(Stream), type });
			mb.DefineParameter(1, ParameterAttributes.None, "stream");
			mb.DefineParameter(2, ParameterAttributes.None, "value");
			return mb;
		}

		static void GenerateSerializerBody(CodeGenContext ctx, Type type, ILGenerator il)
		{
			// arg0: Stream, arg1: value

			D(il, "ser {0}", type.Name);

			if (type.IsArray)
				GenSerializerBodyForArray(ctx, type, il);
			else
				GenSerializerBody(ctx, type, il);
		}

		static void GenSerializerBody(CodeGenContext ctx, Type type, ILGenerator il)
		{
			var fields = GetFieldInfos(type);

			foreach (var field in fields)
			{
				// Note: the user defined value type is not passed as reference. could cause perf problems with big structs

				il.Emit(OpCodes.Ldarg_0);
				if (type.IsValueType)
					il.Emit(OpCodes.Ldarga, 1);
				else
					il.Emit(OpCodes.Ldarg, 1);
				il.Emit(OpCodes.Ldfld, field);

				GenSerializerCall(ctx, il, field.FieldType);
			}

			il.Emit(OpCodes.Ret);
		}

		static void GenSerializerBodyForArray(CodeGenContext ctx, Type type, ILGenerator il)
		{
			var elemType = type.GetElementType();

			// write array len
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldlen);
			il.EmitCall(OpCodes.Call, ctx.GetWriterMethodInfo(typeof(uint)), null);

			// declare i
			var idxLocal = il.DeclareLocal(typeof(int));

			// i = 0
			il.Emit(OpCodes.Ldc_I4_0);
			il.Emit(OpCodes.Stloc, idxLocal);

			var loopBodyLabel = il.DefineLabel();
			var loopCheckLabel = il.DefineLabel();

			il.Emit(OpCodes.Br, loopCheckLabel);

			// loop body
			il.MarkLabel(loopBodyLabel);

			// write element at index i
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldloc, idxLocal);
			il.Emit(OpCodes.Ldelem, elemType);

			GenSerializerCall(ctx, il, elemType);

			// i = i + 1
			il.Emit(OpCodes.Ldloc, idxLocal);
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Add);
			il.Emit(OpCodes.Stloc, idxLocal);

			il.MarkLabel(loopCheckLabel);

			// loop condition
			il.Emit(OpCodes.Ldloc, idxLocal);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldlen);
			il.Emit(OpCodes.Conv_I4);
			il.Emit(OpCodes.Clt);
			il.Emit(OpCodes.Brtrue, loopBodyLabel);

			il.Emit(OpCodes.Ret);
		}

		static void GenSerializerCall(CodeGenContext ctx, ILGenerator il, Type type)
		{
			// We can call the Serializer method directly for:
			// - Value types
			// - Sealed types with static Serializer method, as the method will handle null
			// Other reference types go through the SerializesSwitch

			bool direct;

			if (type.IsValueType)
				direct = true;
			else if (type.IsSealed && ctx.IsDynamic(type) == false)
				direct = true;
			else
				direct = false;

			if (direct)
				il.EmitCall(OpCodes.Call, ctx.GetWriterMethodInfo(type), null);
			else
				il.EmitCall(OpCodes.Call, ctx.SerializerSwitchMethodInfo, null);
		}


		static void GenerateSerializerSwitch(CodeGenContext ctx, ILGenerator il, IDictionary<Type, TypeData> map)
		{
			// arg0: Stream, arg1: object

			var idLocal = il.DeclareLocal(typeof(ushort));

			// get TypeID from object's Type
			var getTypeIDMethod = typeof(Serializer).GetMethod("GetTypeID", BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(object) }, null);
			il.Emit(OpCodes.Ldarg_1);
			il.EmitCall(OpCodes.Call, getTypeIDMethod, null);
			il.Emit(OpCodes.Stloc, idLocal);

			// write typeID
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldloc, idLocal);
			il.EmitCall(OpCodes.Call, ctx.GetWriterMethodInfo(typeof(ushort)), null);

			// +1 for 0 (null)
			var jumpTable = new Label[map.Count + 1];
			jumpTable[0] = il.DefineLabel();
			foreach (var kvp in map)
				jumpTable[kvp.Value.TypeID] = il.DefineLabel();

			il.Emit(OpCodes.Ldloc, idLocal);
			il.Emit(OpCodes.Switch, jumpTable);

			D(il, "eihx");
			il.ThrowException(typeof(Exception));

			/* null case */
			il.MarkLabel(jumpTable[0]);
			il.Emit(OpCodes.Ret);

			/* cases for types */
			foreach (var kvp in map)
			{
				var data = kvp.Value;

				il.MarkLabel(jumpTable[data.TypeID]);

				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldarg_1);
				if (kvp.Key.IsValueType)
					il.Emit(OpCodes.Unbox_Any, kvp.Key);
				else
					il.Emit(OpCodes.Castclass, kvp.Key);
				il.EmitCall(OpCodes.Call, data.WriterMethodInfo, null);

				il.Emit(OpCodes.Ret);
			}
		}
	}
}
