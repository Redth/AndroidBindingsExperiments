#!/usr/bin/env python3
"""Run small offline C# -> JNI probes against the real newly generated SDK DLLs."""

import argparse
import base64
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET
import zipfile

FILES = Path(__file__).resolve().parent
ROOT = Path("${ANDROID_SOURCE}")
TESTS = FILES / "third-party-sdk-tests"
BODIES = {
    "networking": """
        using var url = Okhttp3.HttpUrl.Get ("https://example.invalid/a%20b");
        Check (url.Host () == "example.invalid" && url.EncodedPath () == "/a%20b", "OkHttp URL parsing");
        using var buffer = new Okio.BufferType ();
        buffer.WriteUtf8 ("roundtrip");
        Check (buffer.ReadUtf8 () == "roundtrip", "Okio native buffer");
        using var builder = new Com.Squareup.Moshi.Moshi.Builder ();
        using var moshi = builder.Build () ?? throw new InvalidOperationException ("Moshi not built.");
        using var textClass = Java.Lang.Class.ForName ("java.lang.String");
        using var adapter = moshi.Adapter (textClass) ?? throw new InvalidOperationException ("No JSON adapter.");
        var typed = new Com.Squareup.Moshi.JsonAdapterView<string> (adapter);
        Check (typed.FromJson ("\\"typed\\"") == "typed", "Moshi generic view JSON read");
        Check (typed.ToJson ("value") == "\\"value\\"", "Moshi generic view JSON write");
        using var retrofitBuilder = new Retrofit2.Retrofit.Builder ();
        retrofitBuilder.BaseUrl ("https://example.invalid/");
        using var retrofit = retrofitBuilder.Build () ?? throw new InvalidOperationException ("Retrofit not built.");
        using var baseUrl = retrofit.BaseUrl ();
        Check (baseUrl?.Host () == "example.invalid", "Retrofit builder without network traffic");
    """,
    "mapbox": """
        using var point = Com.Mapbox.Geojson.Point.FromLngLat (12.5, 48.25) ?? throw new InvalidOperationException ("No point.");
        Check (point.Longitude () == 12.5 && point.Latitude () == 48.25, "Mapbox GeoJSON coordinate model");
        using var builder = new Com.Mapbox.Maps.CameraOptions.Builder ();
        using var zoom = new Java.Lang.Double (7.5);
        builder.Zoom (zoom);
        using var options = builder.Build () ?? throw new InvalidOperationException ("No camera options.");
        using var actual = options.Zoom;
        Check (actual?.DoubleValue () == 7.5, "Mapbox camera builder");
        using var overrideBuilder = new Com.Mapbox.Maps.Extension.Style.Sources.Generated.ModelMaterialOverride.Builder ("material");
        using var material = overrideBuilder.Build ();
        Check (material.Get ("missing") == null && material.Remove ("missing") == null, "Mapbox typed map overloads");
    """,
    "coil": """
        using var builder = new Coil3.Request.ImageRequest.Builder (this);
        using var data = new Java.Lang.String ("offline-image");
        builder.Data (data);
        builder.Size (64, 32);
        using var request = builder.Build ();
        Check (request.Data.ToString () == "offline-image", "Coil request data");
        Check (request.ToString ().Contains ("ImageRequest"), "Coil Kotlin model without an image download");
        using var copyBuilder = new Coil3.Request.ImageRequest.Builder (request);
        using var copy = copyBuilder.Build ();
        Check (copy.Data.ToString () == "offline-image", "Coil request copy");
    """,
    "stripe": """
        using var builder = new Com.Stripe.Android.Model.PaymentMethodCreateParams.CardType.Builder ();
        builder.SetNumber ("4242424242424242");
        using var month = new Java.Lang.Integer (12);
        using var year = new Java.Lang.Integer (2030);
        builder.SetExpiryMonth (month);
        builder.SetExpiryYear (year);
        using var card = builder.Build ();
        Check (card.ToString ().Contains ("4242424242424242"), "Stripe card model using a documented test number");
        Check (typeof (Com.Stripe.Android.Model.IStripeIntent.NextActionDataType.SdkData.Use3DS2.DirectoryServerEncryption) != null,
            "Stripe deeply nested SDK contract");
    """,
}

REVENUECAT = """
        using var code = Com.Revenuecat.Purchases.PurchasesErrorCode.UnknownError ?? throw new InvalidOperationException ("No error code.");
        using var error = new Com.Revenuecat.Purchases.PurchasesError (code, "offline");
        Check (error.Code.Equals (code) && error.Message.Length > 0, "RevenueCat native error model");
        using var period = Com.Revenuecat.Purchases.Models.Period.FactoryInstance.Create ("P1M");
        Check (period.Value == 1, "RevenueCat billing period model");
"""
REVENUECAT_UI = """
        using var dismiss = new ManagedDismiss ();
        using var optionsBuilder = new Com.Revenuecat.Purchases.UI.Revenuecatui.PaywallOptions.Builder (dismiss);
        using var paywallOptions = optionsBuilder.Build ();
        Check (paywallOptions.FontProvider == null, "RevenueCat Paywall options builder");
        using var dismissCallback = paywallOptions.DismissRequest;
        using var dismissed = dismissCallback.Invoke ();
        Check (dismiss.Calls == 1 && dismissed?.Class?.Name == "kotlin.Unit", "RevenueCat Paywall managed dismiss callback");
        using var result = new Com.Revenuecat.Purchases.UI.Revenuecatui.Activity.PaywallResult.Error (error);
        Check (result.ToString ().Contains ("offline"), "RevenueCat Paywall result model");
        using var contract = new Com.Revenuecat.Purchases.UI.Revenuecatui.Customercenter.ShowCustomerCenter ();
        using var unit = contract.ParseResult (0, null);
        Check (unit?.Class?.Name == "kotlin.Unit", "RevenueCat UI Kotlin Unit object bridge");
        contract.ParseResultNative17693033 (0, null);
        Check (true, "RevenueCat UI native void entry remains callable");
        using var amazonBuilder = new Com.Revenuecat.Purchases.Amazon.AmazonConfiguration.Builder (this, "offline-not-configured");
        using var amazon = new Com.Revenuecat.Purchases.Amazon.AmazonConfiguration (amazonBuilder);
        Check (amazon.Class?.Name == "com.revenuecat.purchases.amazon.AmazonConfiguration", "RevenueCat Amazon configuration model");
        using var galaxyBuilder = new Com.Revenuecat.Purchases.Galaxy.GalaxyConfiguration.Builder (this, "offline-not-configured");
        using var galaxy = new Com.Revenuecat.Purchases.Galaxy.GalaxyConfiguration (galaxyBuilder);
        Check (galaxy.Class?.Name == "com.revenuecat.purchases.galaxy.GalaxyConfiguration", "RevenueCat Galaxy configuration model");
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--aot", action="store_true")
    parser.add_argument("--probe-source", type=Path, help="An authored C# probe using OfflineProbeActivity.")
    parser.add_argument("--expected-checks", type=int, help="Exact number of unique checks expected from the authored probe.")
    parser.add_argument("--artifact-assemblies", type=Path, help="Use the verified artifact DLL tree instead of its monolithic generation input.")
    args = parser.parse_args()
    profile = TESTS / args.profile
    outcome = json.loads((profile / "result.json").read_text())
    if outcome["build"]["exitCode"]:
        raise RuntimeError("Bindings must compile before a runtime probe.")
    directory = profile / (("artifact-device-aot" if args.aot else "artifact-device") if args.artifact_assemblies else
                           "device-aot" if args.aot else "device")
    directory.mkdir(exist_ok=True)
    if args.probe_source:
        if not args.probe_source.is_file() or not args.expected_checks or args.expected_checks < 1:
            parser.error("--probe-source requires an existing file and a positive --expected-checks.")
        body = ""
        expected = args.expected_checks
    else:
        body = REVENUECAT + (REVENUECAT_UI if args.profile == "revenuecat" else "") if args.profile.startswith("revenuecat") else BODIES[args.profile]
        expected = body.count("Check (")
    source = """#nullable enable
using System;
using Android.App;
using Android.OS;
using Android.Util;
namespace ThirdPartyProbe {
[Activity (Name = "net.dot.android.thirdparty.ProbeActivity", MainLauncher = true, Exported = true)]
public sealed class ProbeActivity : Activity {
    protected override void OnCreate (Bundle? state) {
        base.OnCreate (state);
        var identity = Intent?.GetStringExtra ("run") ?? "missing";
        int passed = 0;
        void Check (bool result, string name) {
            if (!result) throw new InvalidOperationException (name);
            passed++;
            Log.Info ("ThirdPartyProbe", identity + " PASS " + name);
        }
        try {
""" + body + """
            Log.Info ("ThirdPartyProbe", identity + " DONE " + passed);
        } catch (Exception error) {
            Log.Error ("ThirdPartyProbe", identity + " FAIL " + error);
        }
    }
}
}
"""
    if args.profile == "revenuecat":
        source += """
namespace ThirdPartyProbe {
[Android.Runtime.Register ("net/dot/android/thirdparty/ManagedDismiss")]
public sealed class ManagedDismiss : Java.Lang.Object, Kotlin.Jvm.Functions.IFunction0 {
    public int Calls { get; private set; }
    public Java.Lang.Object? Invoke () {
        Calls++;
        return Kotlin.Unit.Instance;
    }
}
}
"""
    if not args.probe_source:
        (directory / "Probe.cs").write_text(source)
    inputs = json.loads((profile / "resolved/native-inputs.json").read_text())
    minimum = 24
    for row in inputs:
        if Path(row["file"]).suffix == ".aar":
            with zipfile.ZipFile(row["file"]) as archive:
                manifest = ET.fromstring(archive.read("AndroidManifest.xml"))
                sdk = manifest.find("uses-sdk")
                if sdk is not None and sdk.get("{http://schemas.android.com/apk/res/android}minSdkVersion"):
                    minimum = max(minimum, int(sdk.get("{http://schemas.android.com/apk/res/android}minSdkVersion")))
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    application = "net.dot.android.thirdparty." + args.profile.replace("-", "")
    for key, value in {
        "TargetFramework": "net11.0-android", "OutputType": "Exe", "RuntimeIdentifier": "android-arm64",
        "ApplicationId": application, "SupportedOSPlatformVersion": str(minimum),
        "Nullable": "enable", "AllowUnsafeBlocks": "true", "TrimMode": "full",
        "AndroidLinkTool": "r8", "JavaMaximumHeapSize": "4G", "_AndroidUseResolvedNativeGraph": "true",
        "EnableDefaultCompileItems": "false",
    }.items():
        ET.SubElement(properties, key).text = value
    if args.aot:
        ET.SubElement(properties, "PublishAot").text = "true"
    items = ET.SubElement(project, "ItemGroup")
    if args.probe_source:
        ET.SubElement(items, "Compile", Include=str(args.probe_source.resolve()))
        ET.SubElement(items, "Compile", Include=str(ROOT / "tests/BindingProjection/ThirdParty/OfflineProbeActivity.cs"))
    else:
        ET.SubElement(items, "Compile", Include=str(directory / "Probe.cs"))
    binding = next((profile / "bin/Debug/net11.0-android").glob("ThirdParty.*.dll"))
    bindings = [binding]
    if args.artifact_assemblies:
        ownership = json.loads((args.artifact_assemblies / "ownership.json").read_text())
        if ownership["AssemblyGranularity"] != "artifact":
            raise RuntimeError("The requested DLLs are not an artifact-level tree.")
        bindings = [args.artifact_assemblies / (row["Assembly"] + ".dll") for row in ownership["Assemblies"]]
        if not bindings or any(not path.is_file() for path in bindings):
            raise RuntimeError("Incomplete artifact implementation tree.")
    for assembly in bindings:
        ET.SubElement(items, "Reference", Include=str(assembly))
    ET.SubElement(items, "Reference", Include=str(ROOT / "src/Microsoft.Android.Kotlin/bin/Debug/net11.0-android/Microsoft.Android.Kotlin.dll"))
    references = {path.stem: path for path in (profile / "references").glob("*.dll")}
    if args.aot or args.probe_source:
        reader = FILES / "sdk-assembly-references/bin/Release/net10.0/References.dll"
        def dependencies(path):
            command = [str(ROOT / "dotnet-local.sh"), "--roll-forward", "Major", str(reader), str(path)]
            return json.loads(subprocess.check_output(command, cwd=ROOT, text=True))
        selected = set()
        pending = [name for path in bindings for name in dependencies(path)]
        while pending:
            name = pending.pop()
            if name in selected or name not in references:
                continue
            selected.add(name)
            pending.extend(dependencies(references[name]))
        for name in sorted(selected):
            ET.SubElement(items, "Reference", Include=str(references[name]))
    else:
        for reference in references.values():
            ET.SubElement(items, "Reference", Include=str(reference))
    for row in inputs:
        ET.SubElement(items, "AndroidLibrary", Include=row["file"], Bind="false")
    if args.aot or args.probe_source:
        bridge = list((ROOT / "src/Microsoft.Android.Kotlin/obj/Debug/net11.0-android").glob(
            "gradle/*/outputs/aar/Microsoft.Android.Kotlin.aar"))
        if len(bridge) != 1:
            raise RuntimeError("Expected one source-built Kotlin bridge AAR.")
        ET.SubElement(items, "AndroidLibrary", Include=str(bridge[0]), Bind="false")
        owners = json.loads((FILES / "consolidated-corpus/tree/assemblies/ownership.json").read_text())
        groups = {row["MavenGroup"] for row in owners["Assemblies"] if row["Assembly"] in selected}
        baseline = json.loads((FILES / "consolidated-corpus/tree/native-inputs.json").read_text())
        present = {(row["group"], row["artifact"]): row for row in inputs}
        extra_native = []
        for row in baseline:
            if row["group"] not in groups or (row["group"], row["artifact"]) in present:
                continue
            ET.SubElement(items, "AndroidLibrary", Include=row["file"], Bind="false")
            extra_native.append(row["group"] + ":" + row["artifact"] + ":" + row["version"])
    ET.indent(project)
    ET.ElementTree(project).write(directory / "Probe.csproj", encoding="unicode")
    manifest_dir = directory / "Properties"
    manifest_dir.mkdir(exist_ok=True)
    (manifest_dir / "AndroidManifest.xml").write_text("""<manifest xmlns:android="http://schemas.android.com/apk/res/android">
  <application>
    <meta-data android:name="com.google.android.gms.ads.APPLICATION_ID" android:value="ca-app-pub-3940256099942544~3347511713" />
    <meta-data android:name="io.sentry.auto-init" android:value="false" />
  </application>
</manifest>""")
    command = [str(ROOT / "dotnet-local.sh"), "build", str(directory / "Probe.csproj"), "-c", "Release", "-t:Install",
               "-v:q", "-nr:false", "-p:AdbTarget=-s " + args.device]
    with (directory / "build.log").open("w") as log:
        result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
    report = {"profile": args.profile, "configuration": "NativeAOT" if args.aot else "Release",
              "buildCommand": command, "buildExitCode": result.returncode,
              "expectedChecks": expected, "scope": "Offline model/JNI calls; no SDK service credentials or payment/network operations."}
    report["assemblyLayout"] = "artifact" if args.artifact_assemblies else "fixture"
    if args.aot or args.probe_source:
        report["canonicalNativeGroupClosure"] = extra_native
    if args.probe_source:
        report["probeSource"] = str(args.probe_source.resolve())
    if result.returncode:
        (directory / "result.json").write_text(json.dumps(report, indent=2) + "\n")
        raise RuntimeError("Device probe build failed: " + str(directory / "build.log"))
    adb = [str(Path(os.environ["ANDROID_HOME"]) / "platform-tools/adb"), "-s", args.device]
    identity = uuid.uuid4().hex
    launch_command = adb + ["shell", "am", "start", "-S", "-W", "-n",
                            application + "/net.dot.android.thirdparty.ProbeActivity", "--es", "run", identity]
    launch = subprocess.run(launch_command, check=True, capture_output=True, text=True)
    (directory / "launch.log").write_text(launch.stdout + launch.stderr)
    if re.search(r"^Error", launch.stdout, re.MULTILINE):
        raise RuntimeError("The probe activity failed to launch.")
    lines = []
    for _ in range(120):
        output = subprocess.run(adb + ["logcat", "-d", "-v", "raw", "-s", "ThirdPartyProbe:V", "AndroidRuntime:E", "*:S"],
                                capture_output=True, text=True, check=True)
        lines = [line[len(identity) + 1:] for line in output.stdout.splitlines() if line.startswith(identity + " ")]
        if any(line.startswith(("DONE ", "FAIL ")) for line in lines):
            break
        time.sleep(0.5)
    (directory / "logcat.log").write_text(output.stdout + output.stderr)
    report.update({"identity": identity, "results": lines, "passed": sum(line.startswith("PASS ") for line in lines)})
    apk = next((directory / "bin/Release/net11.0-android/android-arm64").glob("*-Signed.apk"))
    report["apkSha256"] = hashlib.sha256(apk.read_bytes()).hexdigest()
    report["bindingSha256"] = hashlib.sha256(binding.read_bytes()).hexdigest()
    report["bindingAssembliesSha256"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in bindings}
    report["launchCommand"] = launch_command
    (directory / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    if report["passed"] != expected or len({line for line in lines if line.startswith("PASS ")}) != expected or not lines or lines[-1] != "DONE " + str(expected):
        raise RuntimeError("SDK runtime probe failed: " + str(directory / "logcat.log"))
    if args.profile.startswith("maplibre"):
        prefix = "ARTIFACT offline-map.png "
        artifacts = [line[len(prefix):] for line in lines if line.startswith(prefix)]
        if len(artifacts) != 1:
            raise RuntimeError("Expected one rendered snapshot artifact.")
        snapshot = base64.b64decode(artifacts[0], validate=True)
        if not snapshot.startswith(b"\x89PNG\r\n\x1a\n"):
            raise RuntimeError("Rendered snapshot is not a valid PNG.")
        (directory / "offline-map.png").write_bytes(snapshot)
    print(args.profile + (" NativeAOT" if args.aot else " Release/R8") + ": " +
          str(expected) + "/" + str(expected) + " offline JNI checks passed.")


if __name__ == "__main__":
    main()
