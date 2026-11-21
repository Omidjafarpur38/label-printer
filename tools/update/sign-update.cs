#:project ../../src/LabelPrinter.Agent.Core
#:property RestorePackagesWithLockFile=false
#:property RuntimeIdentifier=
#:property RuntimeIdentifiers=
// Release-side tool for the signed update manifest (docs/auto-update.md). Not part of the shipped program.
//
//   dotnet run tools/update/sign-update.cs -- keygen --private-out <file>
//       Creates a P-256 key pair. The PRIVATE key goes to the file (never to the screen or a log); the PUBLIC key is
//       printed, to be pasted into src/LabelPrinter.Agent.Core/Updates/TrustedKeys.cs.
//   UPDATE_SIGNING_KEY=<base64 PKCS#8> dotnet run tools/update/sign-update.cs -- sign --installer <exe> --version <v> --out <dir> [--notes "<text>"]
//       Writes agent-update.json and agent-update.sig for that exact installer file.
//   dotnet run tools/update/sign-update.cs -- verify --dir <dir> [--current <version>]
//       Checks the pair against the keys built into the program, the way the program itself will.
using System.Security.Cryptography;
using System.Text;
using LabelPrinter.Agent.Core.Updates;

static string? Arg(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
static int Fail(string m) { Console.Error.WriteLine(m); return 1; }

if (args.Length == 0) return Fail("usage: keygen | sign | verify");
switch (args[0])
{
    case "keygen":
    {
        var outFile = Arg(args, "--private-out") ?? "";
        if (outFile.Length == 0) return Fail("--private-out <file> is required");
        if (File.Exists(outFile)) return Fail("refusing to overwrite " + outFile);
        var (priv, pub) = UpdateManifest.GenerateKeyPair();
        File.WriteAllText(outFile, priv);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine("Private key written to: " + outFile + "  (store it as the GitHub secret UPDATE_SIGNING_KEY, then delete the file)");
        Console.WriteLine("Public key (put it in TrustedKeys.SpkiBase64):");
        Console.WriteLine(pub);
        return 0;
    }
    case "sign":
    {
        var installer = Arg(args, "--installer"); var version = Arg(args, "--version"); var outDir = Arg(args, "--out");
        var notes = Arg(args, "--notes") ?? "";
        var keyText = Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY");
        if (installer is null || version is null || outDir is null) return Fail("--installer, --version and --out are required");
        if (string.IsNullOrWhiteSpace(keyText)) return Fail("UPDATE_SIGNING_KEY is not set");
        if (!AgentVersion.TryParse(version, out _)) return Fail("bad version");
        if (Path.GetFileName(installer) != UpdateManifest.InstallerFileName(version)) return Fail("installer file name must be " + UpdateManifest.InstallerFileName(version));
        var (sha, size) = UpdateManifest.Measure(installer);
        var manifest = UpdateManifest.Create(new UpdateOffer(version, Path.GetFileName(installer), size, sha, DateTimeOffset.UtcNow, notes));
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(keyText.Trim()), out _);
        var sig = UpdateManifest.Sign(manifest, key);
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "agent-update.json"), manifest);
        File.WriteAllText(Path.Combine(outDir, "agent-update.sig"), Convert.ToBase64String(sig));
        Console.WriteLine($"signed {Path.GetFileName(installer)} ({size} bytes, sha256 {sha})");
        return 0;
    }
    case "verify":
    {
        var dir = Arg(args, "--dir"); var current = Arg(args, "--current") ?? "0.0.0";
        if (dir is null) return Fail("--dir is required");
        var manifest = File.ReadAllBytes(Path.Combine(dir, "agent-update.json"));
        var sig = Convert.FromBase64String(File.ReadAllText(Path.Combine(dir, "agent-update.sig"), Encoding.ASCII).Trim());
        try
        {
            var offer = UpdateManifest.Verify(manifest, sig, TrustedKeys.Parsed(), current);
            Console.WriteLine("verified with the built-in key: " + offer.Version + " " + offer.File);
            return 0;
        }
        catch (UpdateRejectedException ex) { return Fail("REFUSED " + ex.Code + ": " + ex.Message); }
    }
    default:
        return Fail("unknown command " + args[0]);
}
