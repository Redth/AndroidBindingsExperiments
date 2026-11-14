#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Java.Interop.Kotlin;
using Kotlin.Coroutines;
using Net.Dot.Android.Kotlinasync;

namespace BindingCompilerSpike
{
	static class KotlinFlowBenchmarks
	{
		const int count = 10000;

		public static async Task Run (AsyncFixture fixture, Action<string> log)
		{
			await Consume (fixture, false, 256).ConfigureAwait (false);
			await Consume (fixture, true, 256).ConfigureAwait (false);
			var results = new List<Measurement> ();
			for (var iteration = 0; iteration < 3; iteration++) {
				foreach (var optimized in iteration % 2 == 0 ? new [] { false, true } : new [] { true, false }) {
					GC.Collect ();
					GC.WaitForPendingFinalizers ();
					GC.Collect ();
					var allocated = GC.GetTotalAllocatedBytes (true);
					var start = Stopwatch.GetTimestamp ();
					var sum = await Consume (fixture, optimized, count).ConfigureAwait (false);
					var elapsed = Stopwatch.GetElapsedTime (start).TotalMilliseconds;
					var bytes = GC.GetTotalAllocatedBytes (true) - allocated;
					if (sum != (long) count * (count - 1) / 2)
						throw new InvalidOperationException ("Flow benchmark dropped or changed values.");
					results.Add (new Measurement (optimized, bytes, elapsed, sum));
				}
			}
			var baseline = results.Where (m => !m.Optimized).Select (m => m.Bytes).Order ().ElementAt (1);
			var improved = results.Where (m => m.Optimized).Select (m => m.Bytes).Order ().ElementAt (1);
			using var buffer = new MemoryStream ();
			using (var json = new Utf8JsonWriter (buffer)) {
				json.WriteStartObject ();
				json.WriteNumber ("items_per_trial", count);
				json.WriteString ("baseline", "one native operation, completion Task and FlowItem per MoveNext");
				json.WriteString ("optimized", "one native subscription and reusable ValueTask source per enumeration");
				json.WriteString ("allocation_scope", "process-wide managed bytes; excludes Java/native heap allocations");
				json.WriteNumber ("baseline_median_bytes", baseline);
				json.WriteNumber ("optimized_median_bytes", improved);
				json.WriteStartArray ("trials");
				foreach (var result in results) {
					json.WriteStartObject ();
					json.WriteBoolean ("optimized", result.Optimized);
					json.WriteNumber ("managed_bytes", result.Bytes);
					json.WriteNumber ("milliseconds", result.Milliseconds);
					json.WriteNumber ("items_per_second", count * 1000 / result.Milliseconds);
					json.WriteNumber ("checksum", result.Checksum);
					json.WriteEndObject ();
				}
				json.WriteEndArray ();
				json.WriteEndObject ();
			}
			log ("BENCHMARK " + Encoding.UTF8.GetString (buffer.ToArray ()));
			if (improved >= baseline * 0.8)
				throw new InvalidOperationException ("Demand-driven Flow must reduce median managed allocations by at least 20% against the per-item-operation baseline.");
		}

		static async Task<long> Consume (AsyncFixture fixture, bool optimized, int items)
		{
			long sum = 0;
			var seen = 0;
			if (optimized) {
				await foreach (var value in fixture.BurstAsAsyncEnumerable (items).ConfigureAwait (false)) {
					sum += value;
					seen++;
				}
			} else {
				using var flow = fixture.Burst (items);
				using var session = CoroutineBridge.CreateFlow (flow, EmptyCoroutineContext.Instance);
				try {
					while (true) {
						using var item = await session.NextAsync ().ConfigureAwait (false);
						if (!item.HasValue)
							break;
						sum += KotlinCoroutines.GetInt32 (item.Value);
						seen++;
					}
				} finally {
					await session.CloseAsync ().ConfigureAwait (false);
				}
			}
			if (seen != items)
				throw new InvalidOperationException ("Incorrect Flow benchmark item count.");
			return sum;
		}

		sealed record Measurement (bool Optimized, long Bytes, double Milliseconds, long Checksum);
	}
}
