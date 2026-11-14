using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xamarin.Android.Tools.Bytecode
{
	public sealed class KotlinTypeContract
	{
		public string? Classifier { get; set; }
		public string? TypeParameter { get; set; }
		public bool Nullable { get; set; }
		public bool IsSuspend { get; set; }
		[JsonIgnore (Condition = JsonIgnoreCondition.WhenWritingDefault)]
		public bool DefinitelyNonNull { get; set; }
		[JsonIgnore (Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? TypeParameterId { get; set; }
		[JsonIgnore (Condition = JsonIgnoreCondition.WhenWritingNull)]
		public List<KotlinTypeContract>? UpperBounds { get; set; }
		[JsonIgnore (Condition = JsonIgnoreCondition.WhenWritingNull)]
		public KotlinTypeContract? OuterType { get; set; }
		[JsonIgnore (Condition = JsonIgnoreCondition.WhenWritingNull)]
		public KotlinTypeContract? FlexibleUpperBound { get; set; }
		public List<KotlinTypeArgumentContract> Arguments { get; set; } = [];

		public string ToJson () => JsonSerializer.Serialize (this);

		public static KotlinTypeContract? Create (KotlinType? type, int typeId, KotlinFile? file, KotlinFunction? function = null)
		{
			var table = function?.TypeTable ?? file?.TypeTable;
			var parameters = (function?.TypeParameters ?? [])
				.Concat ((file as KotlinClass)?.TypeParameters ?? [])
				.GroupBy (p => p.Id).ToDictionary (g => g.Key, g => g.First ().Name);
			return Create (type, typeId, table, parameters, new HashSet<int> (), 0);
		}

		static KotlinTypeContract? Create (KotlinType? type, int typeId, KotlinTypeTable? table,
			Dictionary<int, string?> parameters, HashSet<int> visiting, int depth)
		{
			if (depth > 64)
				throw new InvalidDataException ("Kotlin type metadata exceeds the maximum nesting depth.");

			var from_table = type == null;
			if (from_table) {
				if (table?.Types == null || typeId < 0 || typeId >= table.Types.Count)
					return null;
				if (!visiting.Add (typeId))
					throw new InvalidDataException ($"Cyclic Kotlin type-table reference: {typeId}.");
				type = table.Types [typeId];
			}
			if (type == null)
				return null;

			var result = new KotlinTypeContract {
				Classifier = (type.ClassName ?? type.TypeAliasName)?.TrimEnd (';'),
				TypeParameter = type.TypeParameterName ?? (type.TypeParameter is int parameter &&
					parameters.TryGetValue (parameter, out var name) ? name : null),
				Nullable = type.Nullable || (from_table && table != null && table.FirstNullable >= 0 && typeId >= table.FirstNullable),
				IsSuspend = type.Flags.HasFlag (KotlinTypeFlags.SuspendType),
			};
			foreach (var argument in type.Arguments ?? [])
				result.Arguments.Add (new KotlinTypeArgumentContract {
					Projection = argument.Projection.ToString (),
					Type = argument.Projection == KotlinProjection.Star ? null :
						Create (argument.Type, argument.TypeId, table, parameters, visiting, depth + 1),
				});
			if (from_table)
				visiting.Remove (typeId);
			return result;
		}
	}

	public sealed class KotlinTypeArgumentContract
	{
		public string Projection { get; set; } = "";
		public KotlinTypeContract? Type { get; set; }
	}
}
