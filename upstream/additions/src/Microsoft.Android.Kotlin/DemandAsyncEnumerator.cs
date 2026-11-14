#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Java.Interop.Kotlin
{
	internal interface IDemandSubscription : IDisposable
	{
		void Request ();
		void Cancel ();
	}

	internal sealed class DemandAsyncEnumerator<TSource, T> : IAsyncEnumerator<T>, IValueTaskSource<bool>
	{
		readonly object gate = new object ();
		readonly Func<TSource, T> convert;
		readonly CancellationToken cancellationToken;
		readonly TaskCompletionSource finished = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
		readonly IDemandSubscription subscription;
		readonly CancellationTokenRegistration registration;
		ManualResetValueTaskSourceCore<bool> next = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true };
		Exception? error;
		(bool HasValue, T Value) current;
		bool completed;
		bool pending;
		bool outstanding;
		bool disposing;
		bool errorObserved;
		Task? disposal;

		public DemandAsyncEnumerator (Func<TSource, T> convert,
			Func<Action<TSource>, Action<Exception?, bool>, IDemandSubscription> subscribe, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull (convert);
			ArgumentNullException.ThrowIfNull (subscribe);
			cancellationToken.ThrowIfCancellationRequested ();
			this.convert = convert;
			this.cancellationToken = cancellationToken;
			subscription = subscribe (OnItem, OnCompleted);
			registration = cancellationToken.Register (subscription.Cancel);
		}

		public T Current {
			get {
				lock (gate) {
					if (!current.HasValue)
						throw new InvalidOperationException ("The enumerator has no current value.");
					return current.Value;
				}
			}
		}

		public ValueTask<bool> MoveNextAsync ()
		{
			short version;
			lock (gate) {
				if (disposing)
					throw new ObjectDisposedException (nameof (DemandAsyncEnumerator<TSource, T>));
				if (outstanding)
					throw new InvalidOperationException ("The previous MoveNextAsync must be consumed before requesting another value.");
				if (completed) {
					if (error != null) {
						errorObserved = true;
						return ValueTask.FromException<bool> (error);
					}
					return new ValueTask<bool> (false);
				}
				outstanding = pending = true;
				next.Reset ();
				version = next.Version;
			}
			try {
				subscription.Request ();
			} catch (Exception ex) {
				FailAndCancel (ex);
			}
			return new ValueTask<bool> (this, version);
		}

		void OnItem (TSource value)
		{
			bool invalid;
			lock (gate) {
				if (disposing || completed)
					return;
				invalid = !pending;
				if (invalid) {
					error = new InvalidOperationException ("The native Flow delivered a value without outstanding demand.");
				}
			}
			if (invalid) {
				subscription.Cancel ();
				return;
			}
			T converted;
			try {
				converted = convert (value);
			} catch (Exception ex) {
				FailAndCancel (ex);
				return;
			}
			lock (gate) {
				if (disposing || completed)
					return;
				current = (true, converted);
				pending = false;
				next.SetResult (true);
			}
		}

		void FailAndCancel (Exception failure)
		{
			lock (gate)
				error = error == null ? failure : new AggregateException (error, failure);
			try {
				subscription.Cancel ();
			} catch (Exception cancellationFailure) {
				OnCompleted (cancellationFailure, false);
			}
		}

		void OnCompleted (Exception? failure, bool cancelled)
		{
			lock (gate) {
				if (completed)
					return;
				completed = true;
				if (failure != null && !cancelled)
					error = error == null ? failure : new AggregateException (error, failure);
				if (error == null && cancelled && !disposing)
					error = new OperationCanceledException (cancellationToken.IsCancellationRequested ?
						cancellationToken : new CancellationToken (true));
				if (pending) {
					pending = false;
					if (error != null)
						next.SetException (error);
					else
						next.SetResult (false);
				}
			}
			finished.TrySetResult ();
		}

		public ValueTask DisposeAsync ()
		{
			TaskCompletionSource start;
			Task result;
			lock (gate) {
				if (disposal != null)
					return new ValueTask (disposal);
				disposing = true;
				start = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
				disposal = result = DisposeCoreAsync (start.Task);
			}
			// Cancellation and native completion can run synchronously. Do not
			// call native code or dispose registrations while holding gate.
			start.SetResult ();
			return new ValueTask (result);
		}

		async Task DisposeCoreAsync (Task start)
		{
			await start.ConfigureAwait (false);
			try {
				subscription.Cancel ();
				await finished.Task.ConfigureAwait (false);
			} finally {
				registration.Dispose ();
				subscription.Dispose ();
			}
			Exception? unobserved;
			lock (gate) {
				current = default;
				unobserved = errorObserved || error is OperationCanceledException ? null : error;
			}
			if (unobserved != null)
				ExceptionDispatchInfo.Capture (unobserved).Throw ();
		}

		bool IValueTaskSource<bool>.GetResult (short token)
		{
			lock (gate) {
				if (token != next.Version || !outstanding)
					throw new InvalidOperationException ("This MoveNextAsync value has already been consumed or is stale.");
				if (next.GetStatus (token) == ValueTaskSourceStatus.Pending)
					throw new InvalidOperationException ("MoveNextAsync has not completed.");
				try {
					return next.GetResult (token);
				} catch {
					errorObserved = true;
					throw;
				} finally {
					outstanding = false;
				}
			}
		}

		ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus (short token) => next.GetStatus (token);

		void IValueTaskSource<bool>.OnCompleted (Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
			next.OnCompleted (continuation, state, token, flags);
	}
}
