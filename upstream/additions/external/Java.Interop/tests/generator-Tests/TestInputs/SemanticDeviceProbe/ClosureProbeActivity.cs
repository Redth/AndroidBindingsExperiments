using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using Android.OS;
using Android.Runtime;
using Androidx.Appcompat.App;
using Androidx.Emoji2.Emojipicker;
using Androidx.Lifecycle;

namespace BindingCompilerSpike
{
	[Activity (Label = "Regenerated binding closure", MainLauncher = true, Exported = true)]
	[Register ("net/dot/android/bindingcompiler/ClosureProbeActivity")]
	public class ClosureProbeActivity : AppCompatActivity
	{
		readonly List<string> results = [];
		EmojiPickerView? picker;

		protected override void OnCreate (Bundle? savedInstanceState)
		{
			SetTheme (Resource.Style.Theme_AppCompat_Light);
			base.OnCreate (savedInstanceState);
			var window = Window ?? throw new InvalidOperationException ("No application window.");
			window.DecorView.Post (RunChecks);
		}

		void RunChecks ()
		{
			results.Add (ProbeRunIdentity.Value);
			try {
				Run ("one managed assembly owns the regenerated AndroidX closure", () => {
					var assembly = typeof (AppCompatActivity).Assembly;
					Check (typeof (EmojiPickerView).Assembly == assembly, "Picker assembly differs.");
					Check (typeof (Androidx.Fragment.App.FragmentManager).Assembly == assembly, "Fragment assembly differs.");
					Check (typeof (ILifecycleOwner).Assembly == assembly, "Lifecycle assembly differs.");
					Check (typeof (Androidx.Savedstate.SavedStateRegistry).Assembly == assembly, "Saved-state assembly differs.");
				});
				Run ("regenerated Activity Fragment Lifecycle and SavedState contracts", () => {
					Check (SupportFragmentManager != null, "No fragment manager.");
					Check (((ILifecycleOwner) this).Lifecycle != null, "No lifecycle.");
					Check (SavedStateRegistry != null, "No saved-state registry.");
				});
				Run ("Java lifecycle dispatch reaches a managed source-interface implementation", () => {
					using var observer = new ManagedClosureObserver ();
					var lifecycle = ((ILifecycleOwner) this).Lifecycle;
					lifecycle.AddObserver (observer);
					lifecycle.RemoveObserver (observer);
					Check (observer.Creates == 1, "The native lifecycle did not deliver exactly one creation callback.");
				});
				Run ("public default interface method survives an inaccessible native ancestor", () => {
					using var observer = new ManagedClosureObserver ();
					((IDefaultLifecycleObserver) observer).OnStart (this);
				});
				Run ("Kotlin EmojiPicker widget creates and exchanges native property values", () => {
					picker = new EmojiPickerView (this);
					picker.EmojiGridColumns = 7;
					picker.EmojiGridRows = 3;
					Check (picker.EmojiGridColumns == 7 && picker.EmojiGridRows == 3, "Wrong picker properties.");
					SetContentView (picker);
				});
				Run ("regenerated unsigned Kotlin values preserve every JVM sign bit", () => {
					Kotlin.UByte small = (sbyte) -1;
					Kotlin.UShort medium = (short) -1;
					Kotlin.UInt large = -1;
					Kotlin.ULong huge = -1L;
					Check (small.Value == byte.MaxValue && (sbyte) small == -1, "UByte lost sign bits.");
					Check (medium.Value == ushort.MaxValue && (short) medium == -1, "UShort lost sign bits.");
					Check (large.Value == uint.MaxValue && (int) large == -1, "UInt lost sign bits.");
					Check (huge.Value == ulong.MaxValue && (long) huge == -1, "ULong lost sign bits.");
				});
				results.Add ("DONE 6");
				WriteResults ();
				Android.Util.Log.Info ("BindingClosureProbe", ProbeRunIdentity.Value + " DONE 6");
			} catch (Exception ex) {
				results.Add ("FAIL " + ex);
				WriteResults ();
				Android.Util.Log.Error ("BindingClosureProbe", ProbeRunIdentity.Value + " FAIL " + ex);
				throw;
			}
		}

		void Run (string name, Action action)
		{
			action ();
			results.Add ("PASS " + name);
			WriteResults ();
			Android.Util.Log.Info ("BindingClosureProbe", ProbeRunIdentity.Value + " PASS " + name);
		}

		void WriteResults ()
		{
			var directory = FilesDir ?? throw new InvalidOperationException ("No application files directory.");
			File.WriteAllLines (Path.Combine (directory.AbsolutePath, "binding-closure-results.txt"), results);
		}

		static void Check (bool condition, string message)
		{
			if (!condition)
				throw new InvalidOperationException (message);
		}
	}

	[Register ("net/dot/android/bindingcompiler/ManagedClosureObserver")]
	public class ManagedClosureObserver : Java.Lang.Object, IDefaultLifecycleObserver
	{
		public int Creates { get; private set; }

		public void OnCreate (ILifecycleOwner owner) => Creates++;
	}
}
