using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Java.Interop.Kotlin;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	public class KotlinDemandTests
	{
		[Test]
		public async Task ReusesValueTaskStateWithoutPrefetching ()
		{
			var source = new FakeSubscription (10000);
			await using var enumerator = Create (source);
			for (var i = 0; i < 10000; i++) {
				Assert.IsTrue (await enumerator.MoveNextAsync ());
				Assert.AreEqual (i, enumerator.Current);
				Assert.AreEqual (i + 1, source.Requests);
			}
			Assert.IsFalse (await enumerator.MoveNextAsync ());
		}

		[Test]
		public async Task CompletedValueMustBeConsumedBeforeAnotherRequest ()
		{
			var source = new FakeSubscription (2);
			await using var enumerator = Create (source);
			var first = enumerator.MoveNextAsync ();
			Assert.Throws<InvalidOperationException> (() => enumerator.MoveNextAsync ());
			Assert.IsTrue (await first);
			var second = enumerator.MoveNextAsync ();
			Assert.Throws<InvalidOperationException> (() => first.GetAwaiter ().GetResult ());
			Assert.IsTrue (await second);
		}

		[Test]
		public async Task DisposalWaitsForProducerCleanup ()
		{
			var source = new FakeSubscription (2) { DelayCancellation = true };
			var enumerator = Create (source);
			Assert.IsTrue (await enumerator.MoveNextAsync ());
			var dispose = enumerator.DisposeAsync ();
			Assert.IsFalse (dispose.IsCompleted);
			source.FinishCancellation ();
			await dispose;
			Assert.IsTrue (source.Disposed);
		}

		[Test]
		public async Task ConverterFailureIsNotDeliveredBeforeCleanup ()
		{
			var source = new FakeSubscription (2) { DelayCancellation = true };
			var enumerator = Create (source, _ => throw new FormatException ("conversion"));
			var move = enumerator.MoveNextAsync ();
			Assert.IsFalse (move.IsCompleted);
			source.FinishCancellation ();
			Assert.ThrowsAsync<FormatException> (async () => await move);
			await enumerator.DisposeAsync ();
		}

		[Test]
		public async Task CancellationUsesNativeCompletion ()
		{
			var source = new FakeSubscription (2) { DelayCancellation = true, DelayItem = true };
			using var cancellation = new CancellationTokenSource ();
			var enumerator = Create (source, token: cancellation.Token);
			var move = enumerator.MoveNextAsync ();
			cancellation.Cancel ();
			Assert.IsFalse (move.IsCompleted);
			source.FinishCancellation ();
			Assert.ThrowsAsync<OperationCanceledException> (async () => await move);
			await enumerator.DisposeAsync ();
		}

		[Test]
		public async Task CompletionAndDisposalCanRace ()
		{
			for (var i = 0; i < 100; i++) {
				var source = new FakeSubscription (1) { DelayItem = true };
				var enumerator = Create (source);
				var move = enumerator.MoveNextAsync ();
				await Task.WhenAll (Task.Run (() => source.FinishCancellation ()), Task.Run (async () => await enumerator.DisposeAsync ()));
				try {
					await move;
				} catch (OperationCanceledException) {
				}
				Assert.IsTrue (source.Disposed);
			}
		}

		[Test]
		public async Task JavaFailureIsNotSilentlyLostOnDisposal ()
		{
			var source = new FakeSubscription (0) { Error = new InvalidOperationException ("producer") };
			var enumerator = Create (source);
			Assert.ThrowsAsync<InvalidOperationException> (async () => await enumerator.MoveNextAsync ());
			await enumerator.DisposeAsync ();
		}

		static DemandAsyncEnumerator<int, int> Create (FakeSubscription source, Func<int, int> convert = null, CancellationToken token = default) =>
			new DemandAsyncEnumerator<int, int> (convert ?? (value => value), source.Subscribe, token);

		sealed class FakeSubscription : IDemandSubscription
		{
			readonly int count;
			Action<int> item;
			Action<Exception, bool> completed;
			int index;
			public int Requests;
			public bool Disposed;
			public bool DelayCancellation;
			public bool DelayItem;
			public Exception Error;

			public FakeSubscription (int count) => this.count = count;

			public IDemandSubscription Subscribe (Action<int> item, Action<Exception, bool> completed)
			{
				this.item = item;
				this.completed = completed;
				return this;
			}

			public void Request ()
			{
				Requests++;
				if (DelayItem)
					return;
				if (index < count)
					item (index++);
				else
					completed (Error, false);
			}

			public void Cancel ()
			{
				if (!DelayCancellation)
					FinishCancellation ();
			}

			public void FinishCancellation () => completed (null, true);
			public void Dispose () => Disposed = true;
		}
	}
}
