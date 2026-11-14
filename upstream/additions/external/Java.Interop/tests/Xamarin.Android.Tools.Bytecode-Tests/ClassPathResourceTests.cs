using System.IO;
using System.IO.Compression;
using System.Linq;
using System;
using NUnit.Framework;
using Xamarin.Android.Tools.Bytecode;

namespace Xamarin.Android.Tools.BytecodeTests
{
	[TestFixture]
	public class ClassPathResourceTests : ClassFileFixture
	{
		[Test]
		public void InstrumentationResourcesAndMultiReleaseEntriesDoNotBecomeDuplicateJvmClasses ()
		{
			using var buffer = new MemoryStream ();
			using (var zip = new ZipArchive (buffer, ZipArchiveMode.Create, leaveOpen: true)) {
				var assembly = typeof (ClassPathResourceTests).Assembly;
				var resource = assembly.GetManifestResourceNames ().Single (n => n.EndsWith (".PublicClass.class", StringComparison.Ordinal));
				foreach (var name in new [] { "PublicClass.class", "DebugProbesKt.bin",
					"META-INF/versions/9/PublicClass.class", "META-INF/versions/17/PublicClass.class" }) {
					using var source = assembly.GetManifestResourceStream (resource)
						?? throw new InvalidOperationException ("Missing class fixture.");
					using var destination = zip.CreateEntry (name).Open ();
					source.CopyTo (destination);
				}
			}
			buffer.Position = 0;
			var path = new ClassPath ();
			path.Load (buffer, leaveOpen: true);
			Assert.AreEqual (1, path.GetPackages ().Values.Sum (classes => classes.Count));
		}
	}
}
