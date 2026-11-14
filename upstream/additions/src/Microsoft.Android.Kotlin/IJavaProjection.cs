#nullable enable
namespace Java.Interop.Kotlin
{
	/// <summary>Exposes the canonical peer represented by an ordinary managed projection.</summary>
	/// <typeparam name="TPeer">The canonical peer contract, not another peer owner.</typeparam>
	public interface IJavaProjection<out TPeer>
	{
		TPeer Peer { get; }
	}
}
